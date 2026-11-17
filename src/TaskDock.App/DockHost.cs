using System.Text.Json;
using System.Windows.Threading;
using TaskDock.Core.Dock;
using TaskDock.Core.Media;
using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;
using TaskDock.Hub;

namespace TaskDock.App;

/// <summary>
/// The widget as a second client sees it: an immutable snapshot after each Sync, and the catalog actions run through
/// the same methods the widget's buttons call, on the UI thread.
/// </summary>
sealed class DockHost : IDockOps, Remote.IDockHost
{
    readonly MainViewModel _vm;
    readonly Dispatcher _ui;
    /// <summary>When each approval goes back to the terminal (Away mode decides); set by the App.</summary>
    internal Func<PendingApproval, DateTimeOffset> DeadlineOf { get; set; } = p => p.Expires;
    internal Func<FlagsView> Flags { get; set; } = () => new(Away: false, KeepAwake: false);
    /// <summary>Raised once per new held approval, after the snapshot that first shows it (the push trigger).</summary>
    internal event Action<ApprovalView>? ApprovalAdded;
    HashSet<string> _seenApprovals = [];
    readonly Action<string, bool> _applyPreset;
    readonly string _epoch = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    long _rev;
    List<string> _presetNames = [];
    long? _presetNamesAt; // null until first read (long.MinValue overflowed the age check, so names never loaded)

    public DockSnapshot? Snapshot { get; private set; }
    /// <summary>Raised on the UI thread with each new snapshot.</summary>
    public event Action<DockSnapshot>? Changed;

    public DockHost(MainViewModel vm, Dispatcher ui, Action<string, bool> applyPreset)
    {
        (_vm, _ui, _applyPreset) = (vm, ui, applyPreset);
        vm.SnapshotChanged += Rebuild;
        if (vm.Media != null) vm.Media.Changed += Rebuild;
    }

    void Rebuild()
    {
        static (Tile?, PresetTile?) Pair(TileViewModel v) => (v.Tile, v.PinTile);
        Snapshot = SnapshotBuilder.Build(_epoch, ++_rev, _vm.Tiles.Select(Pair), _vm.Detected.Select(Pair),
            _vm.Broker.Pending.Select(ToInput), _vm.Registry.Tiles.ToList(), _vm.Media?.ToView(),
            new PresetsView(_vm.Preset.Name, PresetNames()), Flags());
        Serilog.Log.Debug("Dock snapshot rev {Rev}", _rev);
        Changed?.Invoke(Snapshot);

        var now = Snapshot.Approvals.Select(a => a.Id).ToHashSet();
        foreach (var a in Snapshot.Approvals.Where(a => !_seenApprovals.Contains(a.Id))) ApprovalAdded?.Invoke(a);
        _seenApprovals = now;
    }

    // shortcut: a preset saved from another TaskDock window shows up within 5 s; watch the folder if that's ever too slow.
    List<string> PresetNames()
    {
        if (_presetNamesAt is not { } at || Environment.TickCount64 - at > 5000) (_presetNames, _presetNamesAt) = (PresetStore.Names().ToList(), Environment.TickCount64);
        return _presetNames;
    }

    ApprovalInput ToInput(PendingApproval p) =>
        new(p.Id, p.SessionId, p.ToolName, p.Preview, p.ToolInputJson, p.CanAlwaysAllow, p.HeldAt, DeadlineOf(p));

    /// <summary>Re-checks and runs one action on the UI thread, where the widget's own buttons run.</summary>
    public Task<DockResult> RunAsync(DockAction action) => _ui.InvokeAsync(() => ActionRouter.RunAsync(action, this)).Task.Unwrap();

    public Requirement Requirement(DockAction action) => ActionRouter.Requirement(action, this);

    public void Notice(string text) => _ui.BeginInvoke(() => _vm.ShowHint(text));

