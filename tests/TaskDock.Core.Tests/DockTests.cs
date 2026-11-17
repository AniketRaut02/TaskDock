using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Claude;
using TaskDock.Core.Dock;
using TaskDock.Core.Presets;
using TaskDock.Core.Protocol;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Tests;

/// <summary>TaskDock Remote, Phase 1: the Core pieces the widget and the phone share.</summary>
public class DockTests
{
    [Fact]
    public void Preset_file_without_ids_gets_stable_ones()
    {
        const string old = """{"name":"Default","tiles":[{"adapter":"unity","match":{"projectPath":"D:/a"}},{"adapter":"claude","match":{"cwd":"D:/b"}}]}""";
        var p = JsonSerializer.Deserialize<Preset>(old, Wire.Json)!;

        Assert.True(p.AssignMissingIds());
        Assert.All(p.Tiles, t => Assert.Matches("^[0-9a-f]{12}$", t.Id));
        Assert.NotEqual(p.Tiles[0].Id, p.Tiles[1].Id);

        var again = JsonSerializer.Deserialize<Preset>(JsonSerializer.Serialize(p, Wire.Json), Wire.Json)!;
        Assert.False(again.AssignMissingIds());
        Assert.Equal(p.Tiles.Select(t => t.Id), again.Tiles.Select(t => t.Id));
    }

    static Tile T(string adapter, string state, StatusClass c) => new() { Adapter = adapter, Instance = "1", State = state, Class = c };
    static readonly JsonObject Cmd = new() { ["exe"] = "x" };

    // caps: Play Stop Pause PauseText Launch Focus Peek Pin Unpin Ack
    public static TheoryData<string, Tile?, PresetTile?, TileCaps> CapsRows() => new()
    {
        { "placeholder with launch", null, new PresetTile { Launch = Cmd }, new(false, false, false, "Pause", true, false, false, false, true, false) },
        { "placeholder, no launch", null, new PresetTile(), new(false, false, false, "Pause", false, false, false, false, true, false) },
        { "unity idle, unpinned", T("unity", "Idle", StatusClass.Idle), null, new(true, false, false, "Pause", false, true, true, true, false, false) },
        { "unity playing", T("unity", "Playing", StatusClass.Busy), null, new(false, true, true, "Pause", false, true, true, true, false, false) },
        { "unity paused", T("unity", "Paused", StatusClass.Busy), null, new(false, true, true, "Resume", false, true, true, true, false, false) },
        { "unity compiling", T("unity", "Compiling", StatusClass.Busy), null, new(false, false, false, "Pause", false, true, true, true, false, false) },
        { "vscode offline, pinned with launch", T("vscode", "Offline", StatusClass.Offline), new PresetTile { Launch = Cmd }, new(false, false, false, "Pause", true, false, false, false, true, false) },
        { "vscode offline, pinned, no launch", T("vscode", "Offline", StatusClass.Offline), new PresetTile(), new(false, false, false, "Pause", false, false, false, false, true, false) },
        { "claude done: no window", T("claude", "Done", StatusClass.Done), null, new(false, false, false, "Pause", false, false, false, true, false, true) },
        { "downloads busy: no window", T("downloads", "Downloading", StatusClass.Busy), new PresetTile(), new(false, false, false, "Pause", false, false, false, false, true, false) },
        { "playing unity is not unity", T("generic", "Playing", StatusClass.Busy), null, new(false, false, false, "Pause", false, true, true, true, false, false) },
    };

    [Theory]
    [MemberData(nameof(CapsRows))]
    public void Tile_caps_follow_the_widget_rules(string row, Tile? t, PresetTile? pin, TileCaps want) =>
        Assert.True(want == TileCaps.For(t, pin), $"{row}: {TileCaps.For(t, pin)}");

    [Theory]
    [InlineData("Read", Risk.Read)]
    [InlineData("Grep", Risk.Read)]
    [InlineData("Glob", Risk.Read)]
    [InlineData("LS", Risk.Read)]
    [InlineData("WebSearch", Risk.Read)]
    [InlineData("TodoWrite", Risk.Read)]
    [InlineData("Bash", Risk.Risky)]
    [InlineData("Write", Risk.Risky)]
    [InlineData("Edit", Risk.Risky)]
    [InlineData("MultiEdit", Risk.Risky)]
    [InlineData("NotebookEdit", Risk.Risky)]
    [InlineData("WebFetch", Risk.Risky)]
    [InlineData("Task", Risk.Risky)]
    [InlineData("mcp__x__y", Risk.Risky)]
    [InlineData("mcp__fs__read", Risk.Risky)]
    [InlineData("read", Risk.Risky)] // names are case-sensitive: only the exact built-in is trusted
    [InlineData("SomethingNew", Risk.Risky)]
    [InlineData("", Risk.Risky)]
    [InlineData(null, Risk.Risky)]
    public void Only_listed_read_only_tools_skip_the_fingerprint(string? tool, Risk want) => Assert.Equal(want, ToolRisk.Classify(tool));

