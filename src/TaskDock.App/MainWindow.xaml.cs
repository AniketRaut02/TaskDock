using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TaskDock.Core.Presets;
using TaskDock.Platform;

namespace TaskDock.App;

/// <summary>
/// Borderless, topmost, never steals focus, hidden from taskbar and Alt-Tab, snaps to the nearest
/// corner of whichever monitor it is dropped on and grows toward the middle of the screen.
/// </summary>
public partial class MainWindow : Window
{
    const int HotkeyId = 1;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000, VK_D = 0x44;

    readonly MainViewModel _vm;
    readonly AppSettings _settings;
    nint _hwnd;

    internal MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _settings = settings;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Snap();
        Closed += (_, _) => Win32.UnregisterHotKey(_hwnd, HotkeyId);
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32.MakeToolWindow(_hwnd, noActivate: true);
        Win32.RoundCorners(_hwnd);
        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_D))
            Serilog.Log.Warning("Hotkey Ctrl+Alt+Shift+D is taken by another app");
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        if (_settings.AnchorX is { } x && _settings.AnchorY is { } y)
        {
            var wa = Win32.WorkAreaAt(x, y); // restore onto the monitor it was last on
            Win32.MoveTo(_hwnd, wa.Left, wa.Top);
        }
        Snap();
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY when wParam == HotkeyId:
                _vm.CycleMode();
                handled = true;
                break;
            case Win32.WM_DISPLAYCHANGE or Win32.WM_DPICHANGED or Win32.WM_SETTINGCHANGE:
                Dispatcher.BeginInvoke(Snap); // after WPF applies the new DPI / work area
                break;
        }
        return 0;
    }

    /// <summary>Keep the saved corner on the window's current monitor, 8 px (scaled) from the work-area edges.</summary>
    void Snap()
    {
        if (_hwnd == 0) return;
        // During SizeChanged the HWND still has its old size, so size from WPF's layout, in device pixels.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        var wa = Win32.WorkAreaOf(_hwnd);
        var m = (int)Math.Round(8 * dpi.DpiScaleX);
        var corner = _vm.Preset.Corner;
        var x = corner.EndsWith("Left", StringComparison.Ordinal) ? wa.Left + m : wa.Right - w - m;
        var y = corner.StartsWith("top", StringComparison.Ordinal) ? wa.Top + m : wa.Bottom - h - m;
        Win32.MoveTo(_hwnd, x, y);
        _settings.AnchorX = x + w / 2;
        _settings.AnchorY = y + h / 2;
    }

    /// <summary>Pick the nearest corner by window centre after a drag.</summary>
    void SnapToNearest()
    {
        Win32.GetWindowRect(_hwnd, out var r);
        var wa = Win32.WorkAreaOf(_hwnd);
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        var top = cy < (wa.Top + wa.Bottom) / 2;
        var left = cx < (wa.Left + wa.Right) / 2;
        _vm.Preset.Corner = (top ? "top" : "bottom") + (left ? "Left" : "Right");
        Snap();
        _settings.Save();
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Win32.GetWindowRect(_hwnd, out var before);
        try { DragMove(); } catch (InvalidOperationException) { return; }
        Win32.GetWindowRect(_hwnd, out var after);
        var moved = Math.Abs(before.Left - after.Left) + Math.Abs(before.Top - after.Top) > 4;
        if (moved) SnapToNearest();
        else if (_vm.IsPill) _vm.ExpandCommand.Execute(null);
    }

    /// <summary>Re-apply the preset's corner (after a preset switch).</summary>
    internal void Resnap() => Snap();

    // ---------- crosshair picker ----------

    PickerOverlay? _overlay;
    bool _picking;

    void Picker_Down(object sender, MouseButtonEventArgs e)
    {
        _picking = ((UIElement)sender).CaptureMouse();
        _overlay ??= new PickerOverlay();
        e.Handled = true; // don't start a window drag
    }

    void Picker_Move(object sender, MouseEventArgs e)
    {
        if (!_picking) return;
        var p = Desktop.Cursor();
        var h = Desktop.WindowAt(p.X, p.Y);
        if (h == 0) _overlay!.Hide();
        else _overlay!.ShowAt(Desktop.Frame(h));
    }

    void Picker_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        _picking = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _overlay!.Hide();
        e.Handled = true;
        var p = Desktop.Cursor();
        var h = Desktop.WindowAt(p.X, p.Y);
        if (h != 0) PickWindow(h);
    }

    internal void PickWindow(nint h)
    {
        var pid = Desktop.ProcessId(h);
        if (Desktop.ImagePath(pid) is not { } image)
        {
            _vm.ShowHint("Can't read that window's process. It may be running as administrator.");
            return;
        }
        var r = Resolver.Resolve(image, Desktop.Title(h), pid, _vm.Registry.Tiles);
        if (r.ClaudeChoices is not { Count: > 0 } sessions)
        {
            _vm.AddPin(r.Pin, r.Hint);
            return;
        }
        // A terminal can't be mapped to a session: let the user say which one it is.
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var s in sessions)
        {
            var item = new MenuItem { Header = $"Claude · {s.Label}   ({s.Match.GetValueOrDefault("cwd")})" };
            item.Click += (_, _) => _vm.AddPin(Resolver.ForClaude(s));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var generic = new MenuItem { Header = "Just watch this window" };
        generic.Click += (_, _) => _vm.AddPin(r.Pin);
        menu.Items.Add(generic);
        menu.IsOpen = true;
    }

    // ---------- file drop ----------

    static string[] DroppedFiles(DragEventArgs e) =>
        (e.Data.GetData(DataFormats.FileDrop) as string[] ?? [])
        .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).ToArray();

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        foreach (var f in DroppedFiles(e)) DropFile(f);
    }

    internal void DropFile(string f)
    {
        var target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Desktop.ShortcutTarget(f) : f;
        if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            _vm.ShowHint($"{Path.GetFileName(f)} doesn't point at a program TaskDock can watch.");
        else
            _vm.AddPin(Resolver.ForDroppedFile(f, target));
    }

    void Tile_Rename(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TileViewModel { PinTile: { } pin } vm) return;
        if (InputDialog.Ask("Rename tile", "Label (empty = use the source's name):", vm.Label) is { } label) _vm.Rename(pin, label);
    }

    void Tile_MouseEnter(object sender, MouseEventArgs e) => ((sender as FrameworkElement)?.DataContext as TileViewModel)?.Acknowledge();

    void TextInput_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Win32.MakeToolWindow(_hwnd, noActivate: false);
        Activate();
        ((UIElement)sender).Focus();
    }

    void TextInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not System.Windows.Controls.TextBox) Win32.MakeToolWindow(_hwnd, noActivate: true);
    }
}
