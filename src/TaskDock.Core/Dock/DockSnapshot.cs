using System.Text.Json;
using System.Text.Json.Serialization;
using TaskDock.Core.Claude;
using TaskDock.Core.Presets;
using TaskDock.Core.Rules;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Dock;

// Remote wire format, version 1. protocol/remote/samples is the source of truth. Times are Unix milliseconds.
public sealed record DockSnapshot(
    string Epoch, long Rev, PillView Pill, IReadOnlyList<TileView> Tiles, IReadOnlyList<TileView> Detected,
    IReadOnlyList<ApprovalView> Approvals, MediaView? Media, PresetsView Presets, FlagsView Flags);

public sealed record PillView(string Text, StatusClass Worst);

public sealed record TileView(
    string Key, string? PinId, string Adapter, string Label, string State, StatusClass Class, string? Detail, double? Progress,
    bool Placeholder, bool Notify, TileCaps Caps, IReadOnlyList<ItemView>? Items);

/// <summary>A download. Never carries the local path.</summary>
public sealed record ItemView(int Id, string File, string State, double? Progress, double? SpeedBps);

public sealed record ApprovalView(
    string Id, string TileKey, string Tool, string Project, string Preview, string Input, Risk Risk,
    bool CanAlwaysAllow, long HeldSince, long Deadline);

public sealed record MediaView(
    string AppId, string AppName, string Title, string Artist, bool Playing, bool CanPrev, bool CanToggle, bool CanNext,
    double? Position, double? Duration, long? TimelineAt, string? ArtHash, IReadOnlyList<MediaAppView> Apps, bool Pinned);

public sealed record MediaAppView(string AppId, string Name, bool Selected);

public sealed record PresetsView(string Active, IReadOnlyList<string> Names);

public sealed record FlagsView(bool Away, bool KeepAwake);

/// <summary>A held approval as the app knows it; Core can't see the hub's PendingApproval.</summary>
public sealed record ApprovalInput(
    string Id, string SessionId, string ToolName, string? Preview, string ToolInputJson, bool CanAlwaysAllow,
    DateTimeOffset HeldSince, DateTimeOffset Deadline);

public static class DockJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Pure: from what the widget shows to the immutable state the phone sees.</summary>
public static class SnapshotBuilder
{
    public static DockSnapshot Build(
        string epoch, long rev, IEnumerable<(Tile? Tile, PresetTile? Pin)> tiles, IEnumerable<(Tile? Tile, PresetTile? Pin)> detected,
        IEnumerable<ApprovalInput> approvals, IReadOnlyList<Tile> registry, MediaView? media, PresetsView presets, FlagsView flags)
    {
        var shown = tiles.Select(p => Tile(p.Tile, p.Pin)).ToList();
        return new(epoch, rev, Pill.Of(shown.Where(v => !v.Placeholder).Select(v => v.Class)), shown,
            detected.Select(p => Tile(p.Tile, p.Pin)).ToList(),
            approvals.Select(a => Approval(a, registry)).ToList(), media, presets, flags);
    }

    public static TileView Tile(Tile? t, PresetTile? pin)
    {
        var caps = TileCaps.For(t, pin);
        if (t == null)
            return new("pin:" + pin!.Id, pin.Id, pin.Adapter, pin.Label ?? Describe(pin), "Waiting", StatusClass.Offline,
                $"Waiting for {pin.Adapter}…", null, true, pin.Notify, caps, null);
        return new(t.Key, pin?.Id, t.Adapter, pin?.Label ?? t.Label, t.State, t.Class, t.Detail, t.Progress, false,
            pin?.Notify ?? false, caps, t.Adapter == "downloads" ? Items(t.Items) : null);
    }

    /// <summary>A pin's name while its source isn't running.</summary>
    public static string Describe(PresetTile pin) =>
        pin.Match.TryGetValue("cwd", out var p) || pin.Match.TryGetValue("projectPath", out p) || pin.Match.TryGetValue("workspacePath", out p)
            ? ClaudeSessions.FolderName(p)
            : pin.Match.Values.FirstOrDefault() ?? pin.Adapter;

    // Same 20-item cap as the widget. Copies named fields only, so "path" (and anything new) never leaves the PC.
    static List<ItemView>? Items(JsonElement? items)
    {
        if (items is not { ValueKind: JsonValueKind.Array } arr) return null;
        static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return arr.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Take(20).Select(i => new ItemView(
            (int)(Num(i, "id") ?? -1), Str(i, "file") ?? "download", Str(i, "state") ?? "",
            Num(i, "total") is > 0 and var total ? (Num(i, "bytes") ?? 0) / total : null,
            Num(i, "speed"))).ToList();
    }

    static ApprovalView Approval(ApprovalInput a, IReadOnlyList<Tile> registry)
    {
        var key = Tiles.Tile.MakeKey("claude", a.SessionId);
        var cwd = registry.FirstOrDefault(t => t.Key == key)?.Match.GetValueOrDefault("cwd");
        return new(a.Id, key, a.ToolName, cwd != null ? ClaudeSessions.FolderName(cwd) : "Claude", a.Preview ?? a.ToolInputJson,
            a.ToolInputJson, ToolRisk.Classify(a.ToolName), a.CanAlwaysAllow,
            a.HeldSince.ToUnixTimeMilliseconds(), a.Deadline.ToUnixTimeMilliseconds());
    }
}

/// <summary>The widget's one-line summary, e.g. "1 needs you · 2 busy".</summary>
public static class Pill
{
    public static PillView Of(IEnumerable<StatusClass> live)
    {
        var classes = live.ToList();
        var parts = new List<string>();
        void Count(StatusClass c, string text) { var n = classes.Count(x => x == c); if (n > 0) parts.Add($"{n} {text}"); }
        Count(StatusClass.Attention, "needs you");
        Count(StatusClass.Error, "failed");
        Count(StatusClass.Busy, "busy");
        Count(StatusClass.Done, "done");
        return new(parts.Count == 0 ? "All quiet" : string.Join(" · ", parts), classes.Count == 0 ? StatusClass.Idle : classes.Min());
    }
}
