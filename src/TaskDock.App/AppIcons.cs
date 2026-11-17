using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskDock.Core.Media;
using TaskDock.Core.Tiles;
using TaskDock.Platform;

namespace TaskDock.App;

/// <summary>
/// The icon Windows shows for each source's own program (Unity, Visual Studio, Brave, Spotify…), so tiles with the
/// same name are told apart. Borrowed from the installed app, so no brand logos ship with TaskDock.
/// One frozen image per program path, kept for the app's life: icons are converted once, never per update.
/// </summary>
static class AppIcons
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForExe(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var image)) return image;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze(); // the bitmap is a copy, so the icon handle is freed by the using
            }
        }
        catch (Exception) { image = null; } // gone or unreadable: the tile shows a glyph instead
        return Cache[path] = image;
    }

    /// <summary>For sources that don't report a PID: the first running process with this name.</summary>
    public static ImageSource? ForProcess(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
                if (Desktop.ImagePath(p.Id) is { } path) return ForExe(path);
        return null;
    }

    public static ImageSource? ForTile(Tile t) => t.Adapter switch
    {
        "claude" => ForProcess("claude"),
        "downloads" => ForProcess(t.Match.GetValueOrDefault("browser") switch { "edge" => "msedge", { } b => b, null => "chrome" }),
        _ => t.Pid is { } pid ? ForExe(Desktop.ImagePath(pid)) : null,
    };

    /// <summary>A media session's app ("Spotify.exe", "Brave", "SpotifyAB.SpotifyMusic_…!Spotify") → its running program.</summary>
    public static ImageSource? ForMediaApp(string appId) => MediaSearch.FriendlyName(appId) switch
    {
        "Edge" => ForProcess("msedge"),
        var name => ForProcess(name),
    };

    /// <summary>Icon (Icons.xaml name) shown until, or instead of, the program's own icon. The phone uses the same three.</summary>
    public static string GlyphFor(string adapter) => adapter switch
    {
        "claude" => "terminal",
        "downloads" => "download",
        _ => "window",
    };
}
