namespace TaskDock.Core.Tiles;

// Declaration order is sort order: Attention and Error float to the top.
public enum StatusClass { Attention, Error, Busy, Done, Idle, Offline }

public enum Sound { None, Complete, Attention, Error }

/// <summary>One normalized update from any adapter. Connected marks the first frame after a (re)connect.</summary>
public sealed record StateEvent(
    string Adapter, string Instance, string State, string Label,
    string? Detail = null, double? Progress = null,
    IReadOnlyDictionary<string, string>? Match = null, int? Pid = null, bool Connected = false,
    System.Text.Json.JsonElement? Items = null)
{
    public string Key => Tile.MakeKey(Adapter, Instance);
}

public sealed class Tile
{
    public static string MakeKey(string adapter, string instance) => adapter + "|" + instance;

    public required string Adapter { get; init; }
    public required string Instance { get; init; }
    public string Key => MakeKey(Adapter, Instance);
    public string Label { get; set; } = "";
    public string State { get; set; } = "";
    public StatusClass Class { get; set; } = StatusClass.Offline;
    public string? Detail { get; set; }
    public double? Progress { get; set; }
    public IReadOnlyDictionary<string, string> Match { get; set; } = new Dictionary<string, string>();
    public int? Pid { get; set; }
    /// <summary>The adapter's item list (e.g. downloads), passed through as sent.</summary>
    public System.Text.Json.JsonElement? Items { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>A Done state the user acknowledged; it shows as Idle until the adapter reports something else.</summary>
    public string? AckedState { get; set; }
}
