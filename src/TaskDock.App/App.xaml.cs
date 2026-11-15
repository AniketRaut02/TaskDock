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
using TaskDock.Core.Notify;
using TaskDock.Core.Tiles;
using TaskDock.Hub;
using TaskDock.Platform;

namespace TaskDock.App;

/// <summary>Composition root: hub → channel → registry (UI thread, 10 Hz) → view model, notify, tray.</summary>
public partial class App : Application
{
    Mutex? _single;
    HubServer? _hub;
    TaskbarIcon? _tray;
    MainWindow? _window;
    MainViewModel? _vm;
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
        _hub = new HubServer(new HubOptions
        {
            HoldWindow = TimeSpan.FromSeconds(_settings.HoldSeconds),
            Logging = l => l.AddSerilog(Log.Logger),
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
        registry.Transition += (t, prevClass, prevState) => sounds.Play(notify.OnTransition(t, prevClass, prevState));

        _window = new MainWindow(_vm, _settings);
        _vm.Sync();
        _window.Show();
        _tray = BuildTray(notify);
        _vm.Hint += text => _tray.ShowNotification("TaskDock", text);

        // Drain on the UI thread at 10 Hz: every event reaches Notify, the UI repaints once per tick.
        var approvalsChanged = 1;
        broker.Changed += () => Interlocked.Exchange(ref approvalsChanged, 1);
        var tick = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            var dirty = Interlocked.Exchange(ref approvalsChanged, 0) == 1;
            while (events.Reader.TryRead(out var ev)) { registry.Apply(ev); dirty = true; }
            if (++tick % 10 == 0)
            {
                foreach (var ev in generic.Tick(_vm.Preset.Tiles)) { registry.Apply(ev); dirty = true; }
                if (registry.Sweep().Count > 0) dirty = true;
                UpdateVisibility();
            }
            if (dirty)
            {
                _vm.Sync();
                UpdateTrayBadge();
            }
        }, Dispatcher);
        timer.Start();
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

    GeneratedIconSource? _icon;

    TaskbarIcon BuildTray(NotifyEngine notify)
    {
        _icon = new GeneratedIconSource
        {
            Text = "T", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x8E, 0xF7)),
            FontWeight = FontWeights.Bold,
        };
        var menu = new ContextMenu();
        MenuItem Item(string header, Action click)
        {
            var mi = new MenuItem { Header = header };
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

        menu.Items.Add(Item("Show / hide", () => { _userHidden = !_userHidden; UpdateVisibility(); }));
        menu.Items.Add(Item("Cycle mode   Ctrl+Alt+Shift+D", () => _vm!.CycleMode()));

        var presets = new MenuItem { Header = "Presets" };
        presets.SubmenuOpened += (_, _) =>
        {
            presets.Items.Clear();
            foreach (var name in PresetStore.Names().Order())
            {
                var mi = Item(name, () => ApplyPreset(name, merge: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
                mi.IsChecked = name == _vm!.Preset.Name;
                mi.ToolTip = "Click to apply (replaces tiles). Shift+click to merge.";
                presets.Items.Add(mi);
            }
            presets.Items.Add(new Separator());
            presets.Items.Add(Item("Save current as…", SavePresetAs));
            presets.Items.Add(Item("Launch missing", () => _ = _vm!.LaunchMissing()));
        };
        presets.Items.Add(new MenuItem()); // placeholder so the submenu arrow shows
        menu.Items.Add(presets);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Connect Claude Code", () =>
        {
            ClaudeHooksInstaller.Connect(_hub!.Port, _hub.Token, _settings.HoldSeconds);
            MessageBox.Show($"Hooks added to {Paths.ClaudeSettings}\n(backup: settings.json.taskdock.bak)\n\nNew Claude Code sessions will report to TaskDock.",
                "TaskDock", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        menu.Items.Add(Item("Disconnect Claude Code", ClaudeHooksInstaller.Disconnect));
        menu.Items.Add(Item("Rotate token", () =>
        {
            _hub!.RotateToken();
            ClaudeHooksInstaller.SyncIfInstalled(_hub.Port, _hub.Token, _settings.HoldSeconds);
        }));

        var mute = new MenuItem { Header = "Mute sounds", IsCheckable = true, IsChecked = notify.Muted };
        mute.Click += (_, _) => { notify.Muted = _settings.Muted = mute.IsChecked; _settings.Save(); };
        menu.Items.Add(mute);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", Quit));

        var tray = new TaskbarIcon { ToolTipText = "TaskDock", IconSource = _icon, ContextMenu = menu };
        tray.TrayLeftMouseUp += (_, _) => { _userHidden = !_userHidden; UpdateVisibility(); };
        tray.ForceCreate(false);
        return tray;
    }

    /// <summary>The tray icon shows how many detected sources are waiting to be pinned.</summary>
    void UpdateTrayBadge()
    {
        if (_icon == null || _vm == null) return;
        var text = _vm.DetectedCount > 0 ? Math.Min(_vm.DetectedCount, 9).ToString() : "T";
        if (_icon.Text != text) _icon.Text = text;
        if (_tray != null) _tray.ToolTipText = $"TaskDock · {_vm.PillText}";
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
        if (_hub != null) await _hub.DisposeAsync(); // held approvals return "no decision"; Claude Code falls back to its own prompt
        Log.CloseAndFlush();
        _single?.Dispose();
        base.OnExit(e);
    }
}
