using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskDock.Core.Claude;
using TaskDock.Core.Protocol;
using TaskDock.Core.Rules;
using TaskDock.Core.Tiles;

namespace TaskDock.Hub;

public sealed class HubOptions
{
    public string DataDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskDock");
    /// <summary>47820, falling back through 47829. 0 lets the OS pick (tests).</summary>
    public int BasePort { get; init; } = 47820;
    public int PortSpan { get; init; } = 10;
    public TimeSpan HoldWindow { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>e.g. "chrome-extension://&lt;fixed id&gt;". Any other Origin is refused.</summary>
    public IReadOnlyCollection<string> AllowedOrigins { get; init; } = [];
    public Action<ILoggingBuilder>? Logging { get; init; }
    /// <summary>Windows' hung-window check for a process's main window. The App supplies it (Platform owns P/Invoke).</summary>
    public Func<int, bool> IsHung { get; init; } = _ => false;
    public Func<int, bool> IsAlive { get; init; } = DefaultIsAlive;
    /// <summary>Unity's main thread counts as blocked after this much silence (background throttling needs headroom).</summary>
    public TimeSpan UnityBlockedAfter { get; init; } = TimeSpan.FromSeconds(5);

    static bool DefaultIsAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// Local server inside the widget process: 127.0.0.1 only, token + Host + Origin checks, never answers CORS.
/// Turns frames into StateEvents on the sink and holds PermissionRequest calls in the ApprovalBroker.
/// </summary>
public sealed class HubServer(HubOptions options, ChannelWriter<StateEvent> sink, ApprovalBroker broker) : IAsyncDisposable
{
    const int MaxFrame = 64 * 1024;
    const int MaxHookBody = 1024 * 1024;

    readonly ClaudeSessions _claude = new();
    readonly ConcurrentDictionary<string, Connection> _conns = new();
    WebApplication? _app;

    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public string HubJsonPath => Path.Combine(options.DataDir, "hub.json");

    public async Task StartAsync()
    {
        Directory.CreateDirectory(options.DataDir);
        Token = ReadSavedToken() ?? NewToken();

        var ports = options.BasePort == 0 ? [0] : Enumerable.Range(options.BasePort, options.PortSpan);
        foreach (var port in ports)
        {
            var app = Build(port);
            try { await app.StartAsync(); }
            catch (IOException) { await app.DisposeAsync(); continue; } // port taken: try the next
            _app = app;
            Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            WriteHubJson();
            return;
        }
        throw new IOException($"No free port in {options.BasePort}-{options.BasePort + options.PortSpan - 1}");
    }

    public void RotateToken()
    {
        Token = NewToken();
        WriteHubJson();
    }

    public async Task<ResultBody> SendCommandAsync(string tileKey, string action, JsonElement? args = null)
    {
        if (!_conns.TryGetValue(tileKey, out var c)) return new ResultBody("", false, "not connected");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ResultBody>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Results[id] = tcs;
        try
        {
            await c.Send("command", new CommandBody(id, action, args));
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { return new ResultBody(id, false, ex.Message); }
        finally { c.Results.TryRemove(id, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }

    WebApplication Build(int port)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        options.Logging?.Invoke(b.Logging);
        b.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.AddServerHeader = false;
        });
        var app = b.Build();

        app.Use(async (ctx, next) =>
        {
            // DNS rebinding: only our own loopback names.
            var host = ctx.Request.Host.Value;
            if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") { ctx.Response.StatusCode = 403; return; }
            // Never answer preflights, never send CORS headers.
            if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 403; return; }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !options.AllowedOrigins.Contains(origin)) { ctx.Response.StatusCode = 403; return; }
            await next();
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/health", () => Results.Json(new { ok = true, protocol = Wire.Version }));
        app.MapPost("/hooks/claude/{route}", (Delegate)ClaudeHook);
        app.Map("/ws", WebSocketEndpoint);
        return app;
    }

    // ---------- Claude Code HTTP hooks ----------

    async Task<IResult> ClaudeHook(HttpContext ctx)
    {
        if (!TokenEquals(ctx.Request.Headers[ClaudeHooks.TokenHeader].ToString())) return Results.StatusCode(401);
        if (ctx.Request.ContentLength > MaxHookBody) return Results.StatusCode(413);

        JsonElement p;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            p = doc.RootElement.Clone();
        }
        catch (JsonException) { return Results.BadRequest(); }
        if (p.ValueKind != JsonValueKind.Object) return Results.BadRequest();

        StateEvent? e;
        lock (_claude) e = _claude.Apply(p);
        if (e != null) sink.TryWrite(e);

        // A turn that ended or moved on was answered in the terminal: Claude Code never closes the held request (spike S1),
        // so drop the dead card now instead of at the end of the hold. Not on PreToolUse: a parallel call can arrive mid-hold.
        var evt = p.GetProperty("hook_event_name").GetString();
        if (evt is "Stop" or "StopFailure" or "UserPromptSubmit" or "SessionEnd" && e != null) broker.ResolveSession(e.Instance, ApprovalChoice.Terminal);

        if (e == null || evt != "PermissionRequest") return Results.Ok();

        var now = DateTimeOffset.UtcNow;
        var item = new PendingApproval(
            Guid.NewGuid().ToString("N"), e.Instance,
            p.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "?" : "?",
            p.TryGetProperty("tool_input", out var ti) ? ClaudeSessions.ToolPreview(ti) : null,
            p.TryGetProperty("tool_input", out ti) ? JsonSerializer.Serialize(ti, new JsonSerializerOptions { WriteIndented = true }) : "{}",
            p.TryGetProperty("permission_suggestions", out var ps) ? ps : null,
            now + options.HoldWindow, now);

        var decision = await broker.Hold(item, options.HoldWindow, ctx.RequestAborted);

        StateEvent? after;
        lock (_claude)
            after = decision is null || decision.Value.Choice == ApprovalChoice.Terminal
                ? _claude.HandedToTerminal(item.SessionId)
                : _claude.Resumed(item.SessionId);
        if (after != null) sink.TryWrite(after);

        // No decision (Terminal button, hold timeout, request dropped) = empty 200: the normal flow takes over.
        if (decision is not { } d || d.Choice == ApprovalChoice.Terminal) return Results.Ok();
        return Results.Json(DecisionJson(d.Choice, d.Reason, item.Suggestions));
    }

    public static JsonObject DecisionJson(ApprovalChoice choice, string? reason, JsonElement? suggestions)
    {
        var decision = new JsonObject { ["behavior"] = choice == ApprovalChoice.Deny ? "deny" : "allow" };
        if (choice == ApprovalChoice.Deny && !string.IsNullOrWhiteSpace(reason)) decision["message"] = reason.Trim();
        if (choice == ApprovalChoice.AlwaysAllow && suggestions is { ValueKind: JsonValueKind.Array } s)
            decision["updatedPermissions"] = JsonNode.Parse(s.GetRawText());
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        };
    }

