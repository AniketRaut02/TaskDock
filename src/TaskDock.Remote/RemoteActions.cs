using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Dock;

namespace TaskDock.Remote;

/// <summary>
/// One path for every phone action, socket or HTTP: rate limit, dedup by action ID, requirement, step-up, run, log, hint.
/// </summary>
sealed class RemoteActions(IDockHost host, StepUp stepUp, ActivityLog log, TimeProvider time)
{
    const int PerSecond = 10, StepUpsPerMinute = 10, Remember = 256;
    /// <summary>A Peek fingerprint is bound to this instead of an approval: it can't approve anything.</summary>
    public const string PeekScope = "peek";
    static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(10), RunTimeout = TimeSpan.FromSeconds(10);

    sealed class PerDevice
    {
        public readonly SemaphoreSlim Gate = new(1, 1); // one action at a time per phone: dedup can't race itself
        public readonly Queue<DateTimeOffset> Actions = new(), StepUps = new();
        public readonly Dictionary<string, (JsonObject Result, DateTimeOffset At)> Done = [];
        public readonly Queue<string> DoneOrder = new();
        public DateTimeOffset PeekUntil, LastPeek;
        public readonly SemaphoreSlim PeekGate = new(1, 1);
    }

    readonly ConcurrentDictionary<string, PerDevice> _devices = new();

    /// <summary>The result body {id, ok, code?, error?} for one action body {id, action}.</summary>
    public async Task<JsonObject> Handle(Device device, JsonObject? body)
    {
        var id = body?["id"]?.GetValueKind() == JsonValueKind.String ? body["id"]!.GetValue<string>() : "";
        if (id.Length is 0 or > 64) return Result(id, DockResult.Fail("invalid", "id is required"));

        var d = _devices.GetOrAdd(device.Id, _ => new PerDevice());
        await d.Gate.WaitAsync();
        try
        {
            var now = time.GetUtcNow();
            foreach (var (k, v) in d.Done.Where(kv => now - kv.Value.At > RememberFor).ToList()) d.Done.Remove(k);
            if (d.Done.TryGetValue(id, out var seen)) return seen.Result; // a retry or double tap: same answer, no second effect
            if (!Allow(d.Actions, now, PerSecond, TimeSpan.FromSeconds(1))) return Result(id, DockResult.Fail("rate_limited", "Too many taps"));

            var r = await Run(device, body!["action"]);
            var result = Result(id, r);
            d.Done[id] = (result, now);
            d.DoneOrder.Enqueue(id);
            while (d.DoneOrder.Count > Remember) d.Done.Remove(d.DoneOrder.Dequeue());
            return result;
        }
        finally { d.Gate.Release(); }
    }

    async Task<DockResult> Run(Device device, JsonNode? actionJson)
    {
        if (actionJson is not JsonObject) return DockResult.Fail("invalid", "missing action");
        var (action, why) = DockAction.Parse(JsonSerializer.Deserialize<JsonElement>(actionJson.ToJsonString()));
        if (action == null) return DockResult.Fail("invalid", why);

        var approval = action switch
        {
            ApprovalAllow a => host.Approval(a.ApprovalId), ApprovalAlwaysAllow a => host.Approval(a.ApprovalId),
            ApprovalDeny a => host.Approval(a.ApprovalId), ApprovalTerminal a => host.Approval(a.ApprovalId), _ => null,
        };
        var (verb, target) = Describe(action, approval);

        DockResult r;
        if (host.Requirement(action) == Requirement.Fingerprint)
        {
            var (approvalId, choice, assertion) = action switch
            {
                ApprovalAllow a => (a.ApprovalId, "allow", a.Assertion),
                ApprovalAlwaysAllow a => (a.ApprovalId, "alwaysAllow", a.Assertion),
                _ => ("", "", null),
            };
            if (approval == null) r = DockResult.Fail("already_resolved", "Already handled");
            else if (assertion is not { } signed) r = DockResult.Fail("stepup_required", "Needs your fingerprint");
            else if (!await stepUp.Verify(device, approvalId, approval.ToolInputJson, choice, signed)) r = DockResult.Fail("stepup_failed", "Fingerprint check failed");
            else r = await RunOnHost(action);
        }
        else r = await RunOnHost(action);

        log.Add(new ActivityEntry(time.GetUtcNow(), device.Id, device.Name, ActionType(action), target, r.Ok, r.Code,
            approval == null ? null : approval.Preview ?? approval.ToolInputJson));
        if (r.Ok) host.Notice($"{verb} from {device.Name}");
        return r;
    }

    async Task<DockResult> RunOnHost(DockAction action)
    {
        var run = host.RunAsync(action);
        return await Task.WhenAny(run, Task.Delay(RunTimeout)) == run ? await run : DockResult.Fail("failed", "TaskDock didn't answer in time");
    }

    /// <summary>Assertion options for a fingerprint on one approval and one choice, or the reason not.</summary>
    public (string? Options, string? Code) StepUpOptions(Device device, JsonObject? body)
    {
        string? Str(string k) => body?[k]?.GetValueKind() == JsonValueKind.String ? body[k]!.GetValue<string>() : null;
        var purpose = Str("purpose");
        if (device.Credential == null || purpose is not ("approval" or "peek")) return (null, "invalid");
        if (purpose == "approval" && (Str("approvalId") is not { Length: > 0 and <= 128 } || Str("choice") is not ("allow" or "alwaysAllow"))) return (null, "invalid");
        var d = _devices.GetOrAdd(device.Id, _ => new PerDevice());
        lock (d.StepUps) if (!Allow(d.StepUps, time.GetUtcNow(), StepUpsPerMinute, TimeSpan.FromMinutes(1))) return (null, "rate_limited");
        if (purpose == "peek") return (stepUp.Options(device, PeekScope, "", PeekScope), null);
        if (host.Approval(Str("approvalId")!) is not { } approval) return (null, "already_resolved");
        return (stepUp.Options(device, approval.Id, approval.ToolInputJson, Str("choice")!), null);
    }

