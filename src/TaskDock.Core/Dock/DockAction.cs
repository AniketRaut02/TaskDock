using System.Text.Json;
using System.Text.Json.Serialization;
using TaskDock.Core.Claude;
using TaskDock.Core.Media;
using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Dock;

/// <summary>Core's own copy of the hub's ApprovalChoice (Core can't reference the hub); the app maps one to the other.</summary>
public enum ApprovalDecision { Allow, AlwaysAllow, Deny, Terminal }

public enum Requirement { None, Confirm, Fingerprint }

/// <summary>The action catalog. A phone names things by ID only, never by path or command text.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApprovalAllow), "approval.allow")]
[JsonDerivedType(typeof(ApprovalAlwaysAllow), "approval.alwaysAllow")]
[JsonDerivedType(typeof(ApprovalDeny), "approval.deny")]
[JsonDerivedType(typeof(ApprovalTerminal), "approval.terminal")]
[JsonDerivedType(typeof(AdapterCommand), "adapter.command")]
[JsonDerivedType(typeof(DownloadCommand), "download.command")]
[JsonDerivedType(typeof(TileAck), "tile.ack")]
[JsonDerivedType(typeof(TilePin), "tile.pin")]
[JsonDerivedType(typeof(TileUnpin), "tile.unpin")]
[JsonDerivedType(typeof(TileRename), "tile.rename")]
[JsonDerivedType(typeof(TileNotify), "tile.notify")]
[JsonDerivedType(typeof(TileLaunch), "tile.launch")]
[JsonDerivedType(typeof(PresetApply), "preset.apply")]
[JsonDerivedType(typeof(PresetLaunchMissing), "preset.launchMissing")]
[JsonDerivedType(typeof(MediaPrev), "media.prev")]
[JsonDerivedType(typeof(MediaToggle), "media.toggle")]
[JsonDerivedType(typeof(MediaNext), "media.next")]
[JsonDerivedType(typeof(MediaSelect), "media.select")]
[JsonDerivedType(typeof(MediaPin), "media.pin")]
[JsonDerivedType(typeof(MediaSearchAction), "media.search")]
[JsonDerivedType(typeof(PowerLock), "power.lock")]
[JsonDerivedType(typeof(PowerSleep), "power.sleep")]
public abstract record DockAction
{
    /// <summary>Null when the arguments are well-formed; otherwise why not.</summary>
    public virtual string? Invalid() => null;

    /// <summary>An action from the network: (action, null) or (null, why it was refused).</summary>
    public static (DockAction?, string?) Parse(JsonElement json)
    {
        try
        {
            var a = json.Deserialize<DockAction>(DockJson.Options);
            return a == null ? (null, "missing action") : a.Invalid() is { } why ? (null, why) : (a, null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return (null, "unknown or malformed action"); }
    }

    internal static string? Id(string? v, string name) => string.IsNullOrEmpty(v) || v.Length > 128 ? $"{name} is required" : null;
}

public sealed record ApprovalAllow(string ApprovalId, JsonElement? Assertion = null) : DockAction { public override string? Invalid() => Id(ApprovalId, "approvalId"); }
public sealed record ApprovalAlwaysAllow(string ApprovalId, JsonElement? Assertion = null) : DockAction { public override string? Invalid() => Id(ApprovalId, "approvalId"); }
public sealed record ApprovalDeny(string ApprovalId, string? Reason = null) : DockAction
{
    public override string? Invalid() => Id(ApprovalId, "approvalId") ?? (Reason?.Length > 500 ? "reason is over 500 characters" : null);
}
public sealed record ApprovalTerminal(string ApprovalId) : DockAction { public override string? Invalid() => Id(ApprovalId, "approvalId"); }

public sealed record AdapterCommand(string TileKey, string Command) : DockAction
{
    public override string? Invalid() => Id(TileKey, "tileKey") ?? (Command is "enterPlay" or "exitPlay" or "togglePause" ? null : "unknown command");
}
public sealed record DownloadCommand(string TileKey, int DownloadId, string Command) : DockAction
{
    public override string? Invalid() => Id(TileKey, "tileKey") ?? (Command is "pause" or "resume" or "cancel" ? null : "unknown command");
}
public sealed record TileAck(string TileKey) : DockAction { public override string? Invalid() => Id(TileKey, "tileKey"); }
public sealed record TilePin(string TileKey) : DockAction { public override string? Invalid() => Id(TileKey, "tileKey"); }
public sealed record TileUnpin(string PinId) : DockAction { public override string? Invalid() => Id(PinId, "pinId"); }
public sealed record TileRename(string PinId, string? Label) : DockAction
{
    public override string? Invalid() => Id(PinId, "pinId") ?? (Label?.Length > 64 ? "label is over 64 characters" : null);
}
public sealed record TileNotify(string PinId, bool On) : DockAction { public override string? Invalid() => Id(PinId, "pinId"); }
public sealed record TileLaunch(string PinId) : DockAction { public override string? Invalid() => Id(PinId, "pinId"); }
public sealed record PresetApply(string Name, bool Merge) : DockAction { public override string? Invalid() => Id(Name, "name"); }
public sealed record PresetLaunchMissing : DockAction;
public sealed record MediaPrev : DockAction;
public sealed record MediaToggle : DockAction;
public sealed record MediaNext : DockAction;
public sealed record MediaSelect(string AppId) : DockAction { public override string? Invalid() => Id(AppId, "appId"); }
public sealed record MediaPin(bool On) : DockAction;
public sealed record MediaSearchAction(string Target, string Query) : DockAction
{
    [JsonIgnore] public SearchTarget? SearchTarget => Target switch { "spotify" => Media.SearchTarget.Spotify, "youtube" => Media.SearchTarget.YouTube, _ => null };
    public override string? Invalid() =>
        SearchTarget == null ? "target must be spotify or youtube"
        : string.IsNullOrWhiteSpace(Query) || Query.Length > 100 ? "query must be 1 to 100 characters" : null;
}

public sealed record PowerLock : DockAction;
/// <summary>Needs confirm: true, sent only after the phone showed "Your phone can't wake the PC again".</summary>
public sealed record PowerSleep(bool Confirm) : DockAction { public override string? Invalid() => Confirm ? null : "confirm first"; }

public sealed record DockResult(bool Ok, string? Code = null, string? Error = null)
{
    public static readonly DockResult Success = new(true);
    public static DockResult Fail(string code, string? error = null) => new(false, code, error);
}

/// <summary>What ActionRouter needs from the app: live state to re-check against, and the widget's own operations.</summary>
public interface IDockOps
{
    Tile? Tile(string key);
    PresetTile? Pin(string pinId);
    PresetTile? PinOf(Tile t);
    /// <summary>The live tile a pin is showing, or null while its source isn't running.</summary>
    Tile? Bound(PresetTile pin);
    ApprovalInput? Approval(string id);
    MediaView? Media { get; }
    bool HasPreset(string name);

