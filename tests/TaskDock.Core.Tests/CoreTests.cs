using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using TaskDock.Core.Claude;
using TaskDock.Core.Notify;
using TaskDock.Core.Presets;
using TaskDock.Core.Protocol;
using TaskDock.Core.Rules;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Tests;

public class ClaudeSessionTests
{
    static JsonElement Hook(string evt, string extra = "") =>
        JsonDocument.Parse($$"""{"session_id":"s1","cwd":"D:\\Projects\\Fatebound","hook_event_name":"{{evt}}"{{extra}}}""").RootElement;

    [Fact]
    public void Full_turn_walks_the_state_table()
    {
        var c = new ClaudeSessions();
        var start = c.Apply(Hook("SessionStart"))!;
        Assert.Equal(("Idle", "Fatebound", true), (start.State, start.Label, start.Connected));
        Assert.Equal("D:\\Projects\\Fatebound", start.Match!["cwd"]);

        Assert.Equal("Working", c.Apply(Hook("UserPromptSubmit", ""","prompt":"hi" """))!.State);
        var pre = c.Apply(Hook("PreToolUse", ""","tool_name":"Bash","tool_input":{"command":"npm test"}"""))!;
        Assert.Equal("Bash: npm test", pre.Detail);

        Assert.Equal("Needs approval", c.Apply(Hook("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"rm x"}"""))!.State);
        // A parallel tool call must not hide the held approval.
        Assert.Null(c.Apply(Hook("PreToolUse", ""","tool_name":"Read","tool_input":{"file_path":"a"}""")));
        Assert.Equal("Working", c.Resumed("s1")!.State);

        Assert.Equal("2 subagents", (c.Apply(Hook("SubagentStart")), c.Apply(Hook("SubagentStart"))).Item2!.Detail);
        var done = c.Apply(Hook("Stop", ""","last_assistant_message":"All tests pass.\nMore text" """))!;
        Assert.Equal(("Done", "All tests pass."), (done.State, done.Detail));

        Assert.Equal("Needs input", c.Apply(Hook("Notification", ""","notification_type":"idle_prompt","message":"Claude is waiting" """))!.State);
        Assert.Null(c.Apply(Hook("Notification", ""","notification_type":"auth_success" """)));
        Assert.Equal("Failed", c.Apply(Hook("StopFailure", ""","error":"rate_limit" """))!.State);
        Assert.Equal("Ended", c.Apply(Hook("SessionEnd"))!.State);
    }

    [Fact]
    public void Terminal_handoff_keeps_attention_until_next_tool()
    {
        var c = new ClaudeSessions();
        c.Apply(Hook("PermissionRequest", ""","tool_name":"Bash" """));
        var t = c.HandedToTerminal("s1")!;
        Assert.Equal(("Needs approval", "Waiting in terminal"), (t.State, t.Detail));
        Assert.Equal("Working", c.Apply(Hook("PreToolUse", ""","tool_name":"Bash" """))!.State);
    }

    [Fact]
    public void Session_title_wins_over_folder_and_bad_payloads_are_ignored()
    {
        var c = new ClaudeSessions();
        Assert.Equal("My task", c.Apply(Hook("UserPromptSubmit", ""","session_title":"My task" """))!.Label);
        Assert.Null(c.Apply(JsonDocument.Parse("""{"hook_event_name":"Stop"}""").RootElement));
        Assert.Null(c.Apply(JsonDocument.Parse("[]").RootElement));
    }
}

public class RulesAndRegistryTests
{
    [Theory]
    [InlineData("unity", "Compile errors (3)", StatusClass.Error)]
    [InlineData("vscode", "Failed (exit 1)", StatusClass.Error)]
    [InlineData("downloads", "Downloading (2, 45%)", StatusClass.Busy)]
    [InlineData("claude", "Needs approval", StatusClass.Attention)]
    [InlineData("unity", "Offline", StatusClass.Offline)]
    [InlineData("visualstudio", "Build failed (2 projects)", StatusClass.Error)]
    [InlineData("visualstudio", "Paused", StatusClass.Attention)]
    [InlineData("visualstudio", "Build succeeded", StatusClass.Done)]
    [InlineData("nobody", "whatever", StatusClass.Idle)]
    public void Classify(string adapter, string state, StatusClass expected) =>
        Assert.Equal(expected, Rules.Rules.Classify(adapter, state));

