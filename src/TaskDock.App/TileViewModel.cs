using System.Collections.ObjectModel;
using System.Text.Json;
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
    [ObservableProperty] private bool _canPlay;
    [ObservableProperty] private bool _canStopPause;
    [ObservableProperty] private string _pauseText = "Pause";
    [ObservableProperty] private bool _showItems;
    [ObservableProperty] private string? _itemsToggleText;

    /// <summary>Downloads tile: the item list, expandable.</summary>
    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
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
            CanFocus = CanPlay = CanStopPause = false;
            SyncItems(null);
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
            CanFocus = t.Adapter is not ("claude" or "downloads") && t.Class != StatusClass.Offline; // neither reports a window
            CanPlay = t.Adapter == "unity" && t.State == "Idle";
            CanStopPause = t.Adapter == "unity" && t.State is "Playing" or "Paused";
            SyncItems(t.Adapter == "downloads" ? t.Items : null);
            PauseText = t.State == "Paused" ? "Resume" : "Pause";
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
    [RelayCommand] void Play() { if (_tile != null) owner.Command(_tile, "enterPlay"); }
    [RelayCommand] void Stop() { if (_tile != null) owner.Command(_tile, "exitPlay"); }
    [RelayCommand] void Pause() { if (_tile != null) owner.Command(_tile, "togglePause"); }
    [RelayCommand] void ToggleItems() => ShowItems = !ShowItems;

    void SyncItems(JsonElement? items)
    {
        Items.Clear();
        if (items is { ValueKind: JsonValueKind.Array } arr)
            foreach (var i in arr.EnumerateArray().Take(20)) Items.Add(new DownloadItemViewModel(i, this));
        ItemsToggleText = Items.Count == 0 ? null : (ShowItems ? "Hide files" : $"Files ({Items.Count})");
    }
    partial void OnShowItemsChanged(bool value) => ItemsToggleText = Items.Count == 0 ? null : (value ? "Hide files" : $"Files ({Items.Count})");

    internal void ItemCommand(string action, int downloadId) =>
        owner.Command(_tile!, action, JsonSerializer.SerializeToElement(new { downloadId }));
    internal void OpenItem(string? path, bool showInFolder) => owner.OpenDownload(path, showInFolder);
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

/// <summary>One download in the tile's list. Pause/Resume/Cancel go to the browser; Open/Show run locally.</summary>
public sealed partial class DownloadItemViewModel(JsonElement i, TileViewModel tile)
{
    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    readonly int _id = (int)(Num(i, "id") ?? -1);
    readonly string? _path = Str(i, "path");
    readonly string _state = Str(i, "state") ?? "";

    public string File { get; } = Str(i, "file") ?? "download";
    public double Progress { get; } = Num(i, "total") is > 0 and var t ? (Num(i, "bytes") ?? 0) / t : 0;
    public bool HasProgress => _state is "in_progress" or "paused" && Num(i, "total") is > 0;
    public string StateText => _state switch
    {
        "in_progress" => $"{Progress:P0}" + (Num(i, "speed") is > 0 and var s ? $" · {s / 1048576:0.0} MB/s" : ""),
        "paused" => $"Paused · {Progress:P0}",
        "complete" => "Done",
        "failed" => "Failed" + (Str(i, "error") is { } e ? $" · {e.ToLowerInvariant().Replace('_', ' ')}" : ""),
        "canceled" => "Canceled",
        "dangerous" => "Waiting for you in the browser",
        _ => _state,
    };
    public bool CanPause => _state == "in_progress";
    public bool CanResume => _state == "paused";
    public bool CanCancel => _state is "in_progress" or "paused";
    public bool CanOpen => _state == "complete" && _path != null;

    [RelayCommand] void Pause() => tile.ItemCommand("pause", _id);
    [RelayCommand] void Resume() => tile.ItemCommand("resume", _id);
    [RelayCommand] void Cancel() => tile.ItemCommand("cancel", _id);
    [RelayCommand] void Open() => tile.OpenItem(_path, false);
    [RelayCommand] void Show() => tile.OpenItem(_path, true);
}
