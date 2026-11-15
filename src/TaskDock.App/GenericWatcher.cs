using System.IO;
using System.Text.RegularExpressions;
using TaskDock.Core.Presets;
using TaskDock.Core.Tiles;
using W = TaskDock.Platform.Desktop;

namespace TaskDock.App;

/// <summary>
/// In-process adapter for generic pins. One shared 1 s tick: IsWindow, IsHungAppWindow, GetWindowText.
/// A pin binds to the first matching top-level window and rebinds after a relaunch.
/// </summary>
sealed class GenericWatcher
{
    sealed record Bound(nint Hwnd, int Pid, string State, string Title);

    readonly Dictionary<string, Bound?> _bound = new(); // instance → window (null = was bound, now closed)

    public nint WindowOf(string instance) => _bound.GetValueOrDefault(instance)?.Hwnd ?? 0;

    public IEnumerable<StateEvent> Tick(IEnumerable<PresetTile> pins)
    {
        var events = new List<StateEvent>();
        var generic = pins.Where(p => p.Adapter == "generic" && p.Match.ContainsKey("process")).ToList();
        var live = generic.Select(Resolver.GenericInstance).ToHashSet();
        foreach (var gone in _bound.Keys.Where(k => !live.Contains(k)).ToList()) _bound.Remove(gone);

        Dictionary<int, string?>? names = null; // pid → exe, only built when something needs binding
        List<nint>? windows = null;

        foreach (var pin in generic)
        {
            var id = Resolver.GenericInstance(pin);
            var b = _bound.GetValueOrDefault(id);
            if (b == null || !W.IsWindow(b.Hwnd))
            {
                windows ??= W.TopLevel();
                names ??= new();
                var hwnd = windows.FirstOrDefault(h => Matches(pin, h, names));
                if (hwnd == 0)
                {
                    if (b != null) // it was running: report Closed once
                    {
                        events.Add(Event(id, pin, "Closed", b.Title));
                        _bound[id] = null;
                    }
                    continue;
                }
                b = new Bound(hwnd, W.ProcessId(hwnd), "", "");
            }

            var state = W.IsHung(b.Hwnd) ? "Not responding" : "Running";
            var title = W.Title(b.Hwnd);
            if (state != b.State || title != b.Title) events.Add(Event(id, pin, state, title, b.State == "", b.Pid));
            _bound[id] = b with { State = state, Title = title };
        }
        return events;
    }

    static StateEvent Event(string id, PresetTile pin, string state, string title, bool connected = false, int? pid = null) =>
        new("generic", id, state, title.Length > 0 ? title : pin.Match["process"], pin.Match["process"],
            Match: new Dictionary<string, string>(pin.Match), Pid: pid, Connected: connected);

    static bool Matches(PresetTile pin, nint h, Dictionary<int, string?> names)
    {
        var pid = W.ProcessId(h);
        if (!names.TryGetValue(pid, out var exe)) names[pid] = exe = W.ImagePath(pid) is { } p ? Path.GetFileName(p) : null;
        if (exe == null || !exe.Equals(pin.Match["process"], StringComparison.OrdinalIgnoreCase)) return false;
        return !pin.Match.TryGetValue("titleRegex", out var rx)
            || Regex.IsMatch(W.Title(h), rx, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    }
}
