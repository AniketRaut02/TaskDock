// Dev-only: replays scripted adapter sessions against a running TaskDock for UI work and load checks.
//   fake demo     unity + vscode + downloads tiles cycling through their states
//   fake claude   one Claude Code session over HTTP hooks, including a held PermissionRequest
//   fake load     8 adapters at 10 updates/s each (Phase 1 CPU acceptance check)
//   fake showcase steady, realistic states for screenshots (Unity compiling, VS Code failed, Chrome downloading)
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Protocol;

var hubJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskDock", "hub.json");
if (!File.Exists(hubJson)) { Console.Error.WriteLine($"TaskDock isn't running ({hubJson} missing)"); return 1; }
var hub = JsonNode.Parse(File.ReadAllText(hubJson))!;
var port = hub["port"]!.GetValue<int>();
var token = hub["token"]!.GetValue<string>();
var sendLock = new SemaphoreSlim(1, 1);
var mode = args.FirstOrDefault() ?? "demo";
var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60;
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

switch (mode)
{
    case "claude": await Claude(); break;
    case "showcase":
        var files = JsonSerializer.SerializeToElement(new object[]
        {
            new { id = 1, file = "starfall-assets-v3.zip", path = @"C:\Users\Public\Downloads\starfall-assets-v3.zip", state = "in_progress", bytes = 640_000_000, total = 1_000_000_000, speed = 12_400_000 },
            new { id = 2, file = "unity-6000.5.8f1.exe", path = @"C:\Users\Public\Downloads\unity-6000.5.8f1.exe", state = "in_progress", bytes = 210_000_000, total = 420_000_000, speed = 9_100_000 },
            new { id = 3, file = "soundtrack-stems.zip", path = @"C:\Users\Public\Downloads\soundtrack-stems.zip", state = "complete", bytes = 88_000_000, total = 88_000_000 },
        });
        await Task.WhenAll(
            Adapter("unity", "unity:1:fake", new() { ["projectPath"] = "D:/Projects/Starfall" }, "Starfall",
                [("Compiling", "Assets/Scripts/Player/PlayerController.cs", null)]),
            Adapter("vscode", "vscode:fake", new() { ["workspacePath"] = "D:/Projects/starfall-web" }, "starfall-web",
                [("Failed (exit 1)", "npm run build exited with 1", null)]),
            Adapter("downloads", "chrome:fake", new() { ["browser"] = "chrome" }, "Chrome downloads",
                [("Downloading (2, 59%)", "starfall-assets-v3.zip · 11.8 MB/s · 31 s left · +1 more", 0.59)], files));
        break;
    case "load": await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Load(i))); break;
    default:
        await Task.WhenAll(
            Adapter("unity", "unity:1:fake", new() { ["projectPath"] = "D:/Projects/Starfall" }, "Starfall",
                [("Idle", null, null), ("Compiling", null, null), ("Reloading", null, null), ("Importing", "Textures", 0.4),
                 ("Compile errors (2)", "Assets/Player.cs(12): CS1002", null), ("Playing", null, null), ("Idle", null, null)]),
            Adapter("vscode", "vscode:fake", new() { ["workspacePath"] = "D:/Projects/Starfall" }, "Starfall",
                [("Idle", null, null), ("Command running", "npm install", null), ("Succeeded", "npm install · 42 s", null),
                 ("Task running", "build", null), ("Failed (exit 1)", "build exited with 1", null)]),
            Adapter("downloads", "chrome:fake", new() { ["browser"] = "chrome" }, "Chrome downloads",
                [("Downloading (1, 10%)", "unity-6000.5.0f1.exe · 12 MB/s", 0.1), ("Downloading (1, 55%)", "unity-6000.5.0f1.exe · 14 MB/s", 0.55),
                 ("Downloading (1, 95%)", "unity-6000.5.0f1.exe", 0.95), ("Complete", "unity-6000.5.0f1.exe", null), ("Idle", null, null)]));
        break;
}
return 0;

async Task Adapter(string adapter, string instance, Dictionary<string, string> match, string label, (string State, string? Detail, double? Progress)[] script, JsonElement? items = null)
{
    using var ws = await Connect(adapter, instance, match);
    var i = 0;
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var (state, detail, progress) = script[i++ % script.Length];
            await Send(ws, "state", new StateBody(state, label, detail, progress, items));
            Console.WriteLine($"{adapter,-10} {state}");
            await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
        }
    }
    catch (OperationCanceledException) { }
    await Send(ws, "bye", new ByeBody("demo over"));
}

