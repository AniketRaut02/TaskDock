using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using TaskDock.Core.Claude;
using TaskDock.Core.Presets;
using TaskDock.Core.Protocol;
using TaskDock.Core.Tiles;

namespace TaskDock.App;

static class Paths
{
    public static readonly string Roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskDock");
    public static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskDock");
    public static string Presets => Path.Combine(Roaming, "presets");
    public static string Settings => Path.Combine(Roaming, "settings.json");
    public static string ClaudeSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Wire.Json) : null; }
        catch (JsonException) { return null; } // corrupt file: start from defaults rather than crash
    }
}

sealed class AppSettings
{
    public string LastPreset { get; set; } = "Default";
    /// <summary>Centre of the widget in screen pixels; picks the monitor on startup.</summary>
    public int? AnchorX { get; set; }
    public int? AnchorY { get; set; }
    public bool Muted { get; set; }
    public string? QuietFrom { get; set; }
    public string? QuietTo { get; set; }
    public double Volume { get; set; } = 0.8;
    public int HoldSeconds { get; set; } = 120;
    public bool AutoSort { get; set; } = true;
    public bool HideWhenFullscreen { get; set; } = true;
    public bool SilentWhenFocused { get; set; } = true;
    /// <summary>Media bar: an app id the bar sticks to (e.g. Spotify), and whether the strip is folded.</summary>
    public string? MediaPinnedApp { get; set; }
    public bool MediaCollapsed { get; set; }
    public string TerminalTemplate { get; set; } = "wt.exe -d \"{cwd}\" {cmd}";

    public static AppSettings Load() => Paths.ReadJson<AppSettings>(Paths.Settings) ?? new AppSettings();
    public void Save() => Paths.WriteAtomic(Paths.Settings, JsonSerializer.Serialize(this, Wire.Json));
}

static class PresetStore
{
    static string FileOf(string name) => Path.Combine(Paths.Presets, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".json");

    public static Preset Load(string name) => Paths.ReadJson<Preset>(FileOf(name)) ?? new Preset { Name = name };

    public static void Save(Preset p) =>
        Paths.WriteAtomic(FileOf(p.Name), JsonSerializer.Serialize(p, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));

    public static IEnumerable<string> Names() =>
        Directory.Exists(Paths.Presets) ? Directory.EnumerateFiles(Paths.Presets, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>() : [];
}

/// <summary>Merges TaskDock's HTTP hooks into ~/.claude/settings.json, backing the file up first.</summary>
static class ClaudeHooksInstaller
{
    public static bool IsInstalled() => Read() is { } s && ClaudeHooks.IsInstalled(s);

    public static void Connect(int port, string token, int holdSeconds) =>
        Write(ClaudeHooks.Install(Read() ?? new JsonObject(), port, token, holdSeconds));

    public static void Disconnect()
    {
        if (Read() is { } s && ClaudeHooks.IsInstalled(s)) Write(ClaudeHooks.Uninstall(s));
    }

    /// <summary>The port can fall back and the token can rotate; keep installed hooks pointing at the live hub.</summary>
    public static bool SyncIfInstalled(int port, string token, int holdSeconds)
    {
        if (Read() is not { } s || ClaudeHooks.Installed(s) is not { } cur) return false;
        if (cur.Port == port && cur.Token == token) return false;
        Write(ClaudeHooks.Install(s, port, token, holdSeconds));
        return true;
    }

    /// <summary>Null when the file doesn't exist. Throws on invalid JSON so we never overwrite a file we can't parse.</summary>
    static JsonObject? Read()
    {
        var path = Paths.ClaudeSettings;
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object");
    }

    static void Write(JsonObject s)
    {
        var path = Paths.ClaudeSettings;
        if (File.Exists(path)) File.Copy(path, path + ".taskdock.bak", true);
        Paths.WriteAtomic(path, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Three bundled tones, replaceable by same-named WAVs in %APPDATA%\TaskDock\sounds.</summary>
sealed class Sounds
{
    readonly Dictionary<Sound, MediaPlayer> _players = new();

    public double Volume { get; set; } = 0.8;

    public void Play(Sound s)
    {
        if (s == Sound.None) return;
        if (!_players.TryGetValue(s, out var p))
        {
            p = new MediaPlayer();
            p.Open(new Uri(PathFor(s)));
            _players[s] = p;
        }
        p.Volume = Volume;
        p.Stop();
        p.Position = TimeSpan.Zero;
        p.Play();
    }

    static string PathFor(Sound s)
    {
        var user = Path.Combine(Paths.Roaming, "sounds", s + ".wav");
        if (File.Exists(user)) return user;
        var def = Path.Combine(Paths.Local, "sounds-default", s + ".wav");
        if (!File.Exists(def))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(def)!);
            File.WriteAllBytes(def, s switch
            {
                Sound.Complete => Tones((660, 90), (880, 140)),
                Sound.Attention => Tones((988, 90), (0, 60), (988, 90), (0, 60), (988, 90)),
                _ => Tones((440, 120), (330, 200)),
            });
        }
        return def;
    }

    /// <summary>16-bit mono PCM with a short fade so notes don't click. Freq 0 = rest.</summary>
    static byte[] Tones(params (int Hz, int Ms)[] notes)
    {
        const int rate = 22050;
        var samples = new List<short>();
        foreach (var (hz, ms) in notes)
        {
            var n = rate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.008));
                samples.Add((short)(hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * env * 9000));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        w.Write("RIFF"u8); w.Write(36 + samples.Count * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Count * 2);
        foreach (var s in samples) w.Write(s);
        return ms2.ToArray();
    }
}
