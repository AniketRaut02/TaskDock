using TaskDock.Core.Tiles;

namespace TaskDock.Core.Rules;

/// <summary>Per-adapter state → status class tables and notify rules, straight from the plan's adapter sections.</summary>
public static class Rules
{
    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    static readonly Dictionary<string, Dictionary<string, StatusClass>> Tables = new(Cmp)
    {
        ["claude"] = new(Cmp)
        {
            ["Idle"] = StatusClass.Idle, ["Working"] = StatusClass.Busy, ["Needs approval"] = StatusClass.Attention,
            ["Needs input"] = StatusClass.Attention, ["Done"] = StatusClass.Done, ["Failed"] = StatusClass.Error,
            ["Ended"] = StatusClass.Offline,
        },
        ["unity"] = new(Cmp)
        {
            ["Idle"] = StatusClass.Idle, ["Compiling"] = StatusClass.Busy, ["Compile errors"] = StatusClass.Error,
            ["Reloading"] = StatusClass.Busy, ["Importing"] = StatusClass.Busy, ["Background task"] = StatusClass.Busy,
            ["Playing"] = StatusClass.Busy, ["Paused"] = StatusClass.Busy, ["Building"] = StatusClass.Busy,
            ["Main thread blocked"] = StatusClass.Busy, ["Not responding"] = StatusClass.Error, ["Closed"] = StatusClass.Offline,
        },
        ["downloads"] = new(Cmp)
        {
            ["Idle"] = StatusClass.Idle, ["Downloading"] = StatusClass.Busy, ["Needs confirmation"] = StatusClass.Attention,
            ["Complete"] = StatusClass.Done, ["Failed"] = StatusClass.Error, ["Paused"] = StatusClass.Idle,
        },
        ["vscode"] = new(Cmp)
        {
            ["Failed"] = StatusClass.Error, ["Succeeded"] = StatusClass.Done, ["Debugging"] = StatusClass.Busy,
            ["Task running"] = StatusClass.Busy, ["Command running"] = StatusClass.Busy, ["Idle"] = StatusClass.Idle,
            ["Closed"] = StatusClass.Offline,
        },
        ["generic"] = new(Cmp)
        {
            ["Running"] = StatusClass.Idle, ["Not responding"] = StatusClass.Error, ["Closed"] = StatusClass.Offline,
        },
    };

    /// <summary>"Compile errors (3)" and "Failed (exit 1)" classify by the text before the parenthesis.</summary>
    public static StatusClass Classify(string adapter, string state)
    {
        var key = state.Split(" (", 2)[0].Trim();
        if (key.Equals("Offline", StringComparison.OrdinalIgnoreCase)) return StatusClass.Offline;
        return Tables.TryGetValue(adapter, out var t) && t.TryGetValue(key, out var c) ? c : StatusClass.Idle;
    }

    /// <summary>The "Notify sound" columns. Reacts to class transitions only.</summary>
    public static Sound SoundFor(string adapter, StatusClass prev, StatusClass next)
    {
        if (prev == next) return Sound.None;
        if (adapter.Equals("generic", StringComparison.OrdinalIgnoreCase))
            return prev == StatusClass.Error && next == StatusClass.Idle ? Sound.Complete : Sound.None; // window recovered
        return next switch
        {
            StatusClass.Attention => Sound.Attention,
            StatusClass.Error => Sound.Error,
            StatusClass.Done => Sound.Complete,
            _ => Sound.None,
        };
    }
}
