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
    readonly Func<TimeSpan> _holdWindow;
    readonly Action<string, bool> _applyPreset;
    readonly string _epoch = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    long _rev;
    List<string> _presetNames = [];
    long _presetNamesAt = long.MinValue;

    public DockSnapshot? Snapshot { get; private set; }
    /// <summary>Raised on the UI thread with each new snapshot.</summary>
    public event Action<DockSnapshot>? Changed;

    public DockHost(MainViewModel vm, Dispatcher ui, Func<TimeSpan> holdWindow, Action<string, bool> applyPreset)
    {
        (_vm, _ui, _holdWindow, _applyPreset) = (vm, ui, holdWindow, applyPreset);
        vm.SnapshotChanged += Rebuild;
        if (vm.Media != null) vm.Media.Changed += Rebuild;
    }

    void Rebuild()
    {
        static (Tile?, PresetTile?) Pair(TileViewModel v) => (v.Tile, v.PinTile);
        Snapshot = SnapshotBuilder.Build(_epoch, ++_rev, _vm.Tiles.Select(Pair), _vm.Detected.Select(Pair),
            _vm.Broker.Pending.Select(ToInput), _vm.Registry.Tiles.ToList(), _vm.Media?.ToView(),
            new PresetsView(_vm.Preset.Name, PresetNames()), new FlagsView(Away: false, KeepAwake: false));
        Serilog.Log.Debug("Dock snapshot rev {Rev}", _rev);
        Changed?.Invoke(Snapshot);
    }

    // shortcut: a preset saved from another TaskDock window shows up within 5 s; watch the folder if that's ever too slow.
    List<string> PresetNames()
    {
        if (Environment.TickCount64 - _presetNamesAt > 5000) (_presetNames, _presetNamesAt) = (PresetStore.Names().ToList(), Environment.TickCount64);
        return _presetNames;
    }

    ApprovalInput ToInput(PendingApproval p) =>
        new(p.Id, p.SessionId, p.ToolName, p.Preview, p.ToolInputJson, p.CanAlwaysAllow, p.Expires - _holdWindow(), p.Expires);

    /// <summary>Re-checks and runs one action on the UI thread, where the widget's own buttons run.</summary>
    public Task<DockResult> RunAsync(DockAction action) => _ui.InvokeAsync(() => ActionRouter.RunAsync(action, this)).Task.Unwrap();

    public Requirement Requirement(DockAction action) => ActionRouter.Requirement(action, this);

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
}