    // ---------- WebSocket adapters ----------

    async Task WebSocketEndpoint(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var fromBrowser = ctx.Request.Headers.Origin.Count > 0; // already checked against the allowlist
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        helloCts.CancelAfter(TimeSpan.FromSeconds(5));
        var first = await Receive(ws, helloCts.Token);
        var env = first == null ? null : TryParse(first);
        var hello = env is { V: Wire.Version, Type: "hello" } ? env.Body<HelloBody>() : null;
        if (hello == null || string.IsNullOrWhiteSpace(hello.Adapter) || string.IsNullOrWhiteSpace(hello.Instance)
            || (!fromBrowser && !TokenEquals(hello.Token)))
        {
            await Close(ws, WebSocketCloseStatus.PolicyViolation, "bad hello");
            return;
        }

        var c = new Connection(ws, hello);
        if (_conns.TryGetValue(c.Key, out var old)) old.Ws.Abort();
        _conns[c.Key] = c;
        var heartbeatMs = hello.Adapter == "unity" ? 1000 : 5000;
        await c.Send("welcome", new WelcomeBody(heartbeatMs, false));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var watchdog = Watchdog(c, TimeSpan.FromMilliseconds(heartbeatMs * 3), cts.Token);
        var clean = false;
        try
        {
            while (await Receive(ws, cts.Token) is { } text)
            {
                c.LastSeen = DateTimeOffset.UtcNow;
                var f = TryParse(text);
                switch (f?.Type)
                {
                    case "state" when f.Body<StateBody>() is { } s:
                        c.Last = s with { Progress = s.Progress is { } pr ? Math.Clamp(pr, 0, 1) : null };
                        Publish(c);
                        break;
                    case "heartbeat" when c.Adapter is "unity" or "visualstudio": // re-check hung / blocked on every beat
                        c.PulseAgeMs = f.Body<HeartbeatBody>()?.PulseAgeMs;
                        Publish(c);
                        break;
                    case "result" when f.Body<ResultBody>() is { } r:
                        if (c.Results.TryGetValue(r.Id, out var tcs)) tcs.TrySetResult(r);
                        break;
                    case "bye":
                        clean = true;
                        break;
                }
                if (clean) break;
            }
        }
        catch (Exception) when (!clean) { /* socket died; handled below */ }
        finally
        {
            await cts.CancelAsync();
            await watchdog;
            _conns.TryRemove(new KeyValuePair<string, Connection>(c.Key, c));
        }

        if (clean || c.Adapter is not ("unity" or "visualstudio"))
        {
            Emit(c, "Offline", null, null); // bye: the tile goes Offline immediately
            if (clean) await Close(ws, WebSocketCloseStatus.NormalClosure, "bye");
            return;
        }

        // Unity goes silent during a domain reload: keep "Reloading" (with elapsed time) until the new hello arrives.
        if (c.Adapter == "unity" && c.Last?.State.StartsWith("Reloading", StringComparison.OrdinalIgnoreCase) == true) { _ = WatchReload(c); return; }
        await Task.Delay(300); // a killed editor drops the socket a moment before its process is gone
        Emit(c, c.Pid is { } pid && !options.IsAlive(pid) ? "Closed" : "Offline", null, null);
    }

