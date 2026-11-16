using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Dock;
using TaskDock.Core.Media;
using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Tests;

/// <summary>Every catalog action against one fixed state: success, stale, not_found, invalid.</summary>
public class ActionRouterTests
{
    sealed class FakeOps : IDockOps
    {
        static Tile T(string adapter, string instance, string state, StatusClass c, string? items = null) => new()
        {
            Adapter = adapter, Instance = instance, Label = instance, State = state, Class = c,
            Items = items == null ? null : JsonDocument.Parse(items).RootElement,
        };

        public readonly List<Tile> Tiles =
        [
            T("unity", "idle", "Idle", StatusClass.Idle),
            T("unity", "playing", "Playing", StatusClass.Busy),
            T("unity", "failing", "Paused", StatusClass.Busy),
            T("downloads", "chrome", "Downloading", StatusClass.Busy, """[{"id":1,"state":"in_progress"},{"id":2,"state":"complete"},{"id":3,"state":"paused"}]"""),
            T("claude", "done", "Done", StatusClass.Done),
            T("vscode", "pinned", "Idle", StatusClass.Idle),
        ];
        public readonly List<PresetTile> Pins =
        [
            new() { Id = "boundpin0001", Adapter = "vscode", Launch = new JsonObject() },
            new() { Id = "waiting00001", Adapter = "unity", Launch = new JsonObject() },
            new() { Id = "nolaunch0001", Adapter = "unity" },
        ];
        public readonly List<ApprovalInput> Approvals =
        [
            new("read", "s1", "Grep", null, "{}", false, default, default),
            new("risky", "s1", "Bash", null, "{}", true, default, default),
            new("norules", "s1", "Bash", null, "{}", false, default, default),
            new("race", "s1", "Bash", null, "{}", true, default, default), // answered on the PC a moment ago
        ];
        public readonly List<string> Calls = [];

        public MediaView? Media { get; set; } = new("Spotify.exe", "Spotify", "Song", "Band", true, CanPrev: true, CanToggle: true, CanNext: false,
            null, null, null, null, [new("Spotify.exe", "Spotify", true), new("chrome", "Chrome", false)], Pinned: false);

        public Tile? Tile(string key) => Tiles.FirstOrDefault(t => t.Key == key);
        public PresetTile? Pin(string pinId) => Pins.FirstOrDefault(p => p.Id == pinId);
        public PresetTile? PinOf(Tile t) => t.Key == "vscode|pinned" ? Pins[0] : null;
        public Tile? Bound(PresetTile pin) => pin == Pins[0] ? Tile("vscode|pinned") : null;
        public ApprovalInput? Approval(string id) => Approvals.FirstOrDefault(a => a.Id == id);
        public bool HasPreset(string name) => name is "Default" or "Work";

        public bool Resolve(string approvalId, ApprovalDecision d, string? reason) { Calls.Add($"resolve {approvalId} {d} {reason}"); return approvalId != "race"; }
        public Task<string?> Command(Tile t, string command, JsonElement? args)
        {
            Calls.Add($"command {t.Key} {command} {args}");
            return Task.FromResult(t.Instance == "failing" ? "adapter said no" : null);
        }
        public void Acknowledge(string tileKey) => Calls.Add("ack " + tileKey);
        public void Pin(Tile t) => Calls.Add("pin " + t.Key);
        public void Unpin(PresetTile pin) => Calls.Add("unpin " + pin.Id);
        public void Rename(PresetTile pin, string? label) => Calls.Add($"rename {pin.Id} {label}");
        public void SetNotify(PresetTile pin, bool on) => Calls.Add($"notify {pin.Id} {on}");
        public void Launch(PresetTile pin) => Calls.Add("launch " + pin.Id);
        public void ApplyPreset(string name, bool merge) => Calls.Add($"apply {name} {merge}");
        public Task LaunchMissing() { Calls.Add("launchMissing"); return Task.CompletedTask; }
        public Task MediaPrev() { Calls.Add("prev"); return Task.CompletedTask; }
        public Task MediaToggle() { Calls.Add("toggle"); return Task.CompletedTask; }
        public Task MediaNext() { Calls.Add("next"); return Task.CompletedTask; }
        public Task MediaSelect(string appId) { Calls.Add("select " + appId); return Task.CompletedTask; }
        public Task MediaPin(bool on) { Calls.Add("mediaPin " + on); return Task.CompletedTask; }
        public void MediaSearch(SearchTarget target, string query) => Calls.Add($"search {target} {query}");
    }

