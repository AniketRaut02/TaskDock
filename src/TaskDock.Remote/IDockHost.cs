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
}
