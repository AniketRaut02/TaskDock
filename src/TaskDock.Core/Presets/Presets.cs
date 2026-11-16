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

    /// <summary>Gives pins from an older file their ID. True when any was missing, so the caller saves.</summary>
    public bool AssignMissingIds()
    {
        var missing = Tiles.Any(t => !t.HasId);
        foreach (var t in Tiles) _ = t.Id;
        return missing;
    }
}

public sealed class PresetTile
{
    string? _id;

    /// <summary>Stable pin identity (12 hex chars): remote actions name a pin by this, never by its position.</summary>
    public string Id { get => _id ??= NewId(); set => _id = value; }
    internal bool HasId => _id != null;
    public static string NewId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));

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
    /// <summary>
    /// The tiles a pin shows. A relaunched app is a new tile (new PID), so the closed one is dropped once a match runs
    /// again; while nothing runs, only the most recent closed match shows (with its Launch button).
    /// </summary>
    public static List<Tile> Bound(PresetTile pin, IEnumerable<Tile> tiles)
    {
        var matches = tiles.Where(t => Matches(pin, t)).ToList();
        var running = matches.Where(t => t.Class != StatusClass.Offline).OrderBy(t => t.ConnectedAt).ToList();
        return running.Count > 0 ? running : matches.OrderByDescending(t => t.UpdatedAt).Take(1).ToList();
    }

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