    static readonly string Long501 = new('x', 501), Long65 = new('x', 65), Long101 = new('x', 101);

    // Expected: "ok" (and the fake saw exactly that call), or the result code.
    public static TheoryData<string, string, string?> Rows() => new()
    {
        { """{"type":"approval.allow","approvalId":"read"}""", "ok", "resolve read Allow " },
        { """{"type":"approval.allow","approvalId":"gone"}""", "already_resolved", null },
        { """{"type":"approval.allow","approvalId":"race"}""", "already_resolved", "resolve race Allow " },
        { """{"type":"approval.allow"}""", "invalid", null },
        { """{"type":"approval.alwaysAllow","approvalId":"risky"}""", "ok", "resolve risky AlwaysAllow " },
        { """{"type":"approval.alwaysAllow","approvalId":"norules"}""", "invalid", null },
        { """{"type":"approval.alwaysAllow","approvalId":"gone"}""", "already_resolved", null },
        { """{"type":"approval.alwaysAllow","approvalId":""}""", "invalid", null },
        { """{"type":"approval.deny","approvalId":"risky","reason":"no"}""", "ok", "resolve risky Deny no" },
        { """{"type":"approval.deny","approvalId":"gone"}""", "already_resolved", null },
        { $$"""{"type":"approval.deny","approvalId":"risky","reason":"{{Long501}}"}""", "invalid", null },
        { """{"type":"approval.terminal","approvalId":"risky"}""", "ok", "resolve risky Terminal " },
        { """{"type":"approval.terminal","approvalId":"gone"}""", "already_resolved", null },
        { """{"type":"approval.terminal"}""", "invalid", null },

        { """{"type":"adapter.command","tileKey":"unity|idle","command":"enterPlay"}""", "ok", "command unity|idle enterPlay " },
        { """{"type":"adapter.command","tileKey":"unity|playing","command":"exitPlay"}""", "ok", "command unity|playing exitPlay " },
        { """{"type":"adapter.command","tileKey":"unity|playing","command":"togglePause"}""", "ok", "command unity|playing togglePause " },
        { """{"type":"adapter.command","tileKey":"unity|playing","command":"enterPlay"}""", "stale", null },
        { """{"type":"adapter.command","tileKey":"unity|idle","command":"exitPlay"}""", "stale", null },
        { """{"type":"adapter.command","tileKey":"unity|failing","command":"exitPlay"}""", "failed", "command unity|failing exitPlay " },
        { """{"type":"adapter.command","tileKey":"unity|gone","command":"enterPlay"}""", "not_found", null },
        { """{"type":"adapter.command","tileKey":"unity|idle","command":"quit"}""", "invalid", null },

        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":1,"command":"pause"}""", "ok", """command downloads|chrome pause {"downloadId":1}""" },
        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":3,"command":"resume"}""", "ok", """command downloads|chrome resume {"downloadId":3}""" },
        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":3,"command":"cancel"}""", "ok", """command downloads|chrome cancel {"downloadId":3}""" },
        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":1,"command":"resume"}""", "stale", null },
        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":2,"command":"cancel"}""", "stale", null },
        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":9,"command":"pause"}""", "not_found", null },
        { """{"type":"download.command","tileKey":"unity|idle","downloadId":1,"command":"pause"}""", "not_found", null },
        { """{"type":"download.command","tileKey":"downloads|chrome","downloadId":1,"command":"open"}""", "invalid", null },

        { """{"type":"tile.ack","tileKey":"claude|done"}""", "ok", "ack claude|done" },
        { """{"type":"tile.ack","tileKey":"unity|idle"}""", "stale", null },
        { """{"type":"tile.ack","tileKey":"claude|gone"}""", "not_found", null },
        { """{"type":"tile.ack","tileKey":""}""", "invalid", null },
        { """{"type":"tile.pin","tileKey":"unity|idle"}""", "ok", "pin unity|idle" },
        { """{"type":"tile.pin","tileKey":"vscode|pinned"}""", "stale", null },
        { """{"type":"tile.pin","tileKey":"unity|gone"}""", "not_found", null },
        { """{"type":"tile.pin"}""", "invalid", null },
        { """{"type":"tile.unpin","pinId":"boundpin0001"}""", "ok", "unpin boundpin0001" },
        { """{"type":"tile.unpin","pinId":"nopin"}""", "not_found", null },
        { """{"type":"tile.unpin","pinId":""}""", "invalid", null },
        { """{"type":"tile.rename","pinId":"boundpin0001","label":"Mine"}""", "ok", "rename boundpin0001 Mine" },
        { """{"type":"tile.rename","pinId":"boundpin0001","label":null}""", "ok", "rename boundpin0001 " },
        { """{"type":"tile.rename","pinId":"nopin","label":"x"}""", "not_found", null },
        { $$"""{"type":"tile.rename","pinId":"boundpin0001","label":"{{Long65}}"}""", "invalid", null },
        { """{"type":"tile.notify","pinId":"boundpin0001","on":true}""", "ok", "notify boundpin0001 True" },
        { """{"type":"tile.notify","pinId":"nopin","on":true}""", "not_found", null },
        { """{"type":"tile.notify","on":true}""", "invalid", null },
        { """{"type":"tile.launch","pinId":"waiting00001"}""", "ok", "launch waiting00001" },
        { """{"type":"tile.launch","pinId":"boundpin0001"}""", "stale", null },
        { """{"type":"tile.launch","pinId":"nolaunch0001"}""", "stale", null },
        { """{"type":"tile.launch","pinId":"nopin"}""", "not_found", null },
        { """{"type":"tile.launch","pinId":""}""", "invalid", null },

        { """{"type":"preset.apply","name":"Work","merge":true}""", "ok", "apply Work True" },
        { """{"type":"preset.apply","name":"Nope","merge":false}""", "not_found", null },
        { """{"type":"preset.apply","name":""}""", "invalid", null },
        { """{"type":"preset.launchMissing"}""", "ok", "launchMissing" },

        { """{"type":"media.prev"}""", "ok", "prev" },
        { """{"type":"media.toggle"}""", "ok", "toggle" },
        { """{"type":"media.next"}""", "stale", null },
        { """{"type":"media.select","appId":"chrome"}""", "ok", "select chrome" },
        { """{"type":"media.select","appId":"zune"}""", "not_found", null },
        { """{"type":"media.select","appId":""}""", "invalid", null },
        { """{"type":"media.pin","on":true}""", "ok", "mediaPin True" },
        { """{"type":"media.pin","on":false}""", "stale", null },
        { """{"type":"media.search","target":"spotify","query":" lofi "}""", "ok", "search Spotify lofi" },
        { """{"type":"media.search","target":"youtube","query":"x"}""", "ok", "search YouTube x" },
        { """{"type":"media.search","target":"napster","query":"x"}""", "invalid", null },
        { """{"type":"media.search","target":"spotify","query":"  "}""", "invalid", null },
        { $$"""{"type":"media.search","target":"spotify","query":"{{Long101}}"}""", "invalid", null },

        { """{"type":"power.format"}""", "invalid", null },
        { """{"approvalId":"read"}""", "invalid", null },
        { """{"type":"tile.ack","tileKey":42}""", "invalid", null },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Action_is_rechecked_against_live_state(string json, string want, string? call)
    {
        var ops = new FakeOps();
        var (action, why) = DockAction.Parse(JsonDocument.Parse(json).RootElement);
        var code = action == null ? "invalid" : (await ActionRouter.RunAsync(action, ops)) is var r && r.Ok ? "ok" : r.Code;

        Assert.True(want == code, $"{json} → {code} {why}");
        Assert.Equal(call == null ? [] : [call], ops.Calls);
    }

    [Theory]
    [InlineData("""{"type":"media.prev"}""")]
    [InlineData("""{"type":"media.select","appId":"chrome"}""")]
    [InlineData("""{"type":"media.pin","on":true}""")]
    public async Task Media_actions_need_a_player(string json)
    {
        var ops = new FakeOps { Media = null };
        var r = await ActionRouter.RunAsync(DockAction.Parse(JsonDocument.Parse(json).RootElement).Item1!, ops);
        Assert.Equal("not_found", r.Code);
        Assert.Empty(ops.Calls);
    }

    [Theory]
    [InlineData("""{"type":"approval.allow","approvalId":"read"}""", Requirement.None)]
    [InlineData("""{"type":"approval.allow","approvalId":"risky"}""", Requirement.Fingerprint)]
    [InlineData("""{"type":"approval.allow","approvalId":"gone"}""", Requirement.Fingerprint)]
    [InlineData("""{"type":"approval.alwaysAllow","approvalId":"read"}""", Requirement.Fingerprint)]
    [InlineData("""{"type":"approval.deny","approvalId":"risky"}""", Requirement.None)]
    [InlineData("""{"type":"approval.terminal","approvalId":"risky"}""", Requirement.None)]
    [InlineData("""{"type":"adapter.command","tileKey":"unity|idle","command":"enterPlay"}""", Requirement.None)]
    public void Fingerprint_only_where_the_security_model_asks(string json, Requirement want) =>
        Assert.Equal(want, ActionRouter.Requirement(DockAction.Parse(JsonDocument.Parse(json).RootElement).Item1!, new FakeOps()));

    [Fact]
    public async Task An_operation_that_throws_is_a_failed_result()
    {
        var ops = new ThrowingOps();
        var r = await ActionRouter.RunAsync(new TileAck("claude|done"), ops);
        Assert.Equal(new DockResult(false, "failed", "boom"), r);
    }

    sealed class ThrowingOps : IDockOps
    {
        readonly FakeOps _f = new();
        public Tile? Tile(string key) => _f.Tile(key);
        public PresetTile? Pin(string pinId) => _f.Pin(pinId);
        public PresetTile? PinOf(Tile t) => null;
        public Tile? Bound(PresetTile pin) => null;
        public ApprovalInput? Approval(string id) => null;
        public MediaView? Media => null;
        public bool HasPreset(string name) => false;
        public bool Resolve(string approvalId, ApprovalDecision d, string? reason) => false;
        public Task<string?> Command(Tile t, string command, JsonElement? args) => Task.FromResult<string?>(null);
        public void Acknowledge(string tileKey) => throw new InvalidOperationException("boom");
        public void Pin(Tile t) { }
        public void Unpin(PresetTile pin) { }
        public void Rename(PresetTile pin, string? label) { }
        public void SetNotify(PresetTile pin, bool on) { }
        public void Launch(PresetTile pin) { }
        public void ApplyPreset(string name, bool merge) { }
        public Task LaunchMissing() => Task.CompletedTask;
        public Task MediaPrev() => Task.CompletedTask;
        public Task MediaToggle() => Task.CompletedTask;
        public Task MediaNext() => Task.CompletedTask;
        public Task MediaSelect(string appId) => Task.CompletedTask;
        public Task MediaPin(bool on) => Task.CompletedTask;
        public void MediaSearch(SearchTarget target, string query) { }
    }
}
