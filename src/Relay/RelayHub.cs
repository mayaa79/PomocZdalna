using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using PomocZdalna.Shared;

namespace PomocZdalna.Relay;

/// <summary>Jedno połączenie WebSocket z bezpiecznym (serializowanym) wysyłaniem.</summary>
public sealed class Conn
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _buf = new byte[16 * 1024];

    public string Ip { get; }

    public Conn(WebSocket ws, string ip) { _ws = ws; Ip = ip; }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public Task SendAsync(Msg m, CancellationToken ct = default) =>
        SendRawAsync(Encoding.UTF8.GetBytes(Proto.Serialize(m)), WebSocketMessageType.Text, ct);

    public async Task SendRawAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct = default)
    {
        if (!IsOpen) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (IsOpen) await _ws.SendAsync(data, type, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Odbiera całą wiadomość (z limitem rozmiaru). null = połączenie zamknięte.</summary>
    public async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveAsync(int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(_buf.AsMemory(), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(_buf, 0, r.Count);
            if (ms.Length > maxBytes)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None); } catch { }
                return null;
            }
            if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
        }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }
}

/// <summary>
/// Serwer pośredniczący (relay). Hosty (komputery klientów) rejestrują się i dostają 9-cyfrowe ID.
/// Technik podaje ID + hasło; serwer przekazuje prośbę do hosta, host sam weryfikuje hasło
/// i prosi użytkownika o zgodę. Po akceptacji serwer tylko przekazuje dane między stronami.
/// </summary>
public sealed class RelayHub
{
    private sealed class Host
    {
        public string Id = "";
        public Conn Conn = null!;
        public Conn? Pending;
        public Conn? Active;
        public string TechName = "";
        public DateTime Started;
        public readonly object Gate = new();
    }

    private const int MaxFails = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Host> _hosts = new();
    private readonly ConcurrentDictionary<string, (int Fails, DateTime Since)> _fails = new();
    private readonly ILogger<RelayHub> _log;
    private readonly SessionLog _audit;

    public RelayHub(ILogger<RelayHub> log, SessionLog audit) { _log = log; _audit = audit; }