    /// <summary>
    /// The plan's "reading the silence" rules for Unity, applied on every state and heartbeat:
    /// hung window → Not responding; main-thread tick older than the limit (not reloading or building) → Main thread blocked.
    /// </summary>
    void Publish(Connection c)
    {
        if (c.Last is not { } s) return;
        string state = s.State, detail = s.Detail ?? "";
        if (c.Adapter == "visualstudio" && c.Pid is { } vsPid && options.IsHung(vsPid)) state = "Not responding";
        if (c.Adapter == "unity")
        {
            var expectedSilence = s.State.StartsWith("Reloading", StringComparison.OrdinalIgnoreCase)
                || s.State.StartsWith("Building", StringComparison.OrdinalIgnoreCase);
            if (c.Pid is { } pid && options.IsHung(pid)) state = "Not responding";
            else if (!expectedSilence && c.PulseAgeMs is { } age && age > options.UnityBlockedAfter.TotalMilliseconds)
            {
                state = "Main thread blocked";
                detail = $"No editor tick for {age / 1000} s" + (s.State == "Idle" ? "" : $" · {s.State}");
            }
        }
        Emit(c, state, detail.Length == 0 ? null : detail, state == s.State ? s.Progress : null);
    }

    void Emit(Connection c, string state, string? detail, double? progress)
    {
        var items = state == c.Last?.State ? c.Last.Items : null; // synthetic states (blocked, offline) carry no items
        var key = (state, detail, progress, items?.GetRawText());
        if (c.Emitted == key) return;
        c.Emitted = key;
        sink.TryWrite(new StateEvent(c.Adapter, c.Instance, state, c.Last?.Label ?? c.Instance, detail, progress, c.Match, c.Pid,
            Connected: !c.AnyEmitted, Items: items));
        c.AnyEmitted = true;
    }

    async Task WatchReload(Connection c)
    {
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(10))
        {
            await Task.Delay(1000);
            if (_conns.ContainsKey(c.Key)) return; // the new domain said hello
            var secs = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
            if (c.Pid is { } pid && !options.IsAlive(pid)) { Emit(c, "Closed", null, null); return; }
            if (c.Pid is { } p2 && options.IsHung(p2)) Emit(c, "Not responding", $"Reloading for {secs} s", null);
            else Emit(c, c.Last!.State, $"{secs} s", null);
        }
        Emit(c, "Offline", "Never came back from a domain reload", null);
    }

    static async Task Watchdog(Connection c, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (DateTimeOffset.UtcNow - c.LastSeen > limit) { c.Ws.Abort(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    static async Task<string?> Receive(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > MaxFrame) { await Close(ws, WebSocketCloseStatus.MessageTooBig, "frame too big"); return null; }
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
    }

    static Envelope? TryParse(string text)
    {
        try { return Wire.Parse(text); } catch (JsonException) { return null; }
    }

    static async Task Close(WebSocket ws, WebSocketCloseStatus status, string why)
    {
        // CloseOutputAsync: don't wait for the peer's close reply, which a dying adapter may never send.
        try { if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) await ws.CloseOutputAsync(status, why, CancellationToken.None); }
        catch (WebSocketException) { }
    }

    sealed class Connection(WebSocket ws, HelloBody hello)
    {
        readonly SemaphoreSlim _sendLock = new(1, 1);
        long _seq;
        public WebSocket Ws { get; } = ws;
        public string Adapter { get; } = hello.Adapter;
        public string Instance { get; } = hello.Instance;
        public string Key => Tile.MakeKey(Adapter, Instance);
        public IReadOnlyDictionary<string, string> Match { get; } = hello.Match ?? new Dictionary<string, string>();
        public int? Pid { get; } = hello.Pid;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public StateBody? Last { get; set; }
        public int? PulseAgeMs { get; set; }
        public (string, string?, double?, string?)? Emitted { get; set; }
        public bool AnyEmitted { get; set; }
        public ConcurrentDictionary<string, TaskCompletionSource<ResultBody>> Results { get; } = new();

        public async Task Send<T>(string type, T body)
        {
            var bytes = Encoding.UTF8.GetBytes(Wire.Frame(type, Interlocked.Increment(ref _seq), null, body));
            await _sendLock.WaitAsync();
            try { await Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _sendLock.Release(); }
        }
    }

    // ---------- token + hub.json ----------

    bool TokenEquals(string? given) =>
        given != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token));

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    string? ReadSavedToken()
    {
        try { return JsonNode.Parse(File.ReadAllText(HubJsonPath))?["token"]?.GetValue<string>() is { Length: >= 32 } t ? t : null; }
        catch (Exception) { return null; } // missing or corrupt: mint a new one
    }

    void WriteHubJson()
    {
        var json = new JsonObject { ["port"] = Port, ["token"] = Token, ["pid"] = Environment.ProcessId, ["protocol"] = Wire.Version };
        var tmp = HubJsonPath + ".tmp";
        File.WriteAllText(tmp, json.ToJsonString());
        File.Move(tmp, HubJsonPath, true);
    }
}
