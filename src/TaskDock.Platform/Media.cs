using Windows.Media.Control;
using Windows.Storage.Streams;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace TaskDock.Platform;

public sealed record MediaInfo(
    string AppId, string Title, string Artist, bool Playing,
    bool CanPrevious, bool CanToggle, bool CanNext,
    double? Position, double? Duration, DateTimeOffset TimelineAt, string TrackKey);

/// <summary>
/// Windows' System Media Transport Controls (the API behind the volume flyout). YouTube in Chrome and the
/// Spotify desktop app both register with it. Changed fires on WinRT threads; callers marshal to their UI.
/// </summary>
public sealed class MediaSessions
{
    readonly SessionManager _manager;
    readonly HashSet<Session> _hooked = [];
    readonly Dictionary<string, byte[]?> _thumbs = new(); // per track; small LRU-free cache, tracks are few

    public event Action? Changed;

    MediaSessions(SessionManager manager)
    {
        _manager = manager;
        _manager.SessionsChanged += (_, _) => { Hook(); Changed?.Invoke(); };
        _manager.CurrentSessionChanged += (_, _) => Changed?.Invoke();
        Hook();
    }

    /// <summary>Null when the API isn't available (Windows 10 before 1809, or blocked by policy).</summary>
    public static async Task<MediaSessions?> CreateAsync()
    {
        try { return new MediaSessions(await SessionManager.RequestAsync()); }
        catch (Exception) { return null; }
    }

    /// <summary>App ids of every session, the one Windows considers current first.</summary>
    public IReadOnlyList<string> Apps()
    {
        var current = _manager.GetCurrentSession()?.SourceAppUserModelId;
        return _manager.GetSessions().Select(s => s.SourceAppUserModelId).Distinct()
            .OrderBy(a => a == current ? 0 : 1).ToList();
    }

    Session? Find(string? appId) =>
        (appId == null ? null : _manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId == appId))
        ?? _manager.GetCurrentSession();

    public async Task<MediaInfo?> SnapshotAsync(string? appId)
    {
        var s = Find(appId);
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            var c = info.Controls;
            var duration = (tl.EndTime - tl.StartTime).TotalSeconds;
            return new MediaInfo(
                s.SourceAppUserModelId, props?.Title ?? "", props?.Artist ?? "",
                info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                c.IsPreviousEnabled, c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled, c.IsNextEnabled,
                duration > 0 ? (tl.Position - tl.StartTime).TotalSeconds : null, duration > 0 ? duration : null,
                tl.LastUpdatedTime, s.SourceAppUserModelId + "|" + props?.Title + "|" + props?.Artist);
        }
        catch (Exception) { return null; } // the session closed under us
    }

    /// <summary>Thumbnail bytes for the session's current track, read once per track.</summary>
    public async Task<byte[]?> ThumbnailAsync(string appId, string trackKey)
    {
        if (_thumbs.TryGetValue(trackKey, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            if (Find(appId) is { } s && (await s.TryGetMediaPropertiesAsync())?.Thumbnail is { } refr)
            {
                using var stream = await refr.OpenReadAsync();
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
            }
        }
        catch (Exception) { }
        if (_thumbs.Count > 50) _thumbs.Clear();
        _thumbs[trackKey] = bytes;
        return bytes;
    }

    public async Task<bool> PreviousAsync(string? appId) => Find(appId) is { } s && await s.TrySkipPreviousAsync();
    public async Task<bool> TogglePlayPauseAsync(string? appId) => Find(appId) is { } s && await s.TryTogglePlayPauseAsync();
    public async Task<bool> NextAsync(string? appId) => Find(appId) is { } s && await s.TrySkipNextAsync();

    void Hook()
    {
        var sessions = _manager.GetSessions();
        foreach (var s in sessions)
        {
            if (!_hooked.Add(s)) continue;
            s.MediaPropertiesChanged += (_, _) => Changed?.Invoke();
            s.PlaybackInfoChanged += (_, _) => Changed?.Invoke();
            s.TimelinePropertiesChanged += (_, _) => Changed?.Invoke();
        }
        _hooked.RemoveWhere(h => !sessions.Contains(h));
    }
}
