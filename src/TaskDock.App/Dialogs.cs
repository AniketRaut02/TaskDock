using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskDock.Platform;

namespace TaskDock.App;

/// <summary>Click-through outline drawn around the window under the crosshair.</summary>
sealed class PickerOverlay : Window
{
    nint _hwnd;

    public PickerOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; // layered is fine here: it lives only while you drag
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x8E, 0xF7)), BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x3E, 0x8E, 0xF7)),
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Desktop.MakeClickThrough(_hwnd);
        };
    }

    public void ShowAt(Win32.RECT r)
    {
        if (!IsVisible) Show();
        Desktop.SetBounds(_hwnd, r); // device pixels; WPF stretches the border to fit
    }
}

static class InputDialog
{
    /// <summary>A plain modal prompt. It is a normal, activating window so you can type.</summary>
    public static string? Ask(string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10), MinWidth = 260 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 70 };
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = prompt }, box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { w.Activate(); box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