    bool Resolve(string approvalId, ApprovalDecision decision, string? reason);
    /// <summary>The adapter's answer: null on success, its error otherwise.</summary>
    Task<string?> Command(Tile t, string command, JsonElement? args);
    void Acknowledge(string tileKey);
    void Pin(Tile t);
    void Unpin(PresetTile pin);
    void Rename(PresetTile pin, string? label);
    void SetNotify(PresetTile pin, bool on);
    void Launch(PresetTile pin);
    void ApplyPreset(string name, bool merge);
    Task LaunchMissing();
    Task MediaPrev();
    Task MediaToggle();
    Task MediaNext();
    Task MediaSelect(string appId);
    Task MediaPin(bool on);
    void MediaSearch(SearchTarget target, string query);
    void Lock();
    /// <summary>Puts the PC to sleep a few seconds later, so the result still reaches the phone.</summary>
    void Sleep();
}

/// <summary>Runs one catalog action after re-checking it against live state, so a phone acting on an old view gets "stale".</summary>
public static class ActionRouter
{
    /// <summary>What the phone must show before the action may run. Step-up itself is checked by the server, not here.</summary>
    public static Requirement Requirement(DockAction action, IDockOps ops) => action switch
    {
        ApprovalAlwaysAllow => Dock.Requirement.Fingerprint,
        PowerSleep => Dock.Requirement.Confirm,
        // An approval we can't see is treated as risky: never one tap by default.
        ApprovalAllow a => ops.Approval(a.ApprovalId) is { } p && ToolRisk.Classify(p.ToolName) == Risk.Read ? Dock.Requirement.None : Dock.Requirement.Fingerprint,
        _ => Dock.Requirement.None,
    };

    public static async Task<DockResult> RunAsync(DockAction action, IDockOps ops)
    {
        if (action.Invalid() is { } why) return DockResult.Fail("invalid", why);
        try { return await Run(action, ops); }
        catch (Exception e) { return DockResult.Fail("failed", e.Message); }
    }

    static DockResult Stale(string why) => DockResult.Fail("stale", why);
    static DockResult NotFound(string what) => DockResult.Fail("not_found", what);
    static readonly DockResult Gone = DockResult.Fail("already_resolved", "Already handled");

