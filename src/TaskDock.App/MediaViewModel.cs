using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDock.Core.Media;
using TaskDock.Platform;

namespace TaskDock.App;

public sealed record MediaChip(string AppId, string Name, bool Selected);

/// <summary>The strip at the bottom of the widget: whatever Windows says is playing. No Notify: media isn't a task.</summary>
public sealed partial class MediaViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly Action<string> _hint;
    readonly Dispatcher _ui;
    MediaSessions? _svc;
    MediaInfo? _info;
    string? _chosen;      // a chip you clicked, until that app's session goes away
    string? _thumbKey;
    bool _refreshQueued;

    public ObservableCollection<MediaChip> Apps { get; } = [];

    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private ImageSource? _thumb;
    [ObservableProperty] private ImageSource? _appIcon;
    [ObservableProperty] private bool _playing;
    [ObservableProperty] private bool _canPrevious;
    [ObservableProperty] private bool _canToggle;
    [ObservableProperty] private bool _canNext;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _collapsed;
    [ObservableProperty] private bool _showChips;
    [ObservableProperty] private string _query = "";

    partial void OnCollapsedChanged(bool value) { _settings.MediaCollapsed = value; _settings.Save(); }

    internal MediaViewModel(AppSettings settings, Action<string> hint, Dispatcher ui)
    {
        _settings = settings;
        _hint = hint;
        _ui = ui;
        _collapsed = settings.MediaCollapsed;
        // The timeline is only pushed now and then; move the progress line along locally while playing.
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateProgress(), ui).Start();
    }

    internal async Task StartAsync()
    {
        _svc = await MediaSessions.CreateAsync();
        if (_svc == null) { Serilog.Log.Warning("Media controls API unavailable; media bar disabled"); return; }
        _svc.Changed += QueueRefresh;
        await Refresh();
    }

    /// <summary>Changed arrives on WinRT threads, often in bursts: coalesce into one refresh on the UI thread.</summary>
    void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.BeginInvoke(async () => { _refreshQueued = false; await Refresh(); });
    }

    async Task Refresh()
    {
        if (_svc == null) return;
        var apps = _svc.Apps();
        if (_chosen != null && !apps.Contains(_chosen)) _chosen = null;
        var pinned = _settings.MediaPinnedApp;
        var target = pinned != null && apps.Contains(pinned) ? pinned : _chosen ?? apps.FirstOrDefault();

        _info = target == null ? null : await _svc.SnapshotAsync(target);
        HasSession = _info != null;
        Apps.Clear();
        foreach (var a in apps) Apps.Add(new MediaChip(a, MediaSearch.FriendlyName(a), a == _info?.AppId));
        ShowChips = apps.Count > 1;
        if (_info == null) { Changed?.Invoke(); return; }

        Title = _info.Title.Length > 0 ? _info.Title : MediaSearch.FriendlyName(_info.AppId);
        Artist = _info.Artist;
        AppIcon = AppIcons.ForMediaApp(_info.AppId); // Spotify's icon, or the browser's for YouTube
        Playing = _info.Playing;
        (CanPrevious, CanToggle, CanNext) = (_info.CanPrevious, _info.CanToggle, _info.CanNext);
        IsPinned = pinned == _info.AppId;
        UpdateProgress();
        Changed?.Invoke();

        if (_thumbKey != _info.TrackKey)
        {
            var key = _thumbKey = _info.TrackKey;
            _artHash = null; // never pair the new track with the old cover
            var bytes = await _svc.ThumbnailAsync(_info.AppId, key);
            if (_thumbKey != key) return; // a faster, newer refresh may have moved on to another track
            Thumb = ToImage(bytes);
            _artHash = bytes is { Length: > 0 } ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) : null;
            Changed?.Invoke();
        }
    }

    string? _artHash;

    /// <summary>Raised on the UI thread whenever what ToView returns may have changed.</summary>
    internal event Action? Changed;

    internal Core.Dock.MediaView? ToView() => _info is not { } i ? null : new(
        i.AppId, MediaSearch.FriendlyName(i.AppId), Title, Artist, Playing, CanPrevious, CanToggle, CanNext,
        i.Position, i.Duration, i.TimelineAt.Year > 2000 ? i.TimelineAt.ToUnixTimeMilliseconds() : null, _artHash,
        Apps.Select(c => new Core.Dock.MediaAppView(c.AppId, c.Name, c.Selected)).ToList(), IsPinned);

    void UpdateProgress()
    {
        if (_info is not { Position: { } pos, Duration: { } dur }) { HasProgress = false; return; }
        if (_info.Playing && _info.TimelineAt.Year > 2000) pos += (DateTimeOffset.Now - _info.TimelineAt).TotalSeconds;
        HasProgress = true;
        Progress = Math.Clamp(pos / dur, 0, 1);
    }

    static ImageSource? ToImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 96;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception) { return null; } // not an image we can decode
    }

    // Using the bar's controls makes it stick to that app: pausing must not hand the bar to whatever
    // Windows promotes to "current" next, or the following press would resume the wrong player.
    [RelayCommand] internal async Task Previous() { if (_svc != null && Stick() is { } app) await _svc.PreviousAsync(app); }
    [RelayCommand] internal async Task TogglePlay() { if (_svc != null && Stick() is { } app) await _svc.TogglePlayPauseAsync(app); }
    [RelayCommand] internal async Task Next() { if (_svc != null && Stick() is { } app) await _svc.NextAsync(app); }

    string? Stick() => _chosen = _info?.AppId;
    [RelayCommand] void ToggleCollapsed() => Collapsed = !Collapsed;

    [RelayCommand]
    internal async Task Select(string appId)
    {
        _chosen = appId;
        if (_settings.MediaPinnedApp != null && _settings.MediaPinnedApp != appId) { _settings.MediaPinnedApp = null; _settings.Save(); }
        await Refresh();
    }

    /// <summary>Pin one app (e.g. always Spotify) so the bar ignores whatever else starts playing.</summary>
    [RelayCommand]
    async Task TogglePin()
    {
        _settings.MediaPinnedApp = IsPinned ? null : _info?.AppId;
        _settings.Save();
        await Refresh();
    }

    internal Task SetPinned(bool on) => on == IsPinned ? Task.CompletedTask : TogglePin();

    [RelayCommand] void SearchSpotify() => Search(SearchTarget.Spotify, Query);
    [RelayCommand] void SearchYouTube() => Search(SearchTarget.YouTube, Query);
    public void SearchDefault() => Search(MediaSearch.DefaultFor(_info?.AppId), Query);

    internal void Search(SearchTarget target, string query)
    {
        if (MediaSearch.UriFor(target, query) is not { } uri) return;
        try { Process.Start(new ProcessStartInfo(uri.OriginalString) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { _hint(target == SearchTarget.Spotify ? "Spotify search needs the Spotify desktop app." : "Couldn't open the browser."); }
    }
}
