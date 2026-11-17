using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using Serilog;
using TaskDock.Core.Dock;
using TaskDock.Core.Notify;
using TaskDock.Core.Tiles;
using Sound = TaskDock.Core.Tiles.Sound;
using TaskDock.Hub;
using TaskDock.Platform;
using TaskDock.Remote;

namespace TaskDock.App;

/// <summary>Composition root: hub → channel → registry (UI thread, 10 Hz) → view model, notify, tray.</summary>
public partial class App : Application
{
    /// <summary>The browser extension's ID is fixed by the public key in adapters/chrome/manifest.json.</summary>
    const string DownloadsExtensionOrigin = "chrome-extension://dojcaemiadcannmfbinpkfjpnhggadej";

    Mutex? _single;
    HubServer? _hub;
    HubOptions? _hubOptions;
    TaskbarIcon? _tray;
    MainWindow? _window;
    MainViewModel? _vm;
    DockHost? _dock;
    RemoteControl? _remote;
    RemoteWindow? _remoteWindow;
    AppSettings _settings = new();
    bool _userHidden;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\TaskDock.Widget", out var first);
        if (!first) { Shutdown(); return; } // a second hub would fight over hub.json

        Directory.CreateDirectory(Paths.Local);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(Paths.Local, "logs", "taskdock-.log"), rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true, fileSizeLimitBytes: 1_000_000, retainedFileCountLimit: 5)
            .CreateLogger();
        DispatcherUnhandledException += (_, a) => { Log.Error(a.Exception, "Unhandled UI exception"); a.Handled = true; };

        try { await Start(); }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(ex.Message, "TaskDock could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    async Task Start()
    {
        _settings = AppSettings.Load();
        var preset = PresetStore.Load(_settings.LastPreset);

        var events = Channel.CreateUnbounded<StateEvent>(new UnboundedChannelOptions { SingleReader = true });
        var broker = new ApprovalBroker();
        _hub = new HubServer(_hubOptions = new HubOptions
        {
            HoldWindow = TimeSpan.FromSeconds(_settings.HoldSeconds),
            Logging = l => l.AddSerilog(Log.Logger),
            AllowedOrigins = [DownloadsExtensionOrigin],
            IsHung = IsMainWindowHung,
        }, events.Writer, broker);
        await _hub.StartAsync();
        Log.Information("Hub listening on 127.0.0.1:{Port}", _hub.Port);

        try
        {
            if (ClaudeHooksInstaller.SyncIfInstalled(_hub.Port, _hub.Token, _settings.HoldSeconds))
                Log.Information("Claude Code hooks re-pointed at port {Port}", _hub.Port);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not check Claude Code hooks"); }

        var registry = new TileRegistry(TimeProvider.System);
        _vm = new MainViewModel(registry, broker, preset, () => PresetStore.Save(_vm?.Preset ?? preset), () => _settings.TerminalTemplate)
        {
            AutoSort = _settings.AutoSort,
        };
        _vm.SendCommand = (key, action, args) => _hub.SendCommandAsync(key, action, args);
        var generic = new GenericWatcher();
        _vm.WindowOf = t => WindowOf(t, generic);

        var sounds = new Sounds { Volume = _settings.Volume };
        var notify = new NotifyEngine(TimeProvider.System)
        {
            Muted = _settings.Muted,
            QuietFrom = TimeOnly.TryParse(_settings.QuietFrom, out var qf) ? qf : null,
            QuietTo = TimeOnly.TryParse(_settings.QuietTo, out var qt) ? qt : null,
            IsEnabled = t => _vm.PinOf(t)?.Notify == true,
            // Stay silent when you're already looking at the source (Claude sessions can't be mapped to a window).
            IsForeground = t => _settings.SilentWhenFocused && _vm.WindowOf(t) is var h && h != 0 && h == Platform.Desktop.Foreground(),
        };
        // Pushes carry the tile's name and state only, never the detail line, a command or a path.
        string Label(Tile t) => _vm!.PinOf(t)?.Label ?? t.Label;
        void Push(PushKind kind, Tile t) => _remote?.Notify(new PushMessageFor(kind, Label(t), t.State, "tile-" + t.Key), notify.QuietNow);
        void Play(Sound s, Tile t)
        {
            if (s == Sound.None) return;
            Log.Information("Sound {Sound} for {Adapter}:{Instance}", s, t.Adapter, t.Instance);
            sounds.Play(s);
            Push(PushKind.Sound, t);
        }
        registry.Transition += (t, prevClass, prevState) =>
        {
            Log.Information("Tile {Adapter}:{Instance} {Prev} -> {State}", t.Adapter, t.Instance, prevState, t.State); // state names only, no content
            if (t.Class == StatusClass.Error && prevClass != StatusClass.Error) Push(PushKind.TileError, t);
            Play(notify.OnTransition(t, prevClass, prevState), t);
        };

        _vm.Media = new MediaViewModel(_settings, text => _vm.ShowHint(text), Dispatcher);
        _dock = new DockHost(_vm, Dispatcher, ApplyPreset);
        _window = new MainWindow(_vm, _settings);
        _vm.Sync();
        _window.Show();
        _tray = BuildTray(notify, sounds);
        _vm.Hint += text => _tray.ShowNotification("TaskDock", text);
        await _vm.Media.StartAsync();

        _remote = new RemoteControl(_dock, _settings, ApplyHold);
        var remote = _remote;
        _dock.DeadlineOf = p => remote.Deadline(p, TimeSpan.FromSeconds(_settings.HoldSeconds));
        _dock.Flags = () => new FlagsView(remote.Away, remote.KeepingAwake);
        _dock.ApprovalAdded += a => remote.Notify(new PushMessageFor(PushKind.Approval, "Claude needs approval", $"{a.Tool} · {a.Project}",
            "approval-" + a.Id, a.Id, DateTimeOffset.FromUnixTimeMilliseconds(a.Deadline)), notify.QuietNow);
        if (_settings.RemoteEnabled && await _remote.EnableAsync() is { } problem)
        {
            Log.Warning("Remote stayed off: {Problem}", problem);
            _vm.ShowHint("Phone access is off: " + problem);
        }
        if (_remote.Server?.HostChanged == true) _vm.ShowHint(HostChangedText);

        // Drain on the UI thread at 10 Hz: every event reaches Notify, the UI repaints once per tick.
        var approvalsChanged = 1;
        broker.Changed += () => Interlocked.Exchange(ref approvalsChanged, 1);
        var tick = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            try { Tick(); }
            catch (Exception ex) { Log.Error(ex, "Tick failed"); } // one bad tick must never stop the widget updating
        }, Dispatcher);
        timer.Start();

        void Tick()
        {
            var dirty = Interlocked.Exchange(ref approvalsChanged, 0) == 1;
            while (events.Reader.TryRead(out var ev)) { registry.Apply(ev); dirty = true; }
            if (notify.Tick() is var (settled, tile) && tile != null) Play(settled, tile); // Unity's settle window
            if (++tick % 10 == 0)
            {
                foreach (var ev in generic.Tick(_vm.Preset.Tiles)) { registry.Apply(ev); dirty = true; }
                if (registry.Sweep().Count > 0) dirty = true;
                UpdateVisibility();
                var flags = (remote.Away, remote.KeepingAwake);
                remote.Tick(broker, registry, TimeSpan.FromSeconds(_settings.HoldSeconds));
                if (flags != (remote.Away, remote.KeepingAwake)) dirty = true; // the phone shows the Away badge and real deadlines
            }
            if (dirty)
            {
                _vm.Sync();
                UpdateTrayBadge();
            }
        }
    }

    /// <summary>Windows' own "not responding" check on a process's main window (called from the hub's threads).</summary>
    static bool IsMainWindowHung(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle != 0 && Win32.IsHungAppWindow(p.MainWindowHandle);
        }
        catch (ArgumentException) { return false; } // exited
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Hide while something is fullscreen (games, presentations, fullscreen video).</summary>
    void UpdateVisibility()
    {
        if (_window == null) return;
        var show = !_userHidden && !(_settings.HideWhenFullscreen && Win32.SomethingIsFullscreen());
        if (show && !_window.IsVisible) _window.Show();
        else if (!show && _window.IsVisible) _window.Hide();
    }

    // ---------- tray ----------

    // Badges are drawn once and reused: regenerating one per change (GeneratedIconSource) leaked GDI objects
    // until Windows' 10,000-per-process limit (found by the Phase 7 soak).
    readonly Dictionary<string, System.Drawing.Icon> _badges = new();
    string _badge = "";
    System.Drawing.Icon? _shown;

    System.Drawing.Icon Badge(string text)
    {
        if (_badges.TryGetValue(text, out var icon)) return icon;
        if (text == "T" && System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) is { } logo) return _badges[text] = logo; // nothing waiting: the app logo
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var font = new System.Drawing.Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
        using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x3E, 0x8E, 0xF7)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.FillEllipse(fill, 1, 1, 30, 30);
            var centre = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            g.DrawString(text, font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 1, 32, 32), centre);
        }
        return _badges[text] = System.Drawing.Icon.FromHandle(bmp.GetHicon()); // at most 10 ("T", 1–9), kept for the app's life
    }

    TaskbarIcon BuildTray(NotifyEngine notify, Sounds sounds)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string? icon, Action click)
        {
            var mi = new MenuItem { Header = header, Icon = icon == null ? null : MenuIcon(icon) };
            mi.Click += (_, _) =>
            {
                try { click(); }
                catch (Exception ex)
                {
                    Log.Error(ex, "{Action} failed", header);
                    MessageBox.Show(ex.Message, "TaskDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            return mi;
        }
        // A section label: a disabled item shows as a quiet heading in the native menu.
        MenuItem Heading(string text) => new() { Header = text, IsEnabled = false, FontSize = 11, FontWeight = FontWeights.SemiBold };

        // ---- the widget ----
        menu.Items.Add(Heading("Widget"));
        menu.Items.Add(Item("Show or hide", "eye", () => { _userHidden = !_userHidden; UpdateVisibility(); }));
        var cycle = Item("Next size", "layout", () => _vm!.CycleMode());
        cycle.InputGestureText = "Ctrl+Alt+Shift+D";
        menu.Items.Add(cycle);

        var presets = new MenuItem { Header = "Presets", Icon = MenuIcon("presets") };
        presets.SubmenuOpened += (_, _) =>
        {
            presets.Items.Clear();
            foreach (var name in PresetStore.Names().Order())
            {
                var mi = Item(name, null, () => ApplyPreset(name, merge: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
                mi.IsChecked = name == _vm!.Preset.Name;
                mi.ToolTip = "Click to apply (replaces tiles). Shift+click to merge.";
                presets.Items.Add(mi);
            }
            presets.Items.Add(new Separator());
            presets.Items.Add(Item("Save current as…", "pencil", SavePresetAs));
            presets.Items.Add(Item("Launch missing", "power", () => _ = _vm!.LaunchMissing()));
        };
        presets.Items.Add(new MenuItem()); // placeholder so the submenu arrow shows
        menu.Items.Add(presets);

        // ---- connections ----
        menu.Items.Add(new Separator());
        menu.Items.Add(Heading("Connect"));
        menu.Items.Add(Item("Phone (Remote)…", "phone", OpenRemote));
        var claude = new MenuItem { Header = "Claude Code", Icon = MenuIcon("terminal") };
        claude.Items.Add(Item("Connect", "check", () =>
        {
            ClaudeHooksInstaller.Connect(_hub!.Port, _hub.Token, HubHoldSeconds);
            MessageBox.Show($"Hooks added to {Paths.ClaudeSettings}\n(backup: settings.json.taskdock.bak)\n\nNew Claude Code sessions will report to TaskDock.",
                "TaskDock", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        claude.Items.Add(Item("Disconnect", "close", ClaudeHooksInstaller.Disconnect));
        claude.Items.Add(new Separator());
        claude.Items.Add(Item("Rotate token", "refresh", () =>
        {
            _hub!.RotateToken();
            ClaudeHooksInstaller.SyncIfInstalled(_hub.Port, _hub.Token, HubHoldSeconds);
        }));
        menu.Items.Add(claude);

        // ---- TaskDock itself ----
        menu.Items.Add(new Separator());
        var mute = new MenuItem { Header = "Mute sounds", IsCheckable = true, IsChecked = notify.Muted };
        void MuteIcon() => mute.Icon = MenuIcon(mute.IsChecked ? "mute" : "volume");
        mute.Click += (_, _) => { notify.Muted = _settings.Muted = mute.IsChecked; _settings.Save(); MuteIcon(); };
        menu.Opened += (_, _) => { mute.IsChecked = notify.Muted; MuteIcon(); }; // the settings window can change it too
        menu.Items.Add(mute);
        menu.Items.Add(Item("Settings…", "settings", () => OpenSettings(notify, sounds)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit TaskDock", "quit", Quit));

        var tray = new TaskbarIcon { ToolTipText = "TaskDock", Icon = _shown = (System.Drawing.Icon)Badge(_badge = "T").Clone(), ContextMenu = menu };
        tray.TrayLeftMouseUp += (_, _) => { _userHidden = !_userHidden; UpdateVisibility(); };
        tray.ForceCreate(false);
        return tray;
    }

    void OpenSettings(NotifyEngine notify, Sounds sounds)
    {
        var w = new SettingsWindow(_settings);
        if (w.ShowDialog() != true) return;
        var e = w.Edited;
        var holdChanged = e.HoldSeconds != _settings.HoldSeconds;
        (_settings.Muted, _settings.Volume, _settings.QuietFrom, _settings.QuietTo) = (e.Muted, e.Volume, e.QuietFrom, e.QuietTo);
        (_settings.SilentWhenFocused, _settings.HideWhenFullscreen, _settings.AutoSort) = (e.SilentWhenFocused, e.HideWhenFullscreen, e.AutoSort);
        (_settings.HoldSeconds, _settings.TerminalTemplate) = (e.HoldSeconds, e.TerminalTemplate);
        _settings.Save();

        // Apply live; the rest is read from _settings where it's used.
        notify.Muted = e.Muted;
        notify.QuietFrom = TimeOnly.TryParse(e.QuietFrom, out var qf) ? qf : null;
        notify.QuietTo = TimeOnly.TryParse(e.QuietTo, out var qt) ? qt : null;
        sounds.Volume = e.Volume;
        _vm!.AutoSort = e.AutoSort;
        _vm.Sync();
        if (holdChanged) ApplyHold();
    }

    /// <summary>The tray icon shows how many detected sources are waiting to be pinned.</summary>
    void UpdateTrayBadge()
    {
        if (_tray == null || _vm == null) return;
        var text = _vm.DetectedCount > 0 ? Math.Min(_vm.DetectedCount, 9).ToString() : "T";
        if (text != _badge)
        {
            // The tray disposes the icon it's replacing, so it only ever gets a copy; the cache stays intact.
            var previous = _shown;
            _tray.Icon = _shown = (System.Drawing.Icon)Badge(_badge = text).Clone();
            previous?.Dispose(); // ours to free too (Dispose is safe twice): at most one copy alive
        }
        _tray.ToolTipText = $"TaskDock · {_vm.PillText}";
    }

    /// <summary>
    /// How long the hub holds an approval. With Remote on it's always the maximum, because Claude Code fixes the hook timeout
    /// when a session starts; Away mode then decides the real wait (RemoteControl.Tick hands it back sooner at the PC).
    /// </summary>
    internal const string HostChangedText = "This PC's Tailscale name changed, so your phones' passkeys no longer fit. Pair them again in Phone (Remote).";

    int HubHoldSeconds => _remote?.Server != null ? RemoteControl.RemoteHoldSeconds : _settings.HoldSeconds;

    void ApplyHold()
    {
        _hubOptions!.HoldWindow = TimeSpan.FromSeconds(HubHoldSeconds);
        try { ClaudeHooksInstaller.SetHold(_hub!.Port, _hub.Token, HubHoldSeconds); } // installed hooks' timeout follows the hold
        catch (Exception ex) { Log.Warning(ex, "Could not update Claude Code hooks"); }
    }

    /// <summary>An Icons.xaml icon at menu size (the same icons as the widget and the phone).</summary>
    static Viewbox MenuIcon(string name) => new()
    {
        Width = 14, Height = 14,
        Child = new Grid
        {
            Width = 24, Height = 24,
            Children =
            {
                new System.Windows.Shapes.Path
                {
                    Data = (Geometry)Current.FindResource("I." + name), Stroke = MenuInk, Style = (Style)Current.FindResource("Stroke"),
                },
            },
        },
    };

    static readonly SolidColorBrush MenuInk = new(Color.FromRgb(0x5C, 0x5F, 0x69));

    void OpenRemote()
    {
        if (_remote == null) return;
        if (_remoteWindow is { IsLoaded: true }) { _remoteWindow.Activate(); return; }
        _remoteWindow = new RemoteWindow(_remote, _settings);
        _remoteWindow.Show();
    }

    void ApplyPreset(string name, bool merge)
    {
        _vm!.Apply(PresetStore.Load(name), merge);
        if (merge) return;
        _settings.LastPreset = name;
        _settings.Save();
        _window?.Resnap();
    }

    void SavePresetAs()
    {
        if (InputDialog.Ask("Save preset", "Preset name:", _vm!.Preset.Name) is not { } name || string.IsNullOrWhiteSpace(name)) return;
        _vm.SaveAs(name);
        _settings.LastPreset = _vm.Preset.Name;
        _settings.Save();
    }

    /// <summary>Focus target per adapter: generic by its bound window, Unity by PID, VS Code by title ↔ workspace name.</summary>
    static nint WindowOf(TaskDock.Core.Tiles.Tile t, GenericWatcher generic)
    {
        switch (t.Adapter)
        {
            case "generic": return generic.WindowOf(t.Instance);
            case "vscode":
                return Platform.Desktop.TopLevel().FirstOrDefault(h =>
                    Platform.Desktop.Title(h).Contains(t.Label, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(Platform.Desktop.ImagePath(Platform.Desktop.ProcessId(h)) ?? "").Equals("Code.exe", StringComparison.OrdinalIgnoreCase));
            case "claude": return 0;
            default:
                if (t.Pid is not { } pid) return 0;
                try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle; }
                catch (ArgumentException) { return 0; } // exited
        }
    }

    void Quit()
    {
        _settings.Save();
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_remote != null) await _remote.StopAsync();
        if (_hub != null) await _hub.DisposeAsync(); // held approvals return "no decision"; Claude Code falls back to its own prompt
        Log.CloseAndFlush();
        _single?.Dispose();
        base.OnExit(e);
    }
}
