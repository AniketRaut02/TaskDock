using TaskDock.Core.Tiles;

namespace TaskDock.Core.Notify;

/// <summary>Turns transitions into sounds. It never plays audio; the UI layer does.</summary>
public sealed class NotifyEngine(TimeProvider time)
{
    public static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);

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
        var sound = Rules.Rules.SoundFor(t.Adapter, prevClass, t.Class);
        if (sound == Sound.None || Muted || !IsEnabled(t) || IsForeground(t)) return Sound.None;

        var now = time.GetUtcNow();
        if (now - t.ConnectedAt < ConnectGrace) return Sound.None; // reconnect snapshots replay old states
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
