using Microsoft.Extensions.Time.Testing;
using TaskDock.Core.Notify;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Tests;

/// <summary>Unity's settle window: one sound per busy episode, after 1.5 s of stable Idle / Compile errors.</summary>
public class UnityNotifyTests
{
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    readonly NotifyEngine _n;
    readonly TileRegistry _r;
    readonly List<Sound> _sounds = [];

    public UnityNotifyTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _n = new NotifyEngine(_time) { IsEnabled = _ => true };
        _r = new TileRegistry(_time);
        _r.Transition += (t, pc, ps) => Add(_n.OnTransition(t, pc, ps));
    }

    void Add(Sound s) { if (s != Sound.None) _sounds.Add(s); }
    void State(string s, bool connected = false) => _r.Apply(new StateEvent("unity", "u", s, "Fatebound", Connected: connected));
    void Wait(double seconds)
    {
        for (var i = 0; i < seconds * 10; i++) { _time.Advance(TimeSpan.FromMilliseconds(100)); Add(_n.Tick().Sound); }
    }

    [Fact]
    public void Compile_reload_import_chain_is_one_sound_after_settling()
    {
        State("Idle", connected: true);
        Wait(5);
        State("Compiling"); Wait(0.5);
        State("Reloading");
        State("Idle", connected: true); // new domain says hello: inside the 3 s connect grace, still counts
        Wait(0.5);
        State("Importing"); Wait(0.3);
        State("Idle");
        Wait(1.4);
        Assert.Empty(_sounds);
        Wait(0.2);
        Assert.Equal([Sound.Complete], _sounds);
        Wait(5);
        Assert.Single(_sounds);
    }

    [Fact]
    public void Compile_errors_settle_to_the_error_sound()
    {
        State("Idle", connected: true); Wait(5);
        State("Compiling"); Wait(1);
        State("Compile errors (2)"); Wait(2);
        Assert.Equal([Sound.Error], _sounds);
    }

    [Fact]
    public void Play_mode_is_silent_and_a_throttled_editor_is_not_an_episode()
    {
        State("Idle", connected: true); Wait(5);
        State("Playing"); Wait(3);
        State("Paused"); Wait(1);
        State("Reloading"); Wait(1); // exit play with a domain reload
        State("Idle"); Wait(3);
        State("Main thread blocked"); Wait(6);
        State("Idle"); Wait(3);
        Assert.Empty(_sounds);
    }

    [Fact]
    public void Hung_editor_alerts_immediately()
    {
        State("Idle", connected: true); Wait(5);
        State("Not responding");
        Assert.Equal([Sound.Error], _sounds);
    }

    [Fact]
    public void Tile_with_notify_off_stays_silent()
    {
        _n.IsEnabled = _ => false;
        State("Idle", connected: true); Wait(5);
        State("Compiling"); Wait(1); State("Idle"); Wait(3);
        Assert.Empty(_sounds);
    }
}