    /// <summary>The device name comes from the phone: it is shown as plain text only.</summary>
    public Task<bool> ConfirmPairingAsync(string deviceName, CancellationToken ct) =>
        _ui.InvokeAsync(() => ConfirmDialog.AskAsync("TaskDock Remote", $"Allow \"{deviceName}\" to control TaskDock?\n\nOnly say yes if you just paired this phone yourself.",
            "Allow", "Don't allow", ct)).Task.Unwrap();

    // ---------- IDockOps: live state ----------

    public Tile? Tile(string key) => _vm.Registry.Get(key);
    public PresetTile? Pin(string pinId) => _vm.Preset.Tiles.FirstOrDefault(p => p.Id == pinId);
    public PresetTile? PinOf(Tile t) => _vm.PinOf(t);
    public Tile? Bound(PresetTile pin) => PresetMatcher.Bound(pin, _vm.Registry.Tiles).FirstOrDefault();
    public ApprovalInput? Approval(string id) => _vm.Broker.Pending.FirstOrDefault(p => p.Id == id) is { } p ? ToInput(p) : null;
    public MediaView? Media => _vm.Media?.ToView();
    public bool HasPreset(string name) => PresetStore.Names().Contains(name, StringComparer.OrdinalIgnoreCase);

    // ---------- IDockOps: the widget's own operations ----------

    public bool Resolve(string approvalId, ApprovalDecision d, string? reason) => _vm.Broker.Resolve(approvalId, d switch
    {
        ApprovalDecision.Allow => ApprovalChoice.Allow,
        ApprovalDecision.AlwaysAllow => ApprovalChoice.AlwaysAllow,
        ApprovalDecision.Deny => ApprovalChoice.Deny,
        _ => ApprovalChoice.Terminal,
    }, reason);

    public Task<string?> Command(Tile t, string command, JsonElement? args) => _vm.CommandAsync(t, command, args);
    public void Acknowledge(string tileKey) => _vm.Acknowledge(tileKey);
    public void Pin(Tile t) => _vm.Pin(t);
    public void Unpin(PresetTile pin) => _vm.Unpin(pin);
    public void Rename(PresetTile pin, string? label) => _vm.Rename(pin, label);
    public void SetNotify(PresetTile pin, bool on) { _vm.SetNotify(pin, on); _vm.Sync(); } // the widget's toggle updates itself; a remote one needs the repaint
    public void Launch(PresetTile pin) => _vm.Launch(pin);
    public void ApplyPreset(string name, bool merge) => _applyPreset(name, merge);
    public Task LaunchMissing() => _vm.LaunchMissing();
    public Task MediaPrev() => _vm.Media?.Previous() ?? Task.CompletedTask;
    public Task MediaToggle() => _vm.Media?.TogglePlay() ?? Task.CompletedTask;
    public Task MediaNext() => _vm.Media?.Next() ?? Task.CompletedTask;
    public Task MediaSelect(string appId) => _vm.Media?.Select(appId) ?? Task.CompletedTask;
    public Task MediaPin(bool on) => _vm.Media?.SetPinned(on) ?? Task.CompletedTask;
    public void MediaSearch(SearchTarget target, string query) => _vm.Media?.Search(target, query);

    public void Lock()
    {
        Serilog.Log.Information("Locking the PC (from a phone)");
        Platform.Win32.LockWorkStation();
    }

    public void Sleep()
    {
        Serilog.Log.Information("Sleeping in 3 s (from a phone)");
        _ = Task.Delay(3000).ContinueWith(_ => Platform.Win32.Sleep(), TaskScheduler.Default); // the result reaches the phone first
    }

    /// <summary>Only tiles whose caps allow Peek, via the same window lookup Focus uses. The capture runs off the UI thread.</summary>
    public async Task<(byte[]? Jpeg, string? Code)> PeekAsync(string tileKey)
    {
        var hwnd = await _ui.InvokeAsync(() =>
            _vm.Registry.Get(tileKey) is { } t && TileCaps.For(t, _vm.PinOf(t)).Peek ? _vm.WindowOf(t) : 0);
        return hwnd == 0 ? (null, "not_found") : await Task.Run(() => WindowCapture.Capture(hwnd));
    }
}
