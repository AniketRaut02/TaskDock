using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TaskDock.Core.Protocol;

namespace TaskDock.App;

/// <summary>Edits a copy of the settings; the caller applies it only after Save passes validation.</summary>
public partial class SettingsWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "TaskDock";

    internal AppSettings Edited { get; }

    internal SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        DataContext = Edited = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, Wire.Json), Wire.Json)!;
        StartWithWindows.IsChecked = IsStartingWithWindows();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Validate() is { } problem)
        {
            Error.Text = problem;
            Error.Visibility = Visibility.Visible;
            return;
        }
        try { SetStartWithWindows(StartWithWindows.IsChecked == true); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not change Start with Windows"); }
        DialogResult = true;
    }

    string? Validate()
    {
        var s = Edited;
        if (s.HoldSeconds is < 5 or > 600) return "Approval hold must be between 5 and 600 seconds.";
        foreach (var t in new[] { s.QuietFrom, s.QuietTo })
            if (!string.IsNullOrWhiteSpace(t) && !TimeOnly.TryParse(t, out _)) return $"\"{t}\" isn't a time. Use HH:mm, like 22:30.";
        if (string.IsNullOrWhiteSpace(s.QuietFrom) != string.IsNullOrWhiteSpace(s.QuietTo)) return "Quiet hours need both a start and an end (or neither).";
        if (!s.TerminalTemplate.Contains("{cwd}")) return "The terminal launch command needs {cwd}, so Claude starts in the right folder.";
        s.QuietFrom = string.IsNullOrWhiteSpace(s.QuietFrom) ? null : s.QuietFrom.Trim();
        s.QuietTo = string.IsNullOrWhiteSpace(s.QuietTo) ? null : s.QuietTo.Trim();
        s.Volume = Math.Clamp(s.Volume, 0, 1);
        return null;
    }

    /// <summary>The per-user Run key: no admin rights, and it points at whichever TaskDock.exe is running now.</summary>
    static bool IsStartingWithWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    static void SetStartWithWindows(bool on)
    {
        if (on == IsStartingWithWindows()) return;
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
