using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDock.Core.Presets;
using TaskDock.Core.Rules;
using TaskDock.Core.Tiles;
using TaskDock.Hub;

namespace TaskDock.App;

public sealed partial class TileViewModel(MainViewModel owner, string key) : ObservableObject
{
    Tile? _tile;
    PresetTile? _pin;

    public string Key { get; } = key;
    internal Tile? Tile => _tile;
    internal PresetTile? PinTile => _pin;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private StatusClass _class;
    [ObservableProperty] private Brush _classBrush = Palette.Of(StatusClass.Offline);
    [ObservableProperty] private bool _isPlaceholder;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _canLaunch;
    [ObservableProperty] private bool _canFocus;
    [ObservableProperty] private ApprovalViewModel? _approval;
    [ObservableProperty] private string? _moreApprovals;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    partial void OnDetailChanged(string? value) => OnPropertyChanged(nameof(HasDetail));

    bool _notify;
    public bool Notify
    {
        get => _notify;
        set { if (SetProperty(ref _notify, value) && _pin != null) owner.SetNotify(_pin, value); }
    }

    internal TileViewModel Update(Tile? t, PresetTile? pin, List<PendingApproval>? approvals)
    {
        _tile = t;
        _pin = pin;
        IsPinned = pin != null;
        IsPlaceholder = t == null;
        SetProperty(ref _notify, pin?.Notify ?? false, nameof(Notify));

        if (t == null)
        {
            Label = pin!.Label ?? Describe(pin);
            StateText = "Waiting";
            Detail = $"Waiting for {pin.Adapter}…";
            Class = StatusClass.Offline;
            HasProgress = false;
            CanLaunch = pin.Launch != null;
            CanFocus = false;
        }
        else
        {
            Label = pin?.Label ?? t.Label;
            StateText = t.State;
            Detail = t.Detail;
            Class = t.Class;
            HasProgress = t.Progress != null;
            Progress = t.Progress ?? 0;
            CanLaunch = t.Class == StatusClass.Offline && pin?.Launch != null;
            CanFocus = t.Adapter != "claude" && t.Class != StatusClass.Offline;
        }
        ClassBrush = Palette.Of(Class);

        var first = approvals?.FirstOrDefault();
        if (first == null) Approval = null;
        else if (Approval?.Id != first.Id) Approval = new ApprovalViewModel(first, owner); // keep the typed deny reason across refreshes
        MoreApprovals = approvals is { Count: > 1 } ? $"+{approvals.Count - 1} more queued" : null;
        return this;
    }

    static string Describe(PresetTile pin) =>
        pin.Match.TryGetValue("cwd", out var p) || pin.Match.TryGetValue("projectPath", out p) || pin.Match.TryGetValue("workspacePath", out p)
            ? ClaudeSessions.FolderName(p)
            : pin.Match.Values.FirstOrDefault() ?? pin.Adapter;

    [RelayCommand] void Pin() { if (_tile != null) owner.Pin(_tile); }
    [RelayCommand] void Unpin() { if (_pin != null) owner.Unpin(_pin); }
    [RelayCommand] void Launch() { if (_pin != null) owner.Launch(_pin); }
    [RelayCommand] void Focus() { if (_tile != null) owner.Focus(_tile); }
    public void Acknowledge() { if (_tile != null) owner.Acknowledge(_tile.Key); }
}

public sealed partial class ApprovalViewModel(PendingApproval p, MainViewModel owner) : ObservableObject
{
    public string Id => p.Id;
    public string Title => $"Allow {p.ToolName}?";
    public string Preview => p.Preview ?? p.ToolInputJson;
    /// <summary>The exact tool_input from the held request, never adapter-supplied text.</summary>
    public string FullInput => p.ToolInputJson;
    public bool CanAlwaysAllow => p.CanAlwaysAllow;

    [ObservableProperty] private bool _expanded;
    [ObservableProperty] private string _denyReason = "";

    [RelayCommand] void Allow() => owner.Resolve(Id, ApprovalChoice.Allow, null);
    [RelayCommand] void AlwaysAllow() => owner.Resolve(Id, ApprovalChoice.AlwaysAllow, null);
    [RelayCommand] void Deny() => owner.Resolve(Id, ApprovalChoice.Deny, DenyReason);
    [RelayCommand] void Terminal() => owner.Resolve(Id, ApprovalChoice.Terminal, null);
    [RelayCommand] void ToggleExpanded() => Expanded = !Expanded;
}
