using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace TaskDock.App;

/// <summary>TaskDock Remote, basic version: on/off, pair a phone with a QR code, paired phones with Revoke. Phase 5 makes it a setup wizard.</summary>
sealed class RemoteWindow : Window
{
    readonly RemoteControl _remote;
    readonly AppSettings _settings;
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    readonly CheckBox _enabled = new() { Content = "Let my paired phones see TaskDock (over my tailnet only)", Margin = new Thickness(0, 4, 0, 4) };
    readonly Button _pair = new() { Content = "Pair a phone…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel _qr = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel _devices = new();
    readonly DispatcherTimer _refresh;
    bool _busy;
    string? _problem; // what to fix, kept until the next try

    public RemoteWindow(RemoteControl remote, AppSettings settings)
    {
        (_remote, _settings) = (remote, settings);
        Title = "TaskDock Remote";
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                _status, _enabled,
                Hint("Turning this on shares TaskDock at this PC's Tailscale address. That name becomes public in certificate transparency logs, "
                    + "like every HTTPS certificate. Tailscale's free Personal plan is for non-commercial use."),
                _pair, _qr,
                new TextBlock { Text = "Paired phones", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) },
                _devices,
            },
        };

        _enabled.IsChecked = _remote.Server != null;
        _enabled.Click += async (_, _) => await Toggle(_enabled.IsChecked == true);
        _pair.Click += (_, _) => ShowPairing();
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Draw(), Dispatcher);
        Loaded += (_, _) => { Draw(); _refresh.Start(); };
        Closed += (_, _) => _refresh.Stop();
    }

    static TextBlock Hint(string text) => new() { Text = text, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    async Task Toggle(bool on)
    {
        _busy = true;
        _enabled.IsEnabled = false;
        _status.Text = on ? "Starting…" : "Stopping…";
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
        _qr.Children.Add(new TextBlock { Text = "On your phone, with Tailscale connected, scan this with the camera. It works once and for 5 minutes.", TextWrapping = TextWrapping.Wrap });
        _qr.Children.Add(new Image { Source = img, Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) });
        _qr.Children.Add(new TextBox { Text = url, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11 });
        _qr.Visibility = Visibility.Visible;
    }

    void Draw()
    {
        if (_busy) return;
        var server = _remote.Server;
        if (server == null && _problem != null) _status.Text = _problem;
        else if (server == null) _status.Text = "Off. Your phone can't reach TaskDock.";
        else _status.Text = $"On at {_remote.Address}";
        _pair.IsEnabled = server != null;
        if (server == null) _qr.Visibility = Visibility.Collapsed;

        _devices.Children.Clear();
        var devices = server?.Devices.Devices.Where(d => d.IsActive).ToList() ?? [];
        if (devices.Count == 0) _devices.Children.Add(Hint(server == null ? "Turn Remote on to see and pair phones." : "None yet."));
        foreach (var d in devices)
        {
            var revoke = new Button { Content = "Revoke", Padding = new Thickness(10, 2, 10, 2) };
            revoke.Click += (_, _) => { server!.Revoke(d.Id); Draw(); };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);
            row.Children.Add(new TextBlock { Text = $"{d.Name}  ·  last seen {d.LastSeenAt.ToLocalTime():g}", VerticalAlignment = VerticalAlignment.Center });
            _devices.Children.Add(row);
        }
    }
}
