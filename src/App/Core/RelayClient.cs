using System.IO;
using System.Net.WebSockets;
using System.Text;
using PomocZdalna.Shared;

namespace PomocZdalna.App.Core;

/// <summary>Klient WebSocket do serwera relay. Zdarzenia wywoływane są z wątku tła.</summary>
public sealed class RelayClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _closedRaised;

    public event Action<Msg>? JsonReceived;
    public event Action<byte[]>? BinaryReceived;
    public event Action<string>? Closed;

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(new Uri(url), ct);
        _ = Task.Run(ReceiveLoop);
    }

    public Task<bool> SendAsync(Msg m) =>
        SendRawAsync(Encoding.UTF8.GetBytes(Proto.Serialize(m)), WebSocketMessageType.Text);

    public Task<bool> SendBinaryAsync(byte[] data) => SendRawAsync(data, WebSocketMessageType.Binary);

    private async Task<bool> SendRawAsync(byte[] data, WebSocketMessageType type)
    {
        if (!IsOpen) return false;
        try
        {
            await _sendLock.WaitAsync(_cts.Token);
            try { await _ws.SendAsync(new ArraySegment<byte>(data), type, true, _cts.Token); }
            finally { _sendLock.Release(); }
            return true;
        }
        catch { return false; }
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[32 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        RaiseClosed("Połączenie z serwerem zamknięte");
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    BinaryReceived?.Invoke(ms.ToArray());
                }
                else
                {
                    var m = Proto.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                    if (m is not null) JsonReceived?.Invoke(m);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RaiseClosed(ex.Message); return; }
        RaiseClosed("Rozłączono");
    }

    private void RaiseClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws.Dispose(); } catch { }
    }
}
