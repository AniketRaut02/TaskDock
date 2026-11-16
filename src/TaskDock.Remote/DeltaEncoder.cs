using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Dock;

namespace TaskDock.Remote;

/// <summary>
/// Per connection: remembers what this phone last got and turns the next snapshot into a delta. Tiles go as per-key
/// upserts and removals; approvals, media, pill, presets and flags go whole when they change. Rev counts frames on this
/// connection, so a gap means the phone missed one.
/// </summary>
public sealed class DeltaEncoder
{
    Dictionary<string, string> _tiles = [];
    List<string> _order = [], _detectedOrder = [];
    string _approvals = "", _media = "", _pill = "", _presets = "", _flags = "";
    string _epoch = "";
    long _rev;

    /// <summary>A full snapshot (on connect and on resync); resets what this phone is known to have.</summary>
    public JsonNode Snapshot(DockSnapshot s)
    {
        Remember(s);
        return JsonSerializer.SerializeToNode(s with { Rev = ++_rev }, DockJson.Options)!;
    }

    /// <summary>Null when nothing this phone can see changed.</summary>
    public JsonObject? Delta(DockSnapshot s)
    {
        var oldTiles = _tiles;
        var (oldOrder, oldDetected) = (_order, _detectedOrder);
        var (oldApprovals, oldMedia, oldPill, oldPresets, oldFlags) = (_approvals, _media, _pill, _presets, _flags);
        Remember(s);

        var upsert = new JsonArray();
        foreach (var (key, json) in _tiles)
            if (!oldTiles.TryGetValue(key, out var was) || was != json) upsert.Add(JsonNode.Parse(json));
        var remove = new JsonArray(oldTiles.Keys.Where(k => !_tiles.ContainsKey(k)).Select(k => (JsonNode)k!).ToArray());

        var changes = new List<(string, JsonNode?)>();
        if (upsert.Count > 0) changes.Add(("upsert", upsert));
        if (remove.Count > 0) changes.Add(("remove", remove));
        if (!_order.SequenceEqual(oldOrder)) changes.Add(("order", Keys(_order)));
        if (!_detectedOrder.SequenceEqual(oldDetected)) changes.Add(("detectedOrder", Keys(_detectedOrder)));
        if (_approvals != oldApprovals) changes.Add(("approvals", JsonNode.Parse(_approvals)));
        if (_media != oldMedia) changes.Add(("media", JsonNode.Parse(_media))); // explicit null when the player went away
        if (_pill != oldPill) changes.Add(("pill", JsonNode.Parse(_pill)));
        if (_presets != oldPresets) changes.Add(("presets", JsonNode.Parse(_presets)));
        if (_flags != oldFlags) changes.Add(("flags", JsonNode.Parse(_flags)));
        if (changes.Count == 0) return null;

        var delta = new JsonObject { ["epoch"] = _epoch, ["rev"] = ++_rev };
        foreach (var (name, node) in changes) delta[name] = node;
        return delta;
    }

    void Remember(DockSnapshot s)
    {
        List<TileView> views = [.. s.Tiles, .. s.Detected];
        _epoch = s.Epoch;
        _tiles = views.ToDictionary(v => v.Key, v => JsonSerializer.Serialize(v, DockJson.Options));
        _order = s.Tiles.Select(v => v.Key).ToList();
        _detectedOrder = s.Detected.Select(v => v.Key).ToList();
        _approvals = JsonSerializer.Serialize(s.Approvals, DockJson.Options);
        _media = JsonSerializer.Serialize(s.Media, DockJson.Options);
        _pill = JsonSerializer.Serialize(s.Pill, DockJson.Options);
        _presets = JsonSerializer.Serialize(s.Presets, DockJson.Options);
        _flags = JsonSerializer.Serialize(s.Flags, DockJson.Options);
    }

    static JsonArray Keys(List<string> keys) => new(keys.Select(k => (JsonNode)k!).ToArray());
}
