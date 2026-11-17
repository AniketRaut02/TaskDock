using TaskDock.Core.Dock;

namespace TaskDock.Remote;

/// <summary>The only way Remote reaches the app. Remote never references WPF.</summary>
public interface IDockHost
{
    DockSnapshot? Snapshot { get; }
    /// <summary>Raised with each new snapshot (on the app's UI thread: handlers must not block).</summary>
    event Action<DockSnapshot>? Changed;
    /// <summary>"Allow Pixel 8 to control TaskDock?" on the PC. Only a yes activates a paired phone.</summary>
    Task<bool> ConfirmPairingAsync(string deviceName, CancellationToken ct);

    /// <summary>A held approval as it is now, for binding a fingerprint to its exact input.</summary>
    ApprovalInput? Approval(string id);
    Requirement Requirement(DockAction action);
    /// <summary>Re-checks the action against live state and runs it on the UI thread, as the widget's own buttons do.</summary>
    Task<DockResult> RunAsync(DockAction action);
    /// <summary>The widget's normal hint, so remote control is never invisible at the PC.</summary>
    void Notice(string text);
    /// <summary>A JPEG of the tile's window, or why not: not_found, minimized, blank.</summary>
    Task<(byte[]? Jpeg, string? Code)> PeekAsync(string tileKey);
}
