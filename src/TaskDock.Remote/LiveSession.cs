using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using TaskDock.Core.Dock;

namespace TaskDock.Remote;

/// <summary>One phone's socket: a full snapshot on connect and on resync, then deltas at most every 250 ms.</summary>
sealed class LiveSession(WebSocket ws, Device device, IDockHost host, RemoteActions actions)
{
    const int MaxClientFrame = 16 * 1024;
    static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(250); // at most 4 frames a second

    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly SemaphoreSlim _changed = new(0, 1);
    readonly DeltaEncoder _encoder = new();
    readonly Lock _encoderLock = new();

    public Device Device { get; } = device;

    public void Notify()
    {
        try { _changed.Release(); } catch (SemaphoreFullException) { } // already pending: one wake-up covers both
    }

    public void Abort() => ws.Abort();

    public async Task RunAsync(CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        await SendSnapshot();
        var pump = Pump(cts.Token);
        try
        {
            while (await Receive(ws, MaxClientFrame, cts.Token) is { } text)
            {
                switch (RemoteServer.Frame(text))
                {
                    case { T: "resync" }: await SendSnapshot(); break;
                    // Not awaited: a slow action must not hold up resyncs. Results go back on this socket.
                    case { T: "action", Body: { } body }: _ = Act(body); break;
                }
            }
        }
        finally
        {
            await cts.CancelAsync();
            await pump;
            await Close(ws, WebSocketCloseStatus.NormalClosure, "bye");
        }
    }

    async Task Act(JsonObject body)
    {
        try { await Send("result", await actions.Handle(Device, body)); }
        catch (WebSocketException) { } // the phone left; it resends the same ID and dedup answers
        catch (ObjectDisposedException) { }
    }

    async Task Pump(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _changed.WaitAsync(ct);
                if (host.Snapshot is not { } s) continue;
                JsonObject? delta;
                lock (_encoderLock) delta = _encoder.Delta(s);
                if (delta != null) await Send("delta", delta);
                await Task.Delay(MinGap, ct); // coalesce: whatever changes meanwhile goes in the next frame
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    async Task SendSnapshot()
    {
        if (host.Snapshot is not { } s) return;
        JsonNode body;
        lock (_encoderLock) body = _encoder.Snapshot(s);
        await Send("snapshot", body);
    }

    public async Task Send(string type, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["t"] = type, ["body"] = body }.ToJsonString());
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    public static async Task<string?> Receive(WebSocket ws, int max, CancellationToken ct)
    {
        var buf = new byte[4096];
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > max) { await Close(ws, WebSocketCloseStatus.MessageTooBig, "frame too big"); return null; }
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
    }

    public static async Task Close(WebSocket ws, WebSocketCloseStatus status, string why)
    {
        try { if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) await ws.CloseOutputAsync(status, why, CancellationToken.None); }
        catch (WebSocketException) { }
    }
}
