using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using TaskDock.Remote;

namespace TaskDock.App;

/// <summary>
/// TaskDock Remote setup: a checklist re-checked every 2 s while open, each step with its fix, then paired phones,
/// Away settings and recent activity. Follows only what Tailscale reports; it never installs or signs in for the user.
/// </summary>
sealed class RemoteWindow : Window
{
    readonly RemoteControl _remote;
    readonly AppSettings _settings;
    readonly StackPanel _steps = new();
    readonly CheckBox _enabled = new() { Content = "Let my paired phones reach TaskDock (over my tailnet only)", VerticalAlignment = VerticalAlignment.Center };
    readonly Button _pair = new() { Content = "Pair a phone…", Padding = new Thickness(12, 4, 12, 4) };
    readonly StackPanel _qr = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel _devices = new();
    readonly TextBlock _activity = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    readonly DispatcherTimer _refresh;
    TailscaleStatus? _status;
    bool _installed, _checking, _busy;
    string? _problem; // what to fix, kept until the next try

    public RemoteWindow(RemoteControl remote, AppSettings settings)
    {
        (_remote, _settings) = (remote, settings);
        Title = "TaskDock Remote";
        SizeToContent = SizeToContent.Height;
        Width = 500;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;

        var awayAfter = new TextBox { Width = 40, Text = settings.AwayAfterMinutes.ToString(), Margin = new Thickness(6, 0, 6, 0) };
        awayAfter.LostFocus += (_, _) =>
        {
            settings.AwayAfterMinutes = int.TryParse(awayAfter.Text, out var m) ? Math.Clamp(m, 1, 30) : 3;
            awayAfter.Text = settings.AwayAfterMinutes.ToString();
            settings.Save();
        };
        AutomationProperties_Name(awayAfter, "Away after minutes");
        var alerts = new CheckBox { Content = "Send approvals and failures to my phone while I'm away", IsChecked = settings.PhoneAlerts, Margin = new Thickness(0, 6, 0, 0) };
        alerts.Click += (_, _) => { settings.PhoneAlerts = alerts.IsChecked == true; settings.Save(); };

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    Heading("Set up", 0), _steps,
                    Hint("Turning this on shares TaskDock at this PC's Tailscale address. That name becomes public in certificate transparency logs, "
                        + "like every HTTPS certificate. Tailscale's free Personal plan is for non-commercial use."),
                    _qr,
                    Heading("Paired phones"), _devices,
                    Heading("Away mode"),
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "I'm away after", VerticalAlignment = VerticalAlignment.Center }, awayAfter, new TextBlock { Text = "minutes without keyboard or mouse.", VerticalAlignment = VerticalAlignment.Center } } },
                    alerts,
                    Hint("While you're away, Claude Code approvals wait up to 10 minutes for your phone and the PC stays awake while work is running. "
                        + "Restart Claude Code sessions that are already running so they pick up the longer wait."),
                    Heading("Recent activity from phones"), _activity,
                },
            },
        };

        _enabled.IsChecked = _remote.Server != null;
        _enabled.Click += async (_, _) => await Toggle(_enabled.IsChecked == true);
        _pair.Click += (_, _) => ShowPairing();
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await Check(), Dispatcher);
        Loaded += async (_, _) => { await Check(); _refresh.Start(); };
        Closed += (_, _) => _refresh.Stop();
    }

    static void AutomationProperties_Name(UIElement e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);
    static TextBlock Heading(string text, double top = 16) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6) };
    static TextBlock Hint(string text) => new() { Text = text, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    static Button Link(string text, string url)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2) };
        b.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return b;
    }

    async Task Check()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            _installed = TailscaleCli.Exe() != null;
            _status = _installed ? await TailscaleCli.StatusAsync() : null;
        }
        finally { _checking = false; }
        Draw();
    }

    async Task Toggle(bool on)
    {
        _busy = true;
        _enabled.IsEnabled = false;
        if (on) _problem = await _remote.EnableAsync();
        else { _problem = null; await _remote.DisableAsync(); }
        _settings.RemoteEnabled = _remote.Server != null;
        _settings.Save();
        _enabled.IsChecked = _remote.Server != null;
        _enabled.IsEnabled = true;
        _busy = false;
        Draw();
    }

    void ShowPairing()
    {
        if (_remote.Server is not { } server) return;
        var url = server.PairingUrl(server.NewPairingCode());
        using var qr = new PngByteQRCode(QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M));
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(qr.GetGraphic(6));
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();

        _qr.Children.Clear();
        _qr.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "On your phone: install Tailscale and sign in with the same account, turn on notifications for Chrome in Android "
                + "settings, then scan this with the camera and open it in Chrome. The code works once, for 5 minutes.",
        });
        _qr.Children.Add(new Image { Source = img, Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) });
        _qr.Children.Add(new TextBox { Text = url, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11 });
        _qr.Visibility = Visibility.Visible;
    }

    void Draw()
    {
        if (_busy) return;
        var server = _remote.Server;
        var s = _status;
        var paired = server?.Devices.Devices.Where(d => d.IsActive).ToList() ?? [];

        _steps.Children.Clear();
        Step(1, _installed, "Tailscale is installed on this PC", Link("Download", "https://tailscale.com/download/windows"));
        Step(2, s?.Running == true, "Tailscale is signed in and connected", _installed ? new TextBlock { Text = "Open Tailscale from the tray and sign in.", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center } : null);
        Step(3, s?.Ready == true, "MagicDNS and HTTPS Certificates are on for your tailnet", Link("Open admin console", "https://login.tailscale.com/admin/dns"));
        _enabled.IsEnabled = server != null || s?.Ready == true;
        Step(4, server != null, server != null ? $"On at {_remote.Address}" : _problem ?? "Remote is off", _enabled);
        _pair.IsEnabled = server != null;
        Step(5, paired.Count > 0, paired.Count > 0 ? $"{paired.Count} phone{(paired.Count == 1 ? "" : "s")} paired" : "Pair your phone", _pair);
        if (server == null) _qr.Visibility = Visibility.Collapsed;

        _devices.Children.Clear();
        if (paired.Count == 0) _devices.Children.Add(Hint(server == null ? "Turn Remote on to see and pair phones." : "None yet."));
        foreach (var d in paired)
        {
            var rename = new Button { Content = "Rename", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            rename.Click += (_, _) => { if (InputDialog.Ask("Rename phone", "Name:", d.Name) is { } n) { server!.Devices.Rename(d.Id, n); Draw(); } };
            var revoke = new Button { Content = "Revoke", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            revoke.Click += (_, _) => { server!.Revoke(d.Id); Draw(); };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(revoke, Dock.Right);
            DockPanel.SetDock(rename, Dock.Right);
            row.Children.Add(revoke);
            row.Children.Add(rename);
            row.Children.Add(new TextBlock { Text = $"{d.Name}  ·  last seen {d.LastSeenAt.ToLocalTime():g}{(d.Push == null ? "  ·  alerts off" : "")}", VerticalAlignment = VerticalAlignment.Center });
            _devices.Children.Add(row);
        }

        var recent = server?.Activity.Page(null, 12) ?? [];
        _activity.Text = recent.Count == 0 ? "Nothing yet." : string.Join("\n", recent.Select(e =>
            $"{e.Ts.ToLocalTime():HH:mm:ss}  {e.DeviceName}  {e.Type}  {e.Target}  {(e.Ok ? "ok" : e.Code)}"));
    }

    void Step(int n, bool done, string text, UIElement? fix)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var mark = new TextBlock
        {
            Text = done ? "✓" : n.ToString(), Width = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = done ? new SolidColorBrush(Color.FromRgb(0x30, 0xA4, 0x6C)) : Brushes.Gray,
        };
        System.Windows.Automation.AutomationProperties.SetName(mark, done ? "Done" : $"Step {n}");
        row.Children.Add(mark);
        if (fix != null && (!done || n is 4 or 5))
        {
            if (fix is FrameworkElement fe && fe.Parent is Panel p) p.Children.Remove(fe); // the toggle and Pair button move between redraws
            DockPanel.SetDock(fix, Dock.Right);
            row.Children.Add(fix);
        }
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _steps.Children.Add(row);
    }
}
