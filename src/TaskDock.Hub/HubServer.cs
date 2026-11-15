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

        if (e == null || p.GetProperty("hook_event_name").GetString() != "PermissionRequest") return Results.Ok();

        var item = new PendingApproval(
            Guid.NewGuid().ToString("N"), e.Instance,
            p.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "?" : "?",
            p.TryGetProperty("tool_input", out var ti) ? ClaudeSessions.ToolPreview(ti) : null,
            p.TryGetProperty("tool_input", out ti) ? JsonSerializer.Serialize(ti, new JsonSerializerOptions { WriteIndented = true }) : "{}",
            p.TryGetProperty("permission_suggestions", out var ps) ? ps : null,
            DateTimeOffset.UtcNow + options.HoldWindow);

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
        var connected = true;
        try
        {
            while (await Receive(ws, cts.Token) is { } text)
            {
                c.LastSeen = DateTimeOffset.UtcNow;
                var f = TryParse(text);
                switch (f?.Type)
                {
                    case "state" when f.Body<StateBody>() is { } s:
                        c.LastState = s.State;
                        c.LastLabel = s.Label;
                        sink.TryWrite(new StateEvent(c.Adapter, c.Instance, s.State, s.Label, s.Detail,
                            s.Progress is { } pr ? Math.Clamp(pr, 0, 1) : null, c.Match, c.Pid, connected));
                        connected = false;
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

        // Unity goes silent during a domain reload: keep "Reloading" until the new hello arrives.
        var reloading = c.Adapter == "unity" && c.LastState?.StartsWith("Reloading", StringComparison.OrdinalIgnoreCase) == true && !clean;
        if (!reloading) sink.TryWrite(new StateEvent(c.Adapter, c.Instance, "Offline", c.LastLabel ?? c.Instance, Match: c.Match, Pid: c.Pid));
        if (clean) await Close(ws, WebSocketCloseStatus.NormalClosure, "bye");
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
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(status, why, CancellationToken.None); }
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
        public string? LastState { get; set; }
        public string? LastLabel { get; set; }
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
