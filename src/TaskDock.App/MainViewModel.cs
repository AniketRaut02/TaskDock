using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;
using TaskDock.Hub;

namespace TaskDock.App;

/// <summary>Owns tile state on the UI thread and rebuilds the visible tile list from registry + preset pins.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    static readonly string[] Modes = ["expanded", "compact", "pill"];

    internal readonly TileRegistry Registry;
    internal readonly ApprovalBroker Broker;
    internal Preset Preset;
    readonly Action _savePreset;
    readonly Func<string> _terminalTemplate;

    public ObservableCollection<TileViewModel> Tiles { get; } = [];
    public ObservableCollection<TileViewModel> Detected { get; } = [];

    [ObservableProperty] private string _mode = "expanded";
    [ObservableProperty] private bool _showDetected;
    [ObservableProperty] private string _pillText = "Idle";
    [ObservableProperty] private Brush _pillBrush = Palette.Of(StatusClass.Idle);
    [ObservableProperty] private int _detectedCount;
    [ObservableProperty] private bool _isEmpty = true;
    public bool AutoSort { get; set; } = true;
    public MediaViewModel? Media { get; set; }
    /// <summary>The source's top-level window, or 0 when it can't be mapped (Claude sessions).</summary>
    internal Func<Tile, nint> WindowOf { get; set; } = _ => 0;
    /// <summary>One-line hints for the user (shown as a tray notification).</summary>
    internal event Action<string>? Hint;
    internal void ShowHint(string text) => Hint?.Invoke(text);
    /// <summary>Hub command to an adapter (Unity play/stop/pause). Set by the App.</summary>
    internal Func<string, string, System.Text.Json.JsonElement?, Task<Core.Protocol.ResultBody>> SendCommand { get; set; } =
        (_, _, _) => Task.FromResult(new Core.Protocol.ResultBody("", false, "not connected"));

    internal async void Command(Tile t, string action, System.Text.Json.JsonElement? args = null)
    {
        var r = await SendCommand(t.Key, action, args);
        if (!r.Ok) ShowHint($"{t.Label}: {action} failed ({r.Error})");
    }

    /// <summary>Open and Show in folder run here from the item's local path, so the extension needs no downloads.open permission.</summary>
    internal void OpenDownload(string? path, bool showInFolder)
    {
        // The path comes from an adapter: only act on an existing local file.
        if (path == null || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            ShowHint("That file isn't there any more.");
            return;
        }
        try
        {
            if (showInFolder) Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex) { ShowHint($"Couldn't open {Path.GetFileName(path)}: {ex.Message}"); }
    }

    public bool IsPill => Mode == "pill";
    public bool IsExpanded => Mode == "expanded";

    internal MainViewModel(TileRegistry registry, ApprovalBroker broker, Preset preset, Action savePreset, Func<string> terminalTemplate)
    {
        Registry = registry;
        Broker = broker;
        Preset = preset;
        _savePreset = savePreset;
        _terminalTemplate = terminalTemplate;
        _mode = Modes.Contains(preset.Mode) ? preset.Mode : "expanded"; // field: restoring isn't a change to save
    }

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPill));
        OnPropertyChanged(nameof(IsExpanded));
        Preset.Mode = value;
        _savePreset();
    }

    [RelayCommand] public void CycleMode() => Mode = Modes[(Array.IndexOf(Modes, Mode) + 1) % Modes.Length];
    [RelayCommand] void Expand() => Mode = "expanded";
    [RelayCommand] void ToggleDetected() => ShowDetected = !ShowDetected;

    internal PresetTile? PinOf(Tile t) => Preset.Tiles.FirstOrDefault(p => PresetMatcher.Matches(p, t));

    public void Sync()
    {
        var pending = Broker.Pending.ToLookup(p => p.SessionId);
        var bound = new HashSet<string>();
        var desired = new List<TileViewModel>();
        var existing = Tiles.Concat(Detected).ToDictionary(v => v.Key);

        TileViewModel Vm(string key) => existing.TryGetValue(key, out var v) ? v : new TileViewModel(this, key);

        for (var i = 0; i < Preset.Tiles.Count; i++)
        {
            var pin = Preset.Tiles[i];
            var live = PresetMatcher.Bound(pin, Registry.Tiles);
            if (live.Count == 0) desired.Add(Vm("pin:" + i).Update(null, pin, null));
            foreach (var t in live.Where(t => bound.Add(t.Key)))
                desired.Add(Vm(t.Key).Update(t, pin, t.Adapter == "claude" ? pending[t.Instance].ToList() : null));
        }

        var detected = new List<TileViewModel>();
        foreach (var t in Registry.Tiles.Where(t => !bound.Contains(t.Key) && t.Class != StatusClass.Offline).OrderBy(t => t.ConnectedAt))
        {
            var approvals = t.Adapter == "claude" ? pending[t.Instance].ToList() : null;
            var vm = Vm(t.Key).Update(t, null, approvals);
            // New sources wait in the tray, except ones that need you now.
            if (t.Class is StatusClass.Attention or StatusClass.Error || approvals is { Count: > 0 }) desired.Add(vm);
            else detected.Add(vm);
        }

        if (AutoSort) desired = desired.OrderBy(v => v.Class is StatusClass.Attention or StatusClass.Error ? 0 : 1).ToList();
        Reconcile(Tiles, desired);
        Reconcile(Detected, detected);

        DetectedCount = detected.Count;
        IsEmpty = desired.Count == 0;
        if (detected.Count == 0) ShowDetected = false;

        var live2 = desired.Where(v => !v.IsPlaceholder).ToList();
        var worst = live2.Count == 0 ? StatusClass.Idle : live2.Min(v => v.Class);
        PillBrush = Palette.Of(worst);
        var parts = new List<string>();
        void Count(StatusClass c, string text) { var n = live2.Count(v => v.Class == c); if (n > 0) parts.Add($"{n} {text}"); }
        Count(StatusClass.Attention, "needs you");
        Count(StatusClass.Error, "failed");
        Count(StatusClass.Busy, "busy");
        Count(StatusClass.Done, "done");
        PillText = parts.Count == 0 ? "All quiet" : string.Join(" · ", parts);
    }

    static void Reconcile(ObservableCollection<TileViewModel> list, List<TileViewModel> desired)
    {
        for (var i = list.Count - 1; i >= 0; i--) if (!desired.Contains(list[i])) list.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var at = list.IndexOf(desired[i]);
            if (at < 0) list.Insert(i, desired[i]);
            else if (at != i) list.Move(at, i);
        }
    }

    // ---------- actions from tiles ----------

    internal void Pin(Tile t)
    {
        var pin = PresetMatcher.PinFor(t);
        pin.Launch = LaunchFor(t);
        AddPin(pin);
    }

    /// <summary>Adds a pin unless an identical one exists. New sources never add themselves; this is the user choosing.</summary>
    internal void AddPin(PresetTile pin, string? hint = null)
    {
        if (!Preset.Tiles.Any(p => p.Adapter == pin.Adapter && p.Match.Count == pin.Match.Count
                && p.Match.All(kv => pin.Match.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))))
        {
            Preset.Tiles.Add(pin);
            _savePreset();
        }
        if (hint != null) Hint?.Invoke(hint);
        Sync();
    }

    internal void Unpin(PresetTile pin)
    {
        Preset.Tiles.Remove(pin);
        if (pin.Adapter == "generic") Registry.Remove(Tile.MakeKey("generic", Resolver.GenericInstance(pin))); // only pins create generic tiles
        _savePreset();
        Sync();
    }

    internal void Rename(PresetTile pin, string? label)
    {
        pin.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        _savePreset();
        Sync();
    }

    internal void Focus(Tile t)
    {
        var h = WindowOf(t);
        if (h != 0) Platform.Desktop.Focus(h);
    }

    /// <summary>How to start this source again: the plan's launch commands per adapter.</summary>
    internal static JsonObject? LaunchFor(Tile t)
    {
        static JsonObject Cmd(string exe, params string[] args) =>
            new() { ["exe"] = exe, ["args"] = new JsonArray(args.Select(a => (JsonNode)a!).ToArray()) };
        return t.Adapter switch
        {
            "claude" => Resolver.ForClaude(t).Launch,
            "vscode" when t.Match.TryGetValue("workspacePath", out var ws) => Cmd("code", ws),
            "unity" when t.Match.TryGetValue("projectPath", out var pp) && t.Pid is { } pid && Platform.Desktop.ImagePath(pid) is { } exe
                => Cmd(exe, "-projectPath", pp),
            "visualstudio" when t.Match.TryGetValue("solutionPath", out var sln) && t.Pid is { } vsPid && Platform.Desktop.ImagePath(vsPid) is { } devenv
                => Cmd(devenv, sln),
            _ => null,
        };
    }

    // ---------- presets ----------

    /// <summary>Captures matchers, labels, Notify flags and launch commands (filled in from live tiles where missing).</summary>
    internal Preset SaveAs(string name)
    {
        var p = new Preset { Name = name.Trim(), Corner = Preset.Corner, Mode = Preset.Mode };
        foreach (var pin in Preset.Tiles)
        {
            var copy = new PresetTile
            {
                Adapter = pin.Adapter, Label = pin.Label, Notify = pin.Notify,
                Match = new Dictionary<string, string>(pin.Match), Launch = pin.Launch?.DeepClone().AsObject(),
            };
            copy.Launch ??= Registry.Tiles.Where(t => PresetMatcher.Matches(pin, t)).Select(LaunchFor).FirstOrDefault(l => l != null);
            p.Tiles.Add(copy);
        }
        Preset = p;
        _savePreset();
        Sync();
        return p;
    }

    /// <summary>Apply replaces the current tiles; merge adds the preset's pins that aren't already there.</summary>
    internal void Apply(Preset p, bool merge)
    {
        if (merge)
        {
            foreach (var pin in p.Tiles) AddPin(pin);
            return;
        }
        Preset = p;
        Mode = Modes.Contains(p.Mode) ? p.Mode : "expanded";
        Sync();
    }

    /// <summary>Starts every unbound pin that has a launch command, 2 s apart.</summary>
    internal async Task LaunchMissing()
    {
        var missing = Preset.Tiles.Where(p => p.Launch != null && !Registry.Tiles.Any(t => t.Class != StatusClass.Offline && PresetMatcher.Matches(p, t))).ToList();
        for (var i = 0; i < missing.Count; i++)
        {
            if (i > 0) await Task.Delay(2000);
            Launch(missing[i]);
        }
    }

    internal void SetNotify(PresetTile pin, bool on)
    {
        pin.Notify = on;
        _savePreset();
    }

    internal void Acknowledge(string key)
    {
        if (Registry.Get(key) is not { Class: StatusClass.Done }) return;
        Registry.Acknowledge(key);
        Sync();
    }

    internal void Resolve(string approvalId, ApprovalChoice choice, string? reason) => Broker.Resolve(approvalId, choice, reason);

    internal void Launch(PresetTile pin)
    {
        if (pin.Launch is not { } l) return;
        try
        {
            ProcessStartInfo psi;
            if (l["template"]?.GetValue<string>() == "terminal")
            {
                var line = _terminalTemplate()
                    .Replace("{cwd}", l["cwd"]?.GetValue<string>() ?? "")
                    .Replace("{cmd}", l["cmd"]?.GetValue<string>() ?? "");
                var exe = line.Split(' ', 2);
                psi = new ProcessStartInfo(exe[0], exe.Length > 1 ? exe[1] : "");
            }
            else
            {
                var args = (l["args"]?.AsArray() ?? []).Select(a => a!.GetValue<string>()).ToList();
                psi = new ProcessStartInfo(l["exe"]!.GetValue<string>()) { UseShellExecute = true };
                args.ForEach(psi.ArgumentList.Add);
                try { Process.Start(psi)?.Dispose(); return; }
                catch (Win32Exception) when (!Path.IsPathRooted(psi.FileName))
                {
                    // "code" is code.cmd on PATH, which ShellExecute won't find.
                    psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add(l["exe"]!.GetValue<string>());
                    args.ForEach(psi.ArgumentList.Add);
                }
            }
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Launch failed");
            Hint?.Invoke($"Couldn't launch: {ex.Message}");
        }
    }
}

static class Palette
{
    static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    static readonly Dictionary<StatusClass, Brush> Map = new()
    {
        [StatusClass.Attention] = B("#F5A524"), [StatusClass.Error] = B("#E5484D"), [StatusClass.Busy] = B("#3E8EF7"),
        [StatusClass.Done] = B("#30A46C"), [StatusClass.Idle] = B("#8B8D98"), [StatusClass.Offline] = B("#4A4C55"),
    };

    public static Brush Of(StatusClass c) => Map[c];
}
