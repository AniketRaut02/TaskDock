using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using TaskDock.Core.Claude;
using TaskDock.Core.Protocol;
using TaskDock.Core.Tiles;
using TaskDock.Hub;

namespace TaskDock.Hub.Tests;

/// <summary>In-process Kestrel with real sockets. Every security case must fail closed.</summary>
public sealed class HubTests : IAsyncLifetime
{
    readonly Channel<StateEvent> _events = Channel.CreateUnbounded<StateEvent>();
    readonly ApprovalBroker _broker = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "taskdock-tests-" + Guid.NewGuid().ToString("N"));
    HubServer _hub = null!;
    volatile bool _hung, _alive = true;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _hub = new HubServer(new HubOptions { DataDir = _dir, BasePort = 0, HoldWindow = TimeSpan.FromSeconds(5),
            IsHung = _ => _hung, IsAlive = _ => _alive,
            AllowedOrigins = ["chrome-extension://goodid"] }, _events.Writer, _broker);
        await _hub.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_hub.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _hub.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    HttpRequestMessage Hook(string route, string body, string? token = null) => new(HttpMethod.Post, $"/hooks/claude/{route}")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
        Headers = { { ClaudeHooks.TokenHeader, token ?? _hub.Token } },
    };

    const string Perm = """{"session_id":"s1","cwd":"D:/p","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf node_modules"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm -rf node_modules"}],"behavior":"allow","destination":"localSettings"}]}""";

    async Task<PendingApproval> WaitForPending()
    {
        for (var i = 0; i < 100; i++)
        {
            if (_broker.Pending.FirstOrDefault() is { } p) return p;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Writes_hub_json_and_answers_health()
    {
        var hubJson = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "hub.json")))!;
        Assert.Equal(_hub.Port, hubJson["port"]!.GetValue<int>());
        Assert.Equal(_hub.Token, hubJson["token"]!.GetValue<string>());
        Assert.Contains("\"ok\":true", await _http.GetStringAsync("/health"));
    }

    [Fact]
    public async Task Hook_posts_a_state_event()
    {
        var r = await _http.SendAsync(Hook("prompt", """{"session_id":"s1","cwd":"D:/p","hook_event_name":"UserPromptSubmit","prompt":"x"}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync());
        var e = await _events.Reader.ReadAsync();
        Assert.Equal(("claude", "s1", "Working"), (e.Adapter, e.Instance, e.State));
    }

    [Theory]
    [InlineData("allow", """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""")]
    [InlineData("deny", """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"use npm ci"}}}""")]
    [InlineData("always", """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedPermissions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm -rf node_modules"}],"behavior":"allow","destination":"localSettings"}]}}}""")]
    [InlineData("terminal", "")]
    public async Task Permission_is_held_until_the_widget_decides(string choice, string expected)
    {
        var call = _http.SendAsync(Hook("permission", Perm));
        var p = await WaitForPending();
        Assert.Equal(("Bash", "rm -rf node_modules", true), (p.ToolName, p.Preview, p.CanAlwaysAllow));
        Assert.False(call.IsCompleted);

        _broker.Resolve(p.Id, choice switch
        {
            "allow" => ApprovalChoice.Allow, "deny" => ApprovalChoice.Deny,
            "always" => ApprovalChoice.AlwaysAllow, _ => ApprovalChoice.Terminal,
        }, "use npm ci");
        var r = await call;
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(expected, await r.Content.ReadAsStringAsync());
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Hold_window_expiry_returns_no_decision()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await _http.SendAsync(Hook("permission", Perm));
        Assert.Equal("", await r.Content.ReadAsStringAsync());
        Assert.InRange(sw.Elapsed.TotalSeconds, 4.5, 8);
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Dropped_request_clears_the_card()
    {
        using var cts = new CancellationTokenSource();
        var call = _http.SendAsync(Hook("permission", Perm), cts.Token);
        await WaitForPending();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        for (var i = 0; i < 100 && _broker.Pending.Count > 0; i++) await Task.Delay(20);
        Assert.Empty(_broker.Pending);
    }

    // ---------- security: all fail closed ----------

    [Fact]
    public async Task Bad_or_missing_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Hook("permission", Perm, "wrong"))).StatusCode);
        var noToken = new HttpRequestMessage(HttpMethod.Post, "/hooks/claude/stop") { Content = new StringContent("{}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(noToken)).StatusCode);
        Assert.Empty(_broker.Pending);
    }

    [Fact]
    public async Task Foreign_host_header_is_403()
    {
        var req = Hook("stop", "{}");
        req.Headers.Host = "evil.example:" + _hub.Port;
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Preflight_is_refused_without_cors_headers()
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/hooks/claude/permission");
        req.Headers.Add("Origin", "https://evil.example");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        var r = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.DoesNotContain(r.Headers, h => h.Key.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Web_page_origin_is_refused_even_with_token()
    {
        var req = Hook("stop", """{"session_id":"s","hook_event_name":"Stop"}""");
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task No_route_can_set_a_decision()
    {
        var call = _http.SendAsync(Hook("permission", Perm));
        var p = await WaitForPending();
        foreach (var path in new[] { $"/hooks/claude/decision", $"/approve/{p.Id}", "/ws" })
            await _http.SendAsync(Hook(path.TrimStart('/'), $$"""{"id":"{{p.Id}}","behavior":"allow"}"""));
        Assert.Single(_broker.Pending);
        _broker.Resolve(p.Id, ApprovalChoice.Terminal);
        await call;
    }

    // ---------- WebSocket adapters ----------

    async Task<ClientWebSocket> Connect(string? origin = null)
    {
        var ws = new ClientWebSocket();
        if (origin != null) ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_hub.Port}/ws"), CancellationToken.None);
        return ws;
    }

    static Task Send(ClientWebSocket ws, string type, object body) => ws.SendAsync(
        Encoding.UTF8.GetBytes(Wire.Frame(type, 1, null, body)), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<Envelope?> Read(ClientWebSocket ws)
    {
        var buf = new byte[65536];
        var r = await ws.ReceiveAsync(buf, new CancellationTokenSource(5000).Token);
        return r.MessageType == WebSocketMessageType.Close ? null : Wire.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
    }

    [Fact]
    public async Task Adapter_hello_state_command_and_bye()
    {
        using var ws = await Connect();
        await Send(ws, "hello", new HelloBody("vscode", "vs:1", Match: new() { ["workspacePath"] = "D:/p" }, Token: _hub.Token));
        Assert.Equal(5000, (await Read(ws))!.Body<WelcomeBody>()!.HeartbeatMs);

        await Send(ws, "state", new StateBody("Task running", "p", "build", 2.0));
        var e = await _events.Reader.ReadAsync();
        Assert.Equal(("vscode", "Task running", 1.0, true, "D:/p"), (e.Adapter, e.State, e.Progress, e.Connected, e.Match!["workspacePath"]));

        var cmd = _hub.SendCommandAsync(e.Key, "pause");
        var c = (await Read(ws))!.Body<CommandBody>()!;
        Assert.Equal("pause", c.Action);
        await Send(ws, "result", new ResultBody(c.Id, true));
        Assert.True((await cmd).Ok);

        await Send(ws, "bye", new ByeBody("shutdown"));
        Assert.Equal("Offline", (await _events.Reader.ReadAsync()).State);
    }

    [Fact]
    public async Task Downloads_items_reach_the_tile_and_extension_origin_needs_no_token()
    {
        using var ws = await Connect("chrome-extension://goodid");
        await Send(ws, "hello", new HelloBody("downloads", "chrome:1", Match: new() { ["browser"] = "chrome" }));
        await Read(ws);
        var items = JsonDocument.Parse("""[{"id":3,"file":"setup.exe","state":"in_progress","bytes":45,"total":100}]""").RootElement;
        await Send(ws, "state", new StateBody("Downloading (1, 45%)", "Chrome downloads", "setup.exe", 0.45, items));
        var e = await _events.Reader.ReadAsync();
        Assert.Equal("setup.exe", e.Items!.Value[0].GetProperty("file").GetString());
    }

    [Fact]
    public async Task Hello_with_bad_token_is_closed()
    {
        using var ws = await Connect();
        await Send(ws, "hello", new HelloBody("unity", "u:1", Token: "nope"));
        Assert.Null(await Read(ws));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws.CloseStatus);
    }

    [Fact]
    public async Task Allowed_extension_origin_needs_no_token_but_other_origins_cannot_connect()
    {
        using var ok = await Connect("chrome-extension://goodid");
        await Send(ok, "hello", new HelloBody("downloads", "chrome:1"));
        Assert.Equal("welcome", (await Read(ok))!.Type);

        await Assert.ThrowsAsync<WebSocketException>(() => Connect("https://evil.example"));
    }

    async Task<ClientWebSocket> Unity(string firstState)
    {
        var ws = await Connect();
        await Send(ws, "hello", new HelloBody("unity", "u:1", Pid: 4242, Match: new() { ["projectPath"] = "D:/p" }, Token: _hub.Token));
        await Read(ws);
        await Send(ws, "state", new StateBody(firstState, "Fatebound"));
        Assert.Equal(firstState, (await _events.Reader.ReadAsync()).State);
        return ws;
    }

    async Task<StateEvent> Next() => await _events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Unity_reload_keeps_reloading_with_elapsed_time_until_the_new_hello()
    {
        var ws = await Unity("Reloading");
        ws.Abort();
        var e = await Next();
        Assert.Equal("Reloading", e.State);
        Assert.Matches(@"^\d+ s$", e.Detail); // elapsed seconds; exact value depends on machine load

        using var again = await Connect();
        await Send(again, "hello", new HelloBody("unity", "u:1", Pid: 4242, Token: _hub.Token));
        await Read(again);
        await Send(again, "state", new StateBody("Idle", "Fatebound"));
        for (var i = 0; i < 10; i++)
        {
            var n = await Next();
            if (n.State == "Idle") { Assert.True(n.Connected); return; }
            Assert.Equal("Reloading", n.State);
        }
        Assert.Fail("never saw Idle");
    }

    [Fact]
    public async Task Unity_silent_main_thread_is_blocked_and_recovers()
    {
        using var ws = await Unity("Importing");
        await Send(ws, "heartbeat", new HeartbeatBody(6500));
        var e = await Next();
        Assert.Equal(("Main thread blocked", "No editor tick for 6 s · Importing"), (e.State, e.Detail));
        await Send(ws, "heartbeat", new HeartbeatBody(40));
        Assert.Equal("Importing", (await Next()).State);
    }

    [Fact]
    public async Task Unity_building_is_expected_silence()
    {
        using var ws = await Unity("Building");
        await Send(ws, "heartbeat", new HeartbeatBody(60_000));
        await Task.Delay(300);
        Assert.False(_events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Unity_hung_window_is_not_responding()
    {
        using var ws = await Unity("Compiling");
        _hung = true;
        await Send(ws, "heartbeat", new HeartbeatBody(9000));
        Assert.Equal("Not responding", (await Next()).State);
    }

    [Fact]
    public async Task Unity_killed_is_closed_and_a_dropped_socket_with_live_process_is_offline()
    {
        var ws = await Unity("Idle");
        _alive = false;
        ws.Abort();
        Assert.Equal("Closed", (await Next()).State);

        _alive = true;
        var ws2 = await Unity("Playing");
        ws2.Abort();
        Assert.Equal("Offline", (await Next()).State);
    }

    [Fact]
    public async Task Visual_studio_hung_is_not_responding_and_killed_is_closed()
    {
        var ws = await Connect();
        await Send(ws, "hello", new HelloBody("visualstudio", "vs:1", Pid: 5151, Match: new() { ["solutionPath"] = "D:/p/p.sln" }, Token: _hub.Token));
        await Read(ws);
        await Send(ws, "state", new StateBody("Building", "p"));
        Assert.Equal("Building", (await Next()).State);
        _hung = true;
        await Send(ws, "heartbeat", new { });
        Assert.Equal("Not responding", (await Next()).State);
        _hung = false;
        await Send(ws, "heartbeat", new { });
        Assert.Equal("Building", (await Next()).State);
        _alive = false;
        ws.Abort();
        Assert.Equal("Closed", (await Next()).State);
    }

    [Fact]
    public async Task Unity_process_dying_mid_reload_is_closed()
    {
        var ws = await Unity("Reloading");
        _alive = false;
        ws.Abort();
        Assert.Equal("Closed", (await Next()).State);
    }
}
