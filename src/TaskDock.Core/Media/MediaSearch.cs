namespace TaskDock.Core.Media;

public enum SearchTarget { Spotify, YouTube }

public static class MediaSearch
{
    /// <summary>Spotify opens on its results page (no auto-play); YouTube opens results in the default browser.</summary>
    public static Uri? UriFor(SearchTarget target, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return null;
        var e = Uri.EscapeDataString(q);
        return target == SearchTarget.Spotify
            ? new Uri("spotify:search:" + e)
            : new Uri("https://www.youtube.com/results?search_query=" + e);
    }

    /// <summary>Enter in the search box searches where the music is already playing.</summary>
    public static SearchTarget DefaultFor(string? currentAppId) =>
        currentAppId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true ? SearchTarget.Spotify : SearchTarget.YouTube;

    /// <summary>"Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Chrome" → a short chip label.</summary>
    public static string FriendlyName(string appId)
    {
        var id = appId.ToLowerInvariant();
        foreach (var (needle, name) in new[] { ("spotify", "Spotify"), ("chrome", "Chrome"), ("msedge", "Edge"), ("brave", "Brave"), ("firefox", "Firefox"), ("vlc", "VLC") })
            if (id.Contains(needle)) return name;
        var last = appId.Split('!', '\\', '/').Last();
        if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        return last.Length is > 0 and <= 24 ? last : "Media";
    }
}