    [Fact]
    public void Registry_reports_transitions_and_acknowledge_sticks_until_state_changes()
    {
        var time = new FakeTimeProvider();
        var r = new TileRegistry(time);
        var seen = new List<(StatusClass, StatusClass)>();
        r.Transition += (t, prev, _) => seen.Add((prev, t.Class));

        r.Apply(new StateEvent("claude", "s1", "Working", "A"));
        r.Apply(new StateEvent("claude", "s1", "Working", "A", Detail: "x")); // no transition
        var t = r.Apply(new StateEvent("claude", "s1", "Done", "A"));
        r.Acknowledge(t.Key);
        Assert.Equal(StatusClass.Idle, t.Class);
        r.Apply(new StateEvent("claude", "s1", "Done", "A")); // same snapshot again stays acknowledged
        Assert.Equal(StatusClass.Idle, t.Class);
        r.Apply(new StateEvent("claude", "s1", "Working", "A"));
        Assert.Equal(StatusClass.Busy, t.Class);
        Assert.Equal([(StatusClass.Offline, StatusClass.Busy), (StatusClass.Busy, StatusClass.Done), (StatusClass.Idle, StatusClass.Busy)], seen);
    }

    [Fact]
    public void Ended_claude_tiles_are_swept_after_30s()
    {
        var time = new FakeTimeProvider();
        var r = new TileRegistry(time);
        r.Apply(new StateEvent("claude", "s1", "Ended", "A"));
        Assert.Empty(r.Sweep());
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Single(r.Sweep());
        Assert.Empty(r.Tiles);
    }
}

public class NotifyTests
{
    static (NotifyEngine, TileRegistry, FakeTimeProvider, List<Sound>) Setup()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var n = new NotifyEngine(time) { IsEnabled = _ => true };
        var r = new TileRegistry(time);
        var sounds = new List<Sound>();
        r.Transition += (t, prev, ps) => { var s = n.OnTransition(t, prev, ps); if (s != Sound.None) sounds.Add(s); };
        return (n, r, time, sounds);
    }

    [Fact]
    public void Done_plays_once_per_turn_and_never_inside_connect_grace()
    {
        var (_, r, time, sounds) = Setup();
        r.Apply(new StateEvent("claude", "s", "Done", "A", Connected: true)); // already Done on connect: silent
        time.Advance(TimeSpan.FromSeconds(5));
        r.Apply(new StateEvent("claude", "s", "Working", "A"));
        r.Apply(new StateEvent("claude", "s", "Done", "A"));
        r.Apply(new StateEvent("claude", "s", "Done", "A"));
        Assert.Equal([Sound.Complete], sounds);
    }

    [Fact]
    public void Bursts_collapse_to_one_sound_per_second()
    {
        var (_, r, time, sounds) = Setup();
        time.Advance(TimeSpan.FromSeconds(10));
        r.Apply(new StateEvent("claude", "a", "Working", "A"));
        r.Apply(new StateEvent("claude", "b", "Working", "B"));
        time.Advance(TimeSpan.FromSeconds(5));
        r.Apply(new StateEvent("claude", "a", "Needs approval", "A"));
        r.Apply(new StateEvent("claude", "b", "Failed", "B"));
        time.Advance(TimeSpan.FromSeconds(2));
        r.Apply(new StateEvent("claude", "a", "Done", "A"));
        Assert.Equal([Sound.Attention, Sound.Complete], sounds);
    }

    [Fact]
    public void Mute_quiet_hours_and_disabled_tiles_are_silent()
    {
        var (n, r, time, sounds) = Setup();
        r.Apply(new StateEvent("claude", "a", "Working", "A"));
        time.Advance(TimeSpan.FromSeconds(5));
        n.QuietFrom = new TimeOnly(11, 0); n.QuietTo = new TimeOnly(13, 0);
        r.Apply(new StateEvent("claude", "a", "Done", "A"));
        n.QuietFrom = null; n.Muted = true;
        r.Apply(new StateEvent("claude", "a", "Failed", "A"));
        n.Muted = false; n.IsEnabled = _ => false;
        r.Apply(new StateEvent("claude", "a", "Needs input", "A"));
        Assert.Empty(sounds);
    }

    [Fact]
    public void Generic_tile_sounds_only_when_a_hung_window_recovers()
    {
        Assert.Equal(Sound.None, Rules.Rules.SoundFor("generic", StatusClass.Idle, StatusClass.Error));
        Assert.Equal(Sound.Complete, Rules.Rules.SoundFor("generic", StatusClass.Error, StatusClass.Idle));
    }
}

