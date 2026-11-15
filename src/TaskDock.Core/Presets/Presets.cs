using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Presets;

/// <summary>%APPDATA%\TaskDock\presets\&lt;name&gt;.json. Stores matchers and launch commands, never handles or PIDs.</summary>
public sealed class Preset
{
    public int Schema { get; set; } = 1;
    public string Name { get; set; } = "Default";
    public string Corner { get; set; } = "bottomRight";
    public string Mode { get; set; } = "expanded";
    public List<PresetTile> Tiles { get; set; } = [];
}

public sealed class PresetTile
{
    public string Adapter { get; set; } = "";
    public string? Label { get; set; }
    public bool Notify { get; set; }
    public Dictionary<string, string> Match { get; set; } = new();
    public JsonObject? Launch { get; set; }
}

public static class PresetMatcher
{
    public static bool Matches(PresetTile pin, Tile tile)
    {
        if (!pin.Adapter.Equals(tile.Adapter, StringComparison.OrdinalIgnoreCase) || pin.Match.Count == 0) return false;
        foreach (var (key, want) in pin.Match)
        {
            if (key == "titleRegex")
            {
                if (!Regex.IsMatch(tile.Label, want, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50))) return false;
                continue;
            }
            if (!tile.Match.TryGetValue(key, out var have)) return false;
            var equal = IsPathKey(key)
                ? NormalizePath(have).Equals(NormalizePath(want), StringComparison.OrdinalIgnoreCase)
                : have.Equals(want, StringComparison.OrdinalIgnoreCase);
            if (!equal) return false;
        }
        return true;
    }

    /// <summary>A pin built from a live tile: every match key it reported.</summary>
    public static PresetTile PinFor(Tile t, bool notify = false) => new()
    {
        Adapter = t.Adapter, Notify = notify, Match = new Dictionary<string, string>(t.Match),
    };

    static bool IsPathKey(string key) => key == "cwd" || key.EndsWith("Path", StringComparison.Ordinal);

    /// <summary>Full path, forward slashes, no trailing slash. Compare case-insensitively.</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { full = path; }
        return full.Replace('\\', '/').TrimEnd('/');
    }
}
