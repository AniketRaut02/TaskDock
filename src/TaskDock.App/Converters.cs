using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskDock.App;

/// <summary>One converter for the small view conversions: visibility kinds, two colour picks for the media bar, and icon names.</summary>
public sealed class VisConverter : IValueConverter
{
    public string Kind { get; set; } = "";

    public object Convert(object? value, Type t, object? p, CultureInfo c) => Kind switch
    {
        "Inverse" => value is true ? Visibility.Collapsed : Visibility.Visible,
        "NotNull" => value is null or "" ? Visibility.Collapsed : Visibility.Visible,
        "NotZero" => value is 0 ? Visibility.Collapsed : Visibility.Visible,
        "Empty" => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed,
        "Null" => value is null ? Visibility.Visible : Visibility.Collapsed,
        "PinColor" => value is true ? Amber : Grey,
        "ChipBg" => value is true ? Blue : Chip,
        "Icon" => Application.Current.TryFindResource("I." + value), // an Icons.xaml geometry by name
        _ => throw new InvalidOperationException(Kind),
    };

    static readonly System.Windows.Media.Brush Amber = Frozen("#F5A524"), Grey = Frozen("#9EA1AA"), Blue = Frozen("#2D5FA8"), Chip = Frozen("#33353C");

    static System.Windows.Media.Brush Frozen(string hex)
    {
        var b = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
