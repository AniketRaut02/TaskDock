using System.Text.Json.Nodes;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Presets;

/// <param name="Pin">What to pin. Null only when the user must choose a Claude session first.</param>
/// <param name="Hint">Shown to the user, e.g. "install the Unity package".</param>
/// <param name="ClaudeChoices">Terminal windows can't be mapped to a session, so the user picks one (or falls back to Pin).</param>
public sealed record Resolution(PresetTile Pin, string? Hint = null, IReadOnlyList<Tile>? ClaudeChoices = null);

/// <summary>Picked or dropped process → the richest adapter available for it.</summary>
public static class Resolver
{
    static readonly string[] Terminals = ["windowsterminal.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "openconsole.exe"];

    public static Resolution Resolve(string imagePath, string windowTitle, int pid, IEnumerable<Tile> live)
    {
        var exe = Path.GetFileName(imagePath).ToLowerInvariant();
        var tiles = live.Where(t => t.Class != StatusClass.Offline).ToList();

        switch (exe)
        {
            case "unity.exe":
                if (tiles.FirstOrDefault(t => t.Adapter == "unity" && t.Pid == pid) is { } u && u.Match.TryGetValue("projectPath", out var project))
                    return new(Pin(u, Launch(imagePath, "-projectPath", project)));
                return new(Generic(imagePath, exe), "Install the TaskDock Unity package to see compile, reload and play-mode state.");

            case "devenv.exe":
                if (tiles.FirstOrDefault(t => t.Adapter == "visualstudio" && t.Pid == pid) is { } vsTile)
                    return new(Pin(vsTile, vsTile.Match.TryGetValue("solutionPath", out var sln) ? Launch(imagePath, sln) : Launch(imagePath)));
                return new(Generic(imagePath, exe), "Install the TaskDock Visual Studio extension to see builds and debugging.");

            case "code.exe":
                if (tiles.FirstOrDefault(t => t.Adapter == "vscode" && t.Label.Length > 0 && windowTitle.Contains(t.Label, StringComparison.OrdinalIgnoreCase)) is { } v
                    && v.Match.TryGetValue("workspacePath", out var ws))
                    return new(Pin(v, Launch("code", ws)));
                return new(Generic(imagePath, exe), "Install the TaskDock VS Code extension to see tasks, debugging and long commands.");

            case "chrome.exe" or "msedge.exe" or "brave.exe":
                var browser = exe switch { "msedge.exe" => "edge", "brave.exe" => "brave", _ => "chrome" };
                var hint = tiles.Any(t => t.Adapter == "downloads" && t.Match.GetValueOrDefault("browser") == browser)
                    ? null : "Load the TaskDock extension in this browser to see downloads.";
                return new(new PresetTile { Adapter = "downloads", Match = new() { ["browser"] = browser } }, hint);

            default:
                if (Terminals.Contains(exe))
                {
                    var sessions = tiles.Where(t => t.Adapter == "claude").ToList();
                    if (sessions.Count > 0) return new(Generic(imagePath, exe), ClaudeChoices: sessions);
                }
                return new(Generic(imagePath, exe));
        }
    }

    /// <summary>A dropped .exe or shortcut: always a generic tile that launches what was dropped.</summary>
    public static PresetTile ForDroppedFile(string droppedPath, string targetExe) => new()
    {
        Adapter = "generic",
        Label = Path.GetFileNameWithoutExtension(droppedPath),
        Match = new() { ["process"] = Path.GetFileName(targetExe).ToLowerInvariant() },
        Launch = new JsonObject { ["exe"] = droppedPath },
    };

    /// <summary>The terminal launch for a Claude session pinned by folder.</summary>
    public static PresetTile ForClaude(Tile session) => Pin(session, new JsonObject
    {
        ["template"] = "terminal", ["cwd"] = session.Match.GetValueOrDefault("cwd") ?? "", ["cmd"] = "claude",
    });

    static PresetTile Pin(Tile t, JsonObject launch)
    {
        var p = PresetMatcher.PinFor(t);
        p.Launch = launch;
        return p;
    }

    static PresetTile Generic(string imagePath, string exe) => new()
    {
        Adapter = "generic", Match = new() { ["process"] = exe }, Launch = new JsonObject { ["exe"] = imagePath },
    };

    static JsonObject Launch(string exe, params string[] args) => new() { ["exe"] = exe, ["args"] = new JsonArray(args.Select(a => (JsonNode)a!).ToArray()) };

    /// <summary>Stable tile identity for a generic pin, so relaunches rebind the same tile.</summary>
    public static string GenericInstance(PresetTile pin) =>
        "generic:" + pin.Match.GetValueOrDefault("process") + "|" + pin.Match.GetValueOrDefault("titleRegex");
}