    static DockSnapshot SampleSnapshot()
    {
        var unity = new Tile { Adapter = "unity", Instance = "g1", Label = "MyGame", State = "Playing", Class = StatusClass.Busy };
        var claude = new Tile
        {
            Adapter = "claude", Instance = "s1", Label = "api", State = "Needs approval", Class = StatusClass.Attention, Detail = "Bash: npm test",
            Match = new Dictionary<string, string> { ["cwd"] = "D:/Projects/api" },
        };
        var downloads = new Tile
        {
            Adapter = "downloads", Instance = "chrome", Label = "Chrome", State = "Downloading", Class = StatusClass.Busy, Progress = 0.45,
            Items = JsonDocument.Parse("""
                [{"id":3,"file":"setup.exe","state":"in_progress","bytes":4500,"total":10000,"speed":1048576,"path":"C:/Users/x/Downloads/setup.exe","url":"https://x"},
                 {"id":4,"file":"notes.pdf","state":"complete","path":"C:/Users/x/Downloads/notes.pdf"}]
                """).RootElement,
        };
        var vscode = new Tile { Adapter = "vscode", Instance = "w1", Label = "TaskDoc", State = "Idle", Class = StatusClass.Idle };
        var unityPin = new PresetTile { Id = "a1b2c3d4e5f6", Adapter = "unity", Notify = true };
        var waiting = new PresetTile { Id = "0f0e0d0c0b0a", Adapter = "claude", Match = new() { ["cwd"] = "D:/Projects/web" }, Launch = Cmd };
        var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1763200000000);

        return SnapshotBuilder.Build("e1", 7,
            [(unity, unityPin), (claude, null), (downloads, null), (null, waiting)],
            [(vscode, null)],
            [new ApprovalInput("ap1", "s1", "Bash", "npm test", """{"command":"npm test"}""", true, t0, t0.AddMinutes(10))],
            [unity, claude, downloads, vscode],
            new MediaView("Spotify.exe", "Spotify", "Song", "Band", true, true, true, true, 30, 200, 1763200000000, "ab12",
                [new MediaAppView("Spotify.exe", "Spotify", true)], false),
            new PresetsView("Default", ["Default", "Work"]), new FlagsView(false, false));
    }

    [Fact]
    public void Snapshot_matches_the_golden_sample()
    {
        var want = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "remote-samples", "snapshot.json")));
        var got = JsonSerializer.SerializeToNode(SampleSnapshot(), DockJson.Options);
        Assert.True(JsonNode.DeepEquals(want, got), got!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void No_local_path_or_unlisted_item_field_reaches_the_phone()
    {
        var json = JsonSerializer.Serialize(SampleSnapshot(), DockJson.Options);
        Assert.DoesNotContain("\"path\"", json);
        Assert.DoesNotContain("Downloads/", json);
        Assert.DoesNotContain("\"url\"", json);
        Assert.DoesNotContain("\"focus\"", json); // Focus acts on the PC's screen
    }

    [Fact]
    public void Pill_summarizes_live_tiles_worst_first()
    {
        Assert.Equal(new PillView("All quiet", StatusClass.Idle), Pill.Of([]));
        Assert.Equal(new PillView("1 failed · 2 done", StatusClass.Error), Pill.Of([StatusClass.Done, StatusClass.Error, StatusClass.Done, StatusClass.Idle]));
    }
}

public class PresenceTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-17T10:00:00Z");
    static readonly TimeSpan AtPc = TimeSpan.FromSeconds(120);
    static DateTimeOffset At(int s) => T0.AddSeconds(s);

    [Theory]
    [InlineData(false, -500, 120)] // at the PC all along: HoldSeconds after it was held
    [InlineData(true, -500, 600)]  // away: the full hold
    [InlineData(false, 300, 420)]  // came back at 5 min: a fresh 2 minutes from then
    [InlineData(false, 550, 600)]  // came back at 9:10: never past the hub's own limit
    [InlineData(false, 0, 120)]
    public void Hold_deadline(bool away, int presentSinceS, int wantS) =>
        Assert.Equal(At(wantS), HoldRule.Deadline(T0, At(600), away, At(presentSinceS), AtPc));

    [Theory]
    // kind, away, app open, quiet hours → urgency
    [InlineData(PushKind.Approval, true, false, false, PushUrgency.High)]
    [InlineData(PushKind.Approval, true, false, true, PushUrgency.High)]   // approvals get through quiet hours
    [InlineData(PushKind.Approval, false, false, false, PushUrgency.None)] // at the PC: no push
    [InlineData(PushKind.Approval, true, true, false, PushUrgency.None)]   // app open: shown live
    [InlineData(PushKind.TileError, true, false, false, PushUrgency.Normal)]
    [InlineData(PushKind.TileError, true, false, true, PushUrgency.None)]
    [InlineData(PushKind.TileError, false, false, false, PushUrgency.None)]
    [InlineData(PushKind.TileError, true, true, false, PushUrgency.None)]
    [InlineData(PushKind.Sound, true, false, false, PushUrgency.Normal)]
    [InlineData(PushKind.Sound, true, false, true, PushUrgency.None)]
    [InlineData(PushKind.Sound, false, true, false, PushUrgency.None)]
    public void Push_policy(PushKind kind, bool away, bool appOpen, bool quiet, PushUrgency want) =>
        Assert.Equal(want, PushPolicy.Decide(kind, away, appOpen, quiet));
}