    public async Task HandleAsync(WebSocket ws, string ip, CancellationToken ct)
    {
        var c = new Conn(ws, ip);
        try
        {
            using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstCts.CancelAfter(TimeSpan.FromSeconds(15));

            (WebSocketMessageType Type, byte[] Data)? first;
            try { first = await c.ReceiveAsync(4096, firstCts.Token); }
            catch (OperationCanceledException) { return; }

            if (first is null || first.Value.Type != WebSocketMessageType.Text) return;
            var msg = Proto.Parse(Encoding.UTF8.GetString(first.Value.Data));
            if (msg is null) return;

            switch (msg.Type)
            {
                case MsgType.Register: await RunHostAsync(c, msg, ct); break;
                case MsgType.Connect: await RunTechAsync(c, msg, ct); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { _log.LogError(ex, "Błąd obsługi połączenia {Ip}", ip); }
        finally { await c.CloseAsync(); }
    }

    // ---------------------------------------------------------------- HOST

    private async Task RunHostAsync(Conn c, Msg reg, CancellationToken ct)
    {
        var host = new Host { Conn = c };
        var wanted = reg.Id;
        host.Id = wanted is { Length: 9 } && wanted.All(char.IsAsciiDigit) && wanted[0] != '0' ? wanted : NewId();

        if (!_hosts.TryAdd(host.Id, host))
        {
            // to samo ID zajęte: przejmij je, jeśli stare połączenie jest martwe albo z tego samego IP (restart/reconnect)
            if (_hosts.TryGetValue(host.Id, out var old) && (!old.Conn.IsOpen || old.Conn.Ip == c.Ip))
            {
                _hosts[host.Id] = host;
                _ = old.Conn.CloseAsync();
            }
            else
            {
                host.Id = NewId();
                _hosts[host.Id] = host;
            }
        }

        _log.LogInformation("Host zarejestrowany: {Id} ({Ip})", host.Id, c.Ip);
        await c.SendAsync(new Msg { Type = MsgType.Registered, Id = host.Id }, ct);

        try
        {
            while (true)
            {
                var msg = await c.ReceiveAsync(4 * 1024 * 1024, ct);
                if (msg is null) break;
                var (type, data) = msg.Value;

                if (type == WebSocketMessageType.Binary)
                {
                    var a = host.Active;
                    if (a is not null) await Safe(a.SendRawAsync(data, WebSocketMessageType.Binary, ct));
                    continue;
                }

                var m = Proto.Parse(Encoding.UTF8.GetString(data));
                if (m is null) continue;

                switch (m.Type)
                {
                    case MsgType.Accept:
                    {
                        Conn? tech;
                        lock (host.Gate)
                        {
                            tech = host.Pending;
                            if (tech is not null)
                            {
                                host.Active = tech;
                                host.Pending = null;
                                host.Started = DateTime.UtcNow;
                            }
                        }
                        if (tech is not null)
                        {
                            _fails.TryRemove(tech.Ip, out _);
                            _audit.Write($"SESSION_START host={host.Id} hostIp={c.Ip} tech=\"{host.TechName}\" techIp={tech.Ip} control={m.Control} commands={m.Commands}");
                            await Safe(tech.SendAsync(new Msg { Type = MsgType.Accept, Control = m.Control, Commands = m.Commands }, ct));
                        }
                        break;
                    }
                    case MsgType.Reject:
                    {
                        Conn? tech;
                        lock (host.Gate) { tech = host.Pending; host.Pending = null; }
                        if (tech is not null)
                        {
                            if (m.Reason == Reasons.BadPassword) RegisterFail(tech.Ip);
                            _audit.Write($"SESSION_REJECTED host={host.Id} techIp={tech.Ip} reason={m.Reason}");
                            await Safe(tech.SendAsync(new Msg { Type = MsgType.Reject, Reason = m.Reason }, ct));
                        }
                        break;
                    }
                    case MsgType.Bye: // host kończy aktywną sesję
                    {
                        Conn? tech;
                        lock (host.Gate) { tech = host.Active; host.Active = null; }
                        if (tech is not null)
                        {
                            _audit.Write($"SESSION_END host={host.Id} by=host duration={Duration(host)}");
                            await Safe(tech.SendAsync(new Msg { Type = MsgType.Ended, Reason = "Klient zakończył sesję" }, ct));
                        }
                        break;
                    }
                    case MsgType.CmdResult:
                    {
                        var a = host.Active;
                        if (a is not null) await Safe(a.SendRawAsync(data, WebSocketMessageType.Text, ct));
                        break;
                    }
                }
            }
        }
        finally
        {
            _hosts.TryRemove(new KeyValuePair<string, Host>(host.Id, host));
            Conn? a, p;
            lock (host.Gate) { a = host.Active; p = host.Pending; host.Active = null; host.Pending = null; }
            var ended = new Msg { Type = MsgType.Ended, Reason = "Komputer klienta rozłączył się" };
            if (a is not null)
            {
                _audit.Write($"SESSION_END host={host.Id} by=host-disconnect duration={Duration(host)}");
                await Safe(a.SendAsync(ended));
            }
            if (p is not null) await Safe(p.SendAsync(ended));
            _log.LogInformation("Host rozłączony: {Id}", host.Id);
        }
    }

    // ---------------------------------------------------------------- TECHNIK

    private async Task RunTechAsync(Conn c, Msg req, CancellationToken ct)
    {
        if (IsBlocked(c.Ip))
        {
            await Safe(c.SendAsync(new Msg { Type = MsgType.Error, Reason = "Zbyt wiele nieudanych prób. Spróbuj ponownie później." }, ct));
            return;
        }

        if (req.Id is null || !_hosts.TryGetValue(req.Id, out var host))
        {
            RegisterFail(c.Ip);
            await Safe(c.SendAsync(new Msg { Type = MsgType.Error, Reason = "Nie znaleziono komputera o podanym ID." }, ct));
            return;
        }

        var name = (req.Name ?? "technik").Trim();
        if (name.Length > 40) name = name[..40];

        bool busy = false;
        lock (host.Gate)
        {
            if (host.Pending is not null || host.Active is not null) busy = true;
            else { host.Pending = c; host.TechName = name; }
        }
        if (busy)
        {
            await Safe(c.SendAsync(new Msg { Type = MsgType.Reject, Reason = Reasons.Busy }, ct));
            return;
        }

        _audit.Write($"CONNECT_REQUEST host={host.Id} tech=\"{name}\" techIp={c.Ip}");
        await Safe(host.Conn.SendAsync(new Msg { Type = MsgType.ConnectReq, Pass = req.Pass, Name = name }, ct));

        // limit czasu na decyzję klienta
        _ = Task.Run(async () =>
        {
            await Task.Delay(ConsentTimeout);
            bool timedOut;
            lock (host.Gate)
            {
                timedOut = ReferenceEquals(host.Pending, c);
                if (timedOut) host.Pending = null;
            }
            if (timedOut)
            {
                await Safe(c.SendAsync(new Msg { Type = MsgType.Error, Reason = "Klient nie odpowiedział na prośbę o połączenie." }));
                await Safe(host.Conn.SendAsync(new Msg { Type = MsgType.Ended }));
            }
        });

        try
        {
            while (true)
            {
                var msg = await c.ReceiveAsync(64 * 1024, ct);
                if (msg is null) break;
                if (msg.Value.Type != WebSocketMessageType.Text) continue;

                var m = Proto.Parse(Encoding.UTF8.GetString(msg.Value.Data));
                if (m is null) continue;
                if (m.Type == MsgType.Bye) break;

                // tylko aktywna (zaakceptowana) sesja może sterować hostem
                if (m.Type is MsgType.Mouse or MsgType.Key or MsgType.Cmd && ReferenceEquals(host.Active, c))
                    await Safe(host.Conn.SendRawAsync(msg.Value.Data, WebSocketMessageType.Text, ct));
            }
        }
        finally
        {
            bool wasActive = false, wasPending = false;
            lock (host.Gate)
            {
                if (ReferenceEquals(host.Active, c)) { host.Active = null; wasActive = true; }
                if (ReferenceEquals(host.Pending, c)) { host.Pending = null; wasPending = true; }
            }
            if (wasActive || wasPending)
            {
                if (wasActive) _audit.Write($"SESSION_END host={host.Id} by=tech duration={Duration(host)}");
                await Safe(host.Conn.SendAsync(new Msg { Type = MsgType.Ended }));
            }
        }
    }

    // ---------------------------------------------------------------- POMOCNICZE

    private string NewId()
    {
        while (true)
        {
            var id = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString();
            if (!_hosts.ContainsKey(id)) return id;
        }
    }

    private bool IsBlocked(string ip)
    {
        if (!_fails.TryGetValue(ip, out var f)) return false;
        if (DateTime.UtcNow - f.Since > FailWindow) { _fails.TryRemove(ip, out _); return false; }
        return f.Fails >= MaxFails;
    }

    private void RegisterFail(string ip)
    {
        _fails.AddOrUpdate(ip,
            _ => (1, DateTime.UtcNow),
            (_, f) => DateTime.UtcNow - f.Since > FailWindow ? (1, DateTime.UtcNow) : (f.Fails + 1, f.Since));
        _audit.Write($"AUTH_FAIL ip={ip}");
    }

    private static string Duration(Host h) => h.Started == default ? "0s" : $"{(DateTime.UtcNow - h.Started).TotalSeconds:F0}s";

    private static async Task Safe(Task t)
    {
        try { await t; } catch { }
    }
}