    static async Task<DockResult> Run(DockAction action, IDockOps ops)
    {
        switch (action)
        {
            // Held approvals disappear once answered, so a missing ID means it was handled (on the PC, in the terminal, or by timeout).
            case ApprovalAllow a: return Resolve(ops, a.ApprovalId, ApprovalDecision.Allow, null);
            case ApprovalAlwaysAllow a:
                if (ops.Approval(a.ApprovalId) is { CanAlwaysAllow: false }) return DockResult.Fail("invalid", "Claude offered no rule to always allow");
                return Resolve(ops, a.ApprovalId, ApprovalDecision.AlwaysAllow, null);
            case ApprovalDeny a: return Resolve(ops, a.ApprovalId, ApprovalDecision.Deny, a.Reason);
            case ApprovalTerminal a: return Resolve(ops, a.ApprovalId, ApprovalDecision.Terminal, null);

            case AdapterCommand c:
            {
                if (ops.Tile(c.TileKey) is not { } t) return NotFound("tile");
                var caps = TileCaps.For(t, ops.PinOf(t));
                var allowed = c.Command switch { "enterPlay" => caps.Play, "exitPlay" => caps.Stop, _ => caps.Pause };
                if (!allowed) return Stale($"{t.Label} is {t.State}");
                return await ops.Command(t, c.Command, null) is { } err ? DockResult.Fail("failed", err) : DockResult.Success;
            }
            case DownloadCommand d:
            {
                if (ops.Tile(d.TileKey) is not { Adapter: "downloads" } t) return NotFound("tile");
                var state = ItemState(t, d.DownloadId);
                if (state == null) return NotFound("download");
                var allowed = d.Command switch { "pause" => state == "in_progress", "resume" => state == "paused", _ => state is "in_progress" or "paused" };
                if (!allowed) return Stale($"download is {state}");
                return await ops.Command(t, d.Command, JsonSerializer.SerializeToElement(new { downloadId = d.DownloadId })) is { } err
                    ? DockResult.Fail("failed", err) : DockResult.Success;
            }
            case TileAck a:
            {
                if (ops.Tile(a.TileKey) is not { } t) return NotFound("tile");
                if (!TileCaps.For(t, ops.PinOf(t)).Ack) return Stale($"{t.Label} is {t.State}");
                ops.Acknowledge(t.Key);
                return DockResult.Success;
            }
            case TilePin p:
            {
                if (ops.Tile(p.TileKey) is not { } t) return NotFound("tile");
                if (ops.PinOf(t) != null) return Stale("already pinned");
                ops.Pin(t);
                return DockResult.Success;
            }
            case TileUnpin u: return WithPin(ops, u.PinId, ops.Unpin);
            case TileRename r: return WithPin(ops, r.PinId, pin => ops.Rename(pin, r.Label));
            case TileNotify n: return WithPin(ops, n.PinId, pin => ops.SetNotify(pin, n.On));
            case TileLaunch l:
            {
                if (ops.Pin(l.PinId) is not { } pin) return NotFound("pin");
                if (!TileCaps.For(ops.Bound(pin), pin).Launch) return Stale(pin.Launch == null ? "no launch command saved" : "already running");
                ops.Launch(pin);
                return DockResult.Success;
            }
            case PresetApply p:
                if (!ops.HasPreset(p.Name)) return NotFound("preset");
                ops.ApplyPreset(p.Name, p.Merge);
                return DockResult.Success;
            case PresetLaunchMissing:
                await ops.LaunchMissing();
                return DockResult.Success;

            case MediaPrev: return await Media(ops, m => m.CanPrev, ops.MediaPrev);
            case MediaToggle: return await Media(ops, m => m.CanToggle, ops.MediaToggle);
            case MediaNext: return await Media(ops, m => m.CanNext, ops.MediaNext);
            case MediaSelect s:
                if (ops.Media is not { } media) return NotFound("media");
                if (!media.Apps.Any(a => a.AppId == s.AppId)) return NotFound("media app");
                await ops.MediaSelect(s.AppId);
                return DockResult.Success;
            case MediaPin p: return await Media(ops, m => m.Pinned != p.On, () => ops.MediaPin(p.On));
            case MediaSearchAction s:
                ops.MediaSearch(s.SearchTarget!.Value, s.Query.Trim());
                return DockResult.Success;

            case PowerLock: ops.Lock(); return DockResult.Success;
            case PowerSleep: ops.Sleep(); return DockResult.Success;

            default: return DockResult.Fail("invalid", "unknown action");
        }
    }

    static DockResult Resolve(IDockOps ops, string id, ApprovalDecision d, string? reason) =>
        ops.Approval(id) != null && ops.Resolve(id, d, reason) ? DockResult.Success : Gone;

    static DockResult WithPin(IDockOps ops, string pinId, Action<PresetTile> run)
    {
        if (ops.Pin(pinId) is not { } pin) return NotFound("pin");
        run(pin);
        return DockResult.Success;
    }

    static async Task<DockResult> Media(IDockOps ops, Func<MediaView, bool> allowed, Func<Task> run)
    {
        if (ops.Media is not { } m) return NotFound("media");
        if (!allowed(m)) return Stale("the player changed");
        await run();
        return DockResult.Success;
    }

    static string? ItemState(Tile t, int id)
    {
        if (t.Items is not { ValueKind: JsonValueKind.Array } items) return null;
        foreach (var i in items.EnumerateArray())
            if (i.ValueKind == JsonValueKind.Object && i.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() == id)
                return i.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "";
        return null;
    }
}
