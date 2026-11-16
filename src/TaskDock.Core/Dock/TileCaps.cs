using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Dock;

/// <summary>What a tile can do right now. One rule set for the widget's buttons and the phone's.</summary>
public sealed record TileCaps(
    bool Play, bool Stop, bool Pause, string PauseText, bool Launch,
    [property: System.Text.Json.Serialization.JsonIgnore] bool Focus, // acts on the PC's screen: never offered remotely
    bool Peek, bool Pin, bool Unpin, bool Ack)
{
    /// <summary>A live tile, a pin waiting for its source (t null), or both.</summary>
    public static TileCaps For(Tile? t, PresetTile? pin)
    {
        if (t == null)
            return new(false, false, false, "Pause", Launch: pin?.Launch != null, false, false, Pin: false, Unpin: pin != null, Ack: false);

        var focus = t.Adapter is not ("claude" or "downloads") && t.Class != StatusClass.Offline; // neither reports a window
        var playing = t.Adapter == "unity" && t.State is "Playing" or "Paused";
        return new(
            Play: t.Adapter == "unity" && t.State == "Idle",
            Stop: playing, Pause: playing, PauseText: t.State == "Paused" ? "Resume" : "Pause",
            Launch: t.Class == StatusClass.Offline && pin?.Launch != null,
            Focus: focus, Peek: focus,
            Pin: pin == null, Unpin: pin != null,
            Ack: t.Class == StatusClass.Done);
    }
}
