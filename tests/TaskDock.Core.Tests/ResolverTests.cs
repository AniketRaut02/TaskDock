using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Tests;

public class ResolverTests
{
    static Tile T(string adapter, string label, int? pid, params (string K, string V)[] match) => new()
    {
        Adapter = adapter, Instance = adapter + label, Label = label, Pid = pid, Class = StatusClass.Idle,
        Match = match.ToDictionary(m => m.K, m => m.V),
    };

    [Fact]
    public void Unity_with_package_pins_the_project_and_captures_the_editor_path()
    {
        var u = T("unity", "Fatebound", 8812, ("projectPath", "D:/Projects/Fatebound"));
        var r = Resolver.Resolve(@"C:\Unity\6000.5.0f1\Editor\Unity.exe", "Fatebound - Main", 8812, [u]);
        Assert.Equal(("unity", "D:/Projects/Fatebound", null), (r.Pin.Adapter, r.Pin.Match["projectPath"], r.Hint));
        Assert.Equal(@"C:\Unity\6000.5.0f1\Editor\Unity.exe", r.Pin.Launch!["exe"]!.GetValue<string>());
        Assert.Equal("-projectPath", r.Pin.Launch["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Unity_without_package_is_generic_with_a_hint()
    {
        var r = Resolver.Resolve(@"C:\Unity\Editor\Unity.exe", "x", 1, [T("unity", "Other", 2, ("projectPath", "D:/o"))]);
        Assert.Equal(("generic", "unity.exe"), (r.Pin.Adapter, r.Pin.Match["process"]));
        Assert.NotNull(r.Hint);
    }

    [Fact]
    public void Visual_studio_with_extension_pins_the_solution_else_generic_with_hint()
    {
        var vs = T("visualstudio", "Starfall", 4242, ("solutionPath", @"D:\Projects\Starfall\Starfall.sln"));
        var r = Resolver.Resolve(@"C:\VS\Common7\IDE\devenv.exe", "Starfall - Microsoft Visual Studio", 4242, [vs]);
        Assert.Equal(("visualstudio", @"D:\Projects\Starfall\Starfall.sln"), (r.Pin.Adapter, r.Pin.Launch!["args"]![0]!.GetValue<string>()));
        var g = Resolver.Resolve(@"C:\VS\Common7\IDE\devenv.exe", "x", 1, [vs]);
        Assert.Equal(("generic", "devenv.exe"), (g.Pin.Adapter, g.Pin.Match["process"]));
        Assert.NotNull(g.Hint);
    }

    [Fact]
    public void Vscode_matches_by_workspace_name_in_title()
    {
        var v = T("vscode", "Fatebound", null, ("workspacePath", "D:/Projects/Fatebound"));
        var r = Resolver.Resolve(@"C:\VS Code\Code.exe", "Player.cs - Fatebound - Visual Studio Code", 5, [v]);
        Assert.Equal(("vscode", "code"), (r.Pin.Adapter, r.Pin.Launch!["exe"]!.GetValue<string>()));
        Assert.Equal("generic", Resolver.Resolve(@"C:\VS Code\Code.exe", "Other - Visual Studio Code", 5, [v]).Pin.Adapter);
    }

    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("MSEDGE.EXE", "edge")]
    [InlineData("brave.exe", "brave")]
    public void Browsers_pin_their_downloads_tile(string exe, string browser)
    {
        var r = Resolver.Resolve(@"C:\b\" + exe, "t", 1, []);
        Assert.Equal(("downloads", browser), (r.Pin.Adapter, r.Pin.Match["browser"]));
        Assert.NotNull(r.Hint);
    }

    [Fact]
    public void Terminal_offers_live_claude_sessions()
    {
        var c = T("claude", "Fatebound", null, ("cwd", "D:/p"));
        var r = Resolver.Resolve(@"C:\WT\WindowsTerminal.exe", "pwsh", 1, [c]);
        Assert.Equal([c], r.ClaudeChoices!);
        Assert.Equal("generic", r.Pin.Adapter);
        Assert.Null(Resolver.Resolve(@"C:\WT\WindowsTerminal.exe", "pwsh", 1, []).ClaudeChoices);
        Assert.Equal(("terminal", "D:/p"), (Resolver.ForClaude(c).Launch!["template"]!.GetValue<string>(), Resolver.ForClaude(c).Match["cwd"]));
    }

    [Fact]
    public void Anything_else_is_generic_and_binds_by_process_name()
    {
        var r = Resolver.Resolve(@"C:\Blender\blender.exe", "Fatebound.blend", 9, []);
        var tile = new Tile { Adapter = "generic", Instance = Resolver.GenericInstance(r.Pin), Label = "Fatebound.blend",
            Match = new Dictionary<string, string>(r.Pin.Match) };
        Assert.True(PresetMatcher.Matches(r.Pin, tile));
        Assert.Equal("generic:blender.exe|", tile.Instance);
    }

    [Fact]
    public void Dropped_shortcut_launches_the_shortcut_and_watches_its_target()
    {
        var p = Resolver.ForDroppedFile(@"C:\Users\me\Desktop\Blender.lnk", @"C:\Blender\blender.exe");
        Assert.Equal(("Blender", "blender.exe", @"C:\Users\me\Desktop\Blender.lnk"),
            (p.Label, p.Match["process"], p.Launch!["exe"]!.GetValue<string>()));
    }
}