    /// <summary>A fingerprint for purpose "peek" gives this phone 5 minutes of Peek.</summary>
    public async Task<bool> GrantPeek(Device device, JsonElement assertion)
    {
        if (!await stepUp.Verify(device, PeekScope, "", PeekScope, assertion)) return false;
        _devices.GetOrAdd(device.Id, _ => new PerDevice()).PeekUntil = time.GetUtcNow() + TimeSpan.FromMinutes(5);
        return true;
    }

    /// <summary>One window capture: needs a Peek grant; one at a time per phone, at most one a second. Never cached.</summary>
    public async Task<(byte[]? Jpeg, string? Code)> Peek(Device device, string tileKey)
    {
        var d = _devices.GetOrAdd(device.Id, _ => new PerDevice());
        var now = time.GetUtcNow();
        if (now >= d.PeekUntil) return (null, "stepup_required");
        if (!d.PeekGate.Wait(0)) return (null, "rate_limited");
        try
        {
            if (now - d.LastPeek < TimeSpan.FromSeconds(1)) return (null, "rate_limited");
            d.LastPeek = now;
            var r = await host.PeekAsync(tileKey);
            var label = host.Snapshot?.Tiles.Concat(host.Snapshot.Detected).FirstOrDefault(t => t.Key == tileKey)?.Label ?? tileKey;
            log.Add(new ActivityEntry(now, device.Id, device.Name, "peek", label, r.Jpeg != null, r.Code));
            return r;
        }
        finally { d.PeekGate.Release(); }
    }

    static bool Allow(Queue<DateTimeOffset> recent, DateTimeOffset now, int max, TimeSpan window)
    {
        while (recent.Count > 0 && now - recent.Peek() >= window) recent.Dequeue();
        if (recent.Count >= max) return false;
        recent.Enqueue(now);
        return true;
    }

    static JsonObject Result(string id, DockResult r)
    {
        var o = new JsonObject { ["id"] = id, ["ok"] = r.Ok };
        if (r.Code != null) o["code"] = r.Code;
        if (r.Error != null) o["error"] = r.Error;
        return o;
    }

    static string ActionType(DockAction a) =>
        JsonSerializer.SerializeToNode(a, DockJson.Options)?["type"]?.GetValue<string>() ?? a.GetType().Name;

    /// <summary>The PC hint ("Allowed Bash", "MyGame: Play") and the activity log's target. Labels come from the widget's own snapshot.</summary>
    (string Verb, string Target) Describe(DockAction a, ApprovalInput? approval)
    {
        var s = host.Snapshot;
        string Tile(string key) => s?.Tiles.Concat(s.Detected).FirstOrDefault(t => t.Key == key)?.Label ?? key;
        string Pin(string pinId) => s?.Tiles.FirstOrDefault(t => t.PinId == pinId)?.Label ?? "a pinned tile";
        var tool = approval?.ToolName ?? "an approval";
        var player = s?.Media?.AppName ?? "media";
        return a switch
        {
            ApprovalAllow => ($"Allowed {tool}", tool),
            ApprovalAlwaysAllow => ($"Always allowed {tool}", tool),
            ApprovalDeny => ($"Denied {tool}", tool),
            ApprovalTerminal => ($"Sent {tool} to the terminal", tool),
            AdapterCommand c => ($"{Tile(c.TileKey)}: {c.Command switch { "enterPlay" => "Play", "exitPlay" => "Stop", _ => "Pause/Resume" }}", Tile(c.TileKey)),
            DownloadCommand c => ($"{Tile(c.TileKey)}: {c.Command} download", $"{Tile(c.TileKey)} #{c.DownloadId}"),
            TileAck t => ($"Cleared {Tile(t.TileKey)}", Tile(t.TileKey)),
            TilePin t => ($"Pinned {Tile(t.TileKey)}", Tile(t.TileKey)),
            TileUnpin t => ($"Unpinned {Pin(t.PinId)}", Pin(t.PinId)),
            TileRename t => ($"Renamed {Pin(t.PinId)}", Pin(t.PinId)),
            TileNotify t => ($"{(t.On ? "Sounds on" : "Sounds off")} for {Pin(t.PinId)}", Pin(t.PinId)),
            TileLaunch t => ($"Launched {Pin(t.PinId)}", Pin(t.PinId)),
            PresetApply p => ($"{(p.Merge ? "Merged" : "Applied")} preset {p.Name}", p.Name),
            PresetLaunchMissing => ("Launched missing apps", "preset"),
            MediaPrev => ($"{player}: previous", player),
            MediaToggle => ($"{player}: play/pause", player),
            MediaNext => ($"{player}: next", player),
            MediaSelect m => ("Switched the media bar", m.AppId),
            MediaPin m => ($"{(m.On ? "Pinned" : "Unpinned")} {player}", player),
            MediaSearchAction m => ($"Searched {m.Target}", m.Target), // the query itself stays out of the log
            PowerLock => ("Locked the PC", "PC"),
            PowerSleep => ("Putting the PC to sleep", "PC"),
            _ => ("Ran an action", a.GetType().Name),
        };
    }
}
