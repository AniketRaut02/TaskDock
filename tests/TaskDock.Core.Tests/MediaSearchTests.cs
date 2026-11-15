using TaskDock.Core.Media;

namespace TaskDock.Core.Tests;

public class MediaSearchTests
{
    [Fact]
    public void Spotify_and_youtube_uris_escape_the_query()
    {
        Assert.Equal("spotify:search:daft%20punk%20%26%20friends", MediaSearch.UriFor(SearchTarget.Spotify, "  daft punk & friends ")!.OriginalString);
        Assert.Equal("https://www.youtube.com/results?search_query=lo-fi%20%E3%83%93%E3%83%BC%E3%83%88",
            MediaSearch.UriFor(SearchTarget.YouTube, "lo-fi ビート")!.OriginalString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_searches_nothing(string? q) => Assert.Null(MediaSearch.UriFor(SearchTarget.YouTube, q));

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    [InlineData(@"C:\Tools\foobar2000.exe", "foobar2000")]
    public void Friendly_names(string appId, string expected) => Assert.Equal(expected, MediaSearch.FriendlyName(appId));

    [Fact]
    public void Enter_searches_where_the_music_is()
    {
        Assert.Equal(SearchTarget.Spotify, MediaSearch.DefaultFor("Spotify.exe"));
        Assert.Equal(SearchTarget.YouTube, MediaSearch.DefaultFor("Chrome"));
        Assert.Equal(SearchTarget.YouTube, MediaSearch.DefaultFor(null));
    }
}