async Task Load(int n)
{
    using var ws = await Connect("downloads", $"load:{n}", new() { ["browser"] = $"load{n}" });
    var heartbeat = DateTime.UtcNow;
    try
    {
        for (var i = 0; !stop.IsCancellationRequested; i++)
        {
            var p = i % 100 / 100.0;
            await Send(ws, "state", new StateBody($"Downloading (1, {p:P0})", $"Load {n}", $"file{n}.bin", p));
            await Task.Delay(100, stop.Token);
        }
    }
    catch (OperationCanceledException) { }
    await Send(ws, "bye", new ByeBody("load over"));
}

async Task<ClientWebSocket> Connect(string adapter, string instance, Dictionary<string, string> match)
{
    var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
    await Send(ws, "hello", new HelloBody(adapter, instance, "fake", Environment.ProcessId, match, [], token));
    var buf = new byte[4096];
    var r = await ws.ReceiveAsync(buf, CancellationToken.None);
    if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException($"hub refused {adapter}: {ws.CloseStatusDescription}");
    var beatMs = Wire.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))?.Body<WelcomeBody>()?.HeartbeatMs ?? 5000; // Unity gets 1 s
    _ = Task.Run(async () => // answer commands
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var rr = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (rr.MessageType == WebSocketMessageType.Close) return;
                var env = Wire.Parse(Encoding.UTF8.GetString(buf, 0, rr.Count));
                if (env?.Type == "command" && env.Body<CommandBody>() is { } c)
                {
                    Console.WriteLine($"{adapter,-10} command {c.Action}");
                    await Send(ws, "result", new ResultBody(c.Id, true));
                }
            }
        }
        catch (Exception) { }
    });
    _ = Task.Run(async () =>
    {
        while (ws.State == WebSocketState.Open) { await Task.Delay(beatMs * 4 / 5); try { await Send(ws, "heartbeat", new { }); } catch { return; } }
    });
    return ws;
}

async Task Send(ClientWebSocket ws, string type, object body)
{
    await sendLock.WaitAsync();
    try { await ws.SendAsync(Encoding.UTF8.GetBytes(Wire.Frame(type, 0, null, body)), WebSocketMessageType.Text, true, CancellationToken.None); }
    finally { sendLock.Release(); }
}

async Task Claude()
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(5) };
    http.DefaultRequestHeaders.Add("X-TaskDock-Token", token);
    var id = "fake-" + Guid.NewGuid().ToString("N")[..8];
    async Task<string> Hook(string route, string evt, object extra)
    {
        var body = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject();
        body["session_id"] = id;
        body["cwd"] = "D:/Projects/Starfall";
        body["hook_event_name"] = evt;
        var r = await http.PostAsJsonAsync($"hooks/claude/{route}", body);
        var text = await r.Content.ReadAsStringAsync();
        Console.WriteLine($"{evt,-18} → {(int)r.StatusCode} {(text.Length == 0 ? "(no decision)" : text)}");
        return text;
    }

    await Hook("session-start", "SessionStart", new { source = "startup" });
    await Hook("prompt", "UserPromptSubmit", new { prompt = "run the tests" });
    await Task.Delay(1500);
    await Hook("pre-tool", "PreToolUse", new { tool_name = "Bash", tool_input = new { command = "npm test" } });
    await Task.Delay(1500);
    Console.WriteLine("Waiting for you to Allow / Deny / Terminal on the widget…");
    await Hook("permission", "PermissionRequest", new
    {
        tool_name = "Bash",
        tool_input = new { command = "rm -rf node_modules && npm ci", description = "Reinstall dependencies" },
        permission_suggestions = new[] { new { type = "addRules", rules = new[] { new { toolName = "Bash", ruleContent = "npm ci" } }, behavior = "allow", destination = "localSettings" } },
    });
    await Hook("subagent", "SubagentStart", new { agent_type = "Explore" });
    await Task.Delay(1500);
    await Hook("subagent", "SubagentStop", new { agent_type = "Explore" });
    await Hook("stop", "Stop", new { last_assistant_message = "All 42 tests pass.\nDetails follow." });
    await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 20)));
    await Hook("session-end", "SessionEnd", new { reason = "prompt_input_exit" });
}
