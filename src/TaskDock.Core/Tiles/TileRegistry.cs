namespace TaskDock.Core.Tiles;

/// <summary>
/// Single-threaded: the App drains the hub's Channel&lt;StateEvent&gt; into it on the UI thread every 100 ms,
/// so each tile repaints at most 10 times a second while NotifyEngine still sees every transition.
/// </summary>
public sealed class TileRegistry(TimeProvider time)
{
    public static readonly TimeSpan EndedLinger = TimeSpan.FromSeconds(30);

    readonly Dictionary<string, Tile> _tiles = new();

    /// <summary>(tile after the change, previous class, previous state)</summary>
    public event Action<Tile, StatusClass, string>? Transition;

    public IReadOnlyCollection<Tile> Tiles => _tiles.Values;

    public Tile? Get(string key) => _tiles.GetValueOrDefault(key);

    public Tile Apply(StateEvent e)
    {
        var now = time.GetUtcNow();
        if (!_tiles.TryGetValue(e.Key, out var t))
        {
            t = new Tile { Adapter = e.Adapter, Instance = e.Instance, ConnectedAt = now };
            _tiles[e.Key] = t;
        }
        if (e.Connected) t.ConnectedAt = now;

        var prevClass = t.Class;
        var prevState = t.State;

        if (t.AckedState != null && !string.Equals(t.AckedState, e.State, StringComparison.Ordinal)) t.AckedState = null;
        var acked = t.AckedState != null;

        t.State = acked ? "Idle" : e.State;
        t.Class = acked ? StatusClass.Idle : Rules.Rules.Classify(e.Adapter, e.State);
        t.Label = e.Label;
        t.Detail = e.Detail;
        t.Progress = e.Progress;
        if (e.Match != null) t.Match = e.Match;
        if (e.Pid != null) t.Pid = e.Pid;
        t.UpdatedAt = now;

        if (prevClass != t.Class || prevState != t.State) Transition?.Invoke(t, prevClass, prevState);
        return t;
    }

    /// <summary>Done drops to Idle once you hover or click the tile.</summary>
    public void Acknowledge(string key)
    {
        if (!_tiles.TryGetValue(key, out var t) || t.Class != StatusClass.Done) return;
        t.AckedState = t.State;
        t.State = "Idle";
        t.Class = StatusClass.Idle;
    }

    public void Remove(string key) => _tiles.Remove(key);

    /// <summary>Ended Claude sessions disappear after 30 s; callers keep pinned ones visible as placeholders.</summary>
    public List<string> Sweep()
    {
        var cutoff = time.GetUtcNow() - EndedLinger;
        var gone = _tiles.Values.Where(t => t.Adapter == "claude" && t.Class == StatusClass.Offline && t.UpdatedAt < cutoff)
            .Select(t => t.Key).ToList();
        foreach (var k in gone) _tiles.Remove(k);
        return gone;
    }
}
