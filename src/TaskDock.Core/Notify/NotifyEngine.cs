using TaskDock.Core.Tiles;

namespace TaskDock.Core.Notify;

/// <summary>Turns transitions into sounds. It never plays audio; the UI layer does.</summary>
public sealed class NotifyEngine(TimeProvider time)
{
    public static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);
    /// <summary>Unity: one sound per busy episode, once the editor has settled this long.</summary>
    public static readonly TimeSpan UnitySettle = TimeSpan.FromSeconds(1.5);

    sealed class Episode { public bool Work, Play; }

    readonly Dictionary<string, Episode> _episodes = new();
    readonly Dictionary<string, (Tile Tile, Sound Sound, string State, DateTimeOffset Due)> _settling = new();
    DateTimeOffset _lastSound = DateTimeOffset.MinValue;

    public bool Muted { get; set; }
    public TimeOnly? QuietFrom { get; set; }
    public TimeOnly? QuietTo { get; set; }
    /// <summary>Per-tile Notify toggle (default off).</summary>
    public Func<Tile, bool> IsEnabled { get; set; } = _ => false;
    /// <summary>Stay silent when the source's window is already in front. Optional.</summary>
    public Func<Tile, bool> IsForeground { get; set; } = _ => false;

    public Sound OnTransition(Tile t, StatusClass prevClass, string prevState)
    {
        if (t.Adapter == "unity") return Unity(t);
        var sound = Rules.Rules.SoundFor(t.Adapter, prevClass, t.Class);
        if (sound == Sound.None) return Sound.None;
        if (time.GetUtcNow() - t.ConnectedAt < ConnectGrace) return Sound.None; // reconnect snapshots replay old states
        return Gate(t, sound);
    }

    /// <summary>Call regularly (the App does every 100 ms). Returns a settled Unity sound and its tile when one is due.</summary>
    public (Sound Sound, Tile? Tile) Tick()
    {
        var now = time.GetUtcNow();
        foreach (var (key, s) in _settling.ToList())
        {
            if (s.Due > now) continue;
            _settling.Remove(key);
            if (s.Tile.State == s.State) return (Gate(s.Tile, s.Sound), s.Tile); // still settled where it landed
        }
        return (Sound.None, null);
    }

    /// <summary>
    /// A compile → reload → import chain is one episode. The sound plays when the editor has been Idle
    /// (or Compile errors) for 1.5 s. Play mode episodes stay silent. A hung editor alerts at once.
    /// </summary>
    Sound Unity(Tile t)
    {
        var key = t.Key;
        if (t.State.StartsWith("Not responding", StringComparison.Ordinal)) return Gate(t, Sound.Error);

        if (t.Class == StatusClass.Busy)
        {
            _settling.Remove(key); // still going: same episode
            if (!_episodes.TryGetValue(key, out var ep)) _episodes[key] = ep = new Episode();
            if (t.State is "Playing" or "Paused") ep.Play = true;
            else if (t.State != "Main thread blocked") ep.Work = true; // a throttled idle editor isn't work worth a sound
            return Sound.None;
        }

        var settled = t.State == "Idle" ? Sound.Complete
            : t.State.StartsWith("Compile errors", StringComparison.Ordinal) ? Sound.Error : Sound.None;
        if (_episodes.Remove(key, out var done) && done.Work && !done.Play && settled != Sound.None)
            _settling[key] = (t, settled, t.State, time.GetUtcNow() + UnitySettle);
        else _settling.Remove(key);
        return Sound.None;
    }

    Sound Gate(Tile t, Sound sound)
    {
        if (Muted || !IsEnabled(t) || IsForeground(t)) return Sound.None;
        var now = time.GetUtcNow();
        if (InQuietHours(time.GetLocalNow())) return Sound.None;
        if (now - _lastSound < MinGap) return Sound.None; // collapse bursts into one sound
        _lastSound = now;
        return sound;
    }

    bool InQuietHours(DateTimeOffset local)
    {
        if (QuietFrom is not { } from || QuietTo is not { } to) return false;
        var t = TimeOnly.FromDateTime(local.DateTime);
        return from <= to ? t >= from && t < to : t >= from || t < to; // window may wrap midnight
    }
}
