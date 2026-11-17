namespace TaskDock.Core.Dock;

/// <summary>
/// How long a held approval waits. Claude Code snapshots the hook timeout when a session starts, so with Remote on the
/// hub always holds for the maximum (600 s) and TaskDock decides the real wait from whether someone is at the PC.
/// </summary>
public static class HoldRule
{
    /// <summary>
    /// When the approval goes back to the terminal. At the PC: <paramref name="atPc"/> after it was held, or after you came
    /// back, whichever is later (returning never makes an old approval vanish at once). Away: the full hold.
    /// </summary>
    public static DateTimeOffset Deadline(DateTimeOffset heldAt, DateTimeOffset expires, bool away, DateTimeOffset presentSince, TimeSpan atPc)
    {
        if (away) return expires;
        var d = (heldAt > presentSince ? heldAt : presentSince) + atPc;
        return d < expires ? d : expires;
    }
}

public enum PushKind { Approval, TileError, Sound }

public enum PushUrgency { None, Normal, High }

/// <summary>Whether an event reaches a locked phone. Only when nobody is at the PC and that phone's app isn't open.</summary>
public static class PushPolicy
{
    public static PushUrgency Decide(PushKind kind, bool away, bool appOpen, bool quietHours) =>
        !away || appOpen ? PushUrgency.None // at the PC nobody needs a buzz; an open app already shows it live
        : kind == PushKind.Approval ? PushUrgency.High // approvals get through quiet hours: they block work
        : quietHours ? PushUrgency.None
        : PushUrgency.Normal;
}