public class PresetTests
{
    [Theory]
    [InlineData(@"D:\Projects\Fatebound\", "d:/projects/fatebound", true)]
    [InlineData("D:/Projects/Fatebound", "D:/Projects/Other", false)]
    public void Path_matching_is_normalised(string have, string want, bool match)
    {
        var tile = new Tile { Adapter = "claude", Instance = "x", Match = new Dictionary<string, string> { ["cwd"] = have } };
        var pin = new PresetTile { Adapter = "claude", Match = new() { ["cwd"] = want } };
        Assert.Equal(match, PresetMatcher.Matches(pin, tile));
    }

    static Tile Unity(int pid, StatusClass c, int updatedSecs) => new()
    {
        Adapter = "unity", Instance = $"unity:{pid}:abc", Class = c, ConnectedAt = DateTimeOffset.UnixEpoch.AddSeconds(updatedSecs),
        UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(updatedSecs), Match = new Dictionary<string, string> { ["projectPath"] = "D:/p" },
    };
    static readonly PresetTile UnityPin = new() { Adapter = "unity", Match = new() { ["projectPath"] = "D:/p" } };

    [Fact]
    public void Relaunched_app_shows_once_not_next_to_its_closed_tile()
    {
        var closed = Unity(100, StatusClass.Offline, 10);
        var relaunched = Unity(200, StatusClass.Idle, 20);
        Assert.Equal([relaunched], PresetMatcher.Bound(UnityPin, [closed, relaunched]));
    }

    [Fact]
    public void While_closed_only_the_latest_closed_tile_shows()
    {
        var first = Unity(100, StatusClass.Offline, 10);
        var second = Unity(200, StatusClass.Offline, 30);
        Assert.Equal([second], PresetMatcher.Bound(UnityPin, [first, second]));
        Assert.Empty(PresetMatcher.Bound(UnityPin, []));
    }

    [Fact]
    public void Two_running_editors_of_one_project_both_show()
    {
        var a = Unity(100, StatusClass.Busy, 10);
        var b = Unity(200, StatusClass.Idle, 20);
        Assert.Equal([a, b], PresetMatcher.Bound(UnityPin, [b, Unity(50, StatusClass.Offline, 5), a]));
    }

    [Fact]
    public void Adapter_must_match_and_empty_matchers_never_bind()
    {
        var tile = new Tile { Adapter = "unity", Instance = "x", Match = new Dictionary<string, string> { ["cwd"] = "D:/a" } };
        Assert.False(PresetMatcher.Matches(new PresetTile { Adapter = "claude", Match = new() { ["cwd"] = "D:/a" } }, tile));
        Assert.False(PresetMatcher.Matches(new PresetTile { Adapter = "unity" }, tile));
    }

    [Fact]
    public void Preset_from_the_plan_round_trips()
    {
        var json = """
        {"schema":1,"name":"FATEBOUND dev","corner":"bottomRight","mode":"expanded","tiles":[
          {"adapter":"unity","label":"FATEBOUND","notify":true,"match":{"projectPath":"D:/Projects/Fatebound"},
           "launch":{"exe":"C:/Program Files/Unity/Hub/Editor/6000.5.0f1/Editor/Unity.exe","args":["-projectPath","D:/Projects/Fatebound"]}},
          {"adapter":"generic","match":{"process":"blender.exe","titleRegex":"Fatebound"}}]}
        """;
        var p = JsonSerializer.Deserialize<Preset>(json, Wire.Json)!;
        Assert.Equal(("FATEBOUND dev", 2, true), (p.Name, p.Tiles.Count, p.Tiles[0].Notify));
        var again = JsonSerializer.Deserialize<Preset>(JsonSerializer.Serialize(p, Wire.Json), Wire.Json)!;
        Assert.Equal("-projectPath", again.Tiles[0].Launch!["args"]![0]!.GetValue<string>());
    }
}

public class ClaudeHooksTests
{
    [Fact]
    public void Install_is_idempotent_and_keeps_user_hooks()
    {
        var s = JsonNode.Parse("""
            {"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}
            """)!.AsObject();
        ClaudeHooks.Install(s, 47820, "tok", 120);
        ClaudeHooks.Install(s, 47821, "tok2", 120);

        Assert.Equal(2, s["hooks"]!["Stop"]!.AsArray().Count); // the user's plus one of ours
        Assert.Equal(2, s["hooks"]!["SubagentStop"]!.AsArray().Count + s["hooks"]!["SubagentStart"]!.AsArray().Count);
        Assert.Equal((47821, "tok2"), ClaudeHooks.Installed(s)!.Value);
        var perm = s["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!;
        Assert.Equal(130, perm["timeout"]!.GetValue<int>());
        Assert.Equal("tok2", perm["headers"]![ClaudeHooks.TokenHeader]!.GetValue<string>());
        Assert.Null(perm["onFailure"]); // never block Claude Code when the widget is down

        ClaudeHooks.Uninstall(s);
        Assert.Equal("""{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo hi"}]}]}}""", s.ToJsonString());
        Assert.False(ClaudeHooks.IsInstalled(s));
    }

    [Fact]
    public void Uninstall_removes_the_hooks_key_it_created()
    {
        var s = new JsonObject();
        ClaudeHooks.Install(s, 47820, "t", 120);
        Assert.True(ClaudeHooks.IsInstalled(s));
        Assert.Equal("{}", ClaudeHooks.Uninstall(s).ToJsonString());
    }
}
