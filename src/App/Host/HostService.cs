using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PomocZdalna.App.Core;
using PomocZdalna.Shared;

namespace PomocZdalna.App.Host;

public sealed record ConsentResult(bool Control, bool Commands);

/// <summary>
/// Strona klienta: rejestruje się na serwerze, pilnuje hasła, pyta użytkownika o zgodę,
/// a po akceptacji strumieniuje ekran i wykonuje polecenia/sterowanie od technika.
/// </summary>
public sealed class HostService
{
    private readonly Settings _settings;
    private readonly ScreenCapturer _capturer = new();
    private readonly SemaphoreSlim _cmdLock = new(1, 1);

    private CancellationTokenSource? _runCts;
    private RelayClient? _client;
    private CancellationTokenSource? _pendingCts;
    private CancellationTokenSource? _sessionCts;
    private volatile bool _active;
    private volatile bool _allowControl;
    private volatile bool _allowCommands;

    public string Password { get; private set; } = NewPassword();
    public string? Id { get; private set; }
    public bool Running => _runCts is not null;
    public bool SessionActive => _active;

    public event Action<string>? Status;
    public event Action? IdChanged;
    public event Action<string?>? SessionChanged; // nazwa technika albo null

    public Func<string, CancellationToken, Task<ConsentResult?>>? AskConsent { get; set; }

    public HostService(Settings settings) => _settings = settings;

    public static string NewPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // bez 0/O, 1/I
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++) sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        return sb.ToString();
    }

    public void RegeneratePassword() => Password = NewPassword();

    public void Start()
    {
        if (_runCts is not null) return;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        _ = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        _runCts?.Cancel();
        _runCts = null;
        EndSessionLocal();
        _client?.Dispose();
        _client = null;
        Id = null;
        IdChanged?.Invoke();
        Status?.Invoke("Udostępnianie wyłączone");
    }

    /// <summary>Klient kończy aktywną sesję z poziomu aplikacji.</summary>
    public async Task EndSessionByUserAsync()
    {
        if (!_active) return;
        var c = _client;
        EndSessionLocal();
        if (c is not null) await c.SendAsync(new Msg { Type = MsgType.Bye });
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new RelayClient();
            try
            {
                Status?.Invoke("Łączenie z serwerem...");
                client.JsonReceived += OnJson;
                client.Closed += _ => closed.TrySetResult();
                _client = client;
                await client.ConnectAsync(_settings.ServerUrl, ct);
                await client.SendAsync(new Msg { Type = MsgType.Register, Id = _settings.HostId });
                await closed.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Status?.Invoke("Błąd połączenia: " + ex.Message); }
            finally
            {
                EndSessionLocal();
                client.Dispose();
                if (ReferenceEquals(_client, client)) _client = null;
            }

            if (ct.IsCancellationRequested) break;
            Id = null;
            IdChanged?.Invoke();
            Status?.Invoke("Brak połączenia z serwerem – ponawiam za 5 s...");
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void OnJson(Msg m)
    {
        switch (m.Type)
        {
            case MsgType.Registered:
                Id = m.Id;
                _settings.HostId = m.Id;
                _settings.Save();
                IdChanged?.Invoke();
                Status?.Invoke("Gotowy – podaj technikowi ID i hasło");
                break;

            case MsgType.ConnectReq:
                _ = Task.Run(() => HandleRequestAsync(m));
                break;

            case MsgType.Ended:
                _pendingCts?.Cancel();
                EndSessionLocal();
                break;

            case MsgType.Mouse when _active && _allowControl:
                InputInjector.Mouse(m);
                break;

            case MsgType.Key when _active && _allowControl:
                InputInjector.Key(m);
                break;

            case MsgType.Cmd when _active && _allowCommands:
                _ = Task.Run(() => HandleCommandAsync(m));
                break;
        }
    }

    private async Task HandleRequestAsync(Msg m)
    {
        var client = _client;
        if (client is null) return;

        if (_active)
        {
            await client.SendAsync(new Msg { Type = MsgType.Reject, Reason = Reasons.Busy });
            return;
        }

        var given = Encoding.UTF8.GetBytes((m.Pass ?? "").Trim().ToUpperInvariant());
        var real = Encoding.UTF8.GetBytes(Password);
        if (!CryptographicOperations.FixedTimeEquals(given, real))
        {
            Status?.Invoke("Odrzucono próbę połączenia (złe hasło)");
            await client.SendAsync(new Msg { Type = MsgType.Reject, Reason = Reasons.BadPassword });
            return;
        }

        var name = m.Name ?? "technik";
        ConsentResult? res = null;
        _pendingCts = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        try
        {
            if (AskConsent is not null) res = await AskConsent(name, _pendingCts.Token);
        }
        catch (OperationCanceledException) { }

        if (res is null)
        {
            await client.SendAsync(new Msg { Type = MsgType.Reject, Reason = Reasons.Denied });
            Status?.Invoke("Połączenie odrzucone");
            return;
        }

        _allowControl = res.Control;
        _allowCommands = res.Commands;
        _sessionCts = new CancellationTokenSource();
        _active = true;
        await client.SendAsync(new Msg { Type = MsgType.Accept, Control = res.Control, Commands = res.Commands });
        SessionChanged?.Invoke(name);
        Status?.Invoke($"Trwa sesja zdalna: {name}");
        _ = Task.Run(() => CaptureLoopAsync(client, _sessionCts.Token));
    }

    private async Task CaptureLoopAsync(RelayClient client, CancellationToken ct)
    {
        byte[]? last = null;
        var lastSent = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            var jpg = _capturer.Capture();
            if (jpg is not null && (last is null || lastSent.ElapsedMilliseconds > 2000 || !jpg.AsSpan().SequenceEqual(last)))
            {
                if (!await client.SendBinaryAsync(jpg)) break;
                last = jpg;
                lastSent.Restart();
            }
            var wait = 100 - (int)sw.ElapsedMilliseconds; // ~10 klatek/s
            if (wait > 0)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleCommandAsync(Msg m)
    {
        var client = _client;
        var ct = _sessionCts?.Token ?? CancellationToken.None;
        if (client is null || string.IsNullOrWhiteSpace(m.Cmd)) return;

        await _cmdLock.WaitAsync();
        try
        {
            var (output, exit) = await CommandRunner.RunAsync(m.Shell ?? "ps", m.Cmd, ct);
            await client.SendAsync(new Msg { Type = MsgType.CmdResult, Cmd = m.Cmd, Output = output, Exit = exit });
        }
        finally { _cmdLock.Release(); }
    }

    private void EndSessionLocal()
    {
        bool was = _active;
        _active = false;
        _allowControl = false;
        _allowCommands = false;
        _sessionCts?.Cancel();
        InputInjector.ReleaseAll();
        if (was)
        {
            SessionChanged?.Invoke(null);
            Status?.Invoke(Id is null ? "Brak połączenia" : "Gotowy – podaj technikowi ID i hasło");
        }
    }
}
