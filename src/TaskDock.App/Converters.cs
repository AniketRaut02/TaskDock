using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskDock.App;

/// <summary>One converter, four keys: InvVis (bool), NotNull, NotZero, EmptyVis (string).</summary>
public sealed class VisConverter : IValueConverter
{
    public string Kind { get; set; } = "";

    public object Convert(object? value, Type t, object? p, CultureInfo c) => Kind switch
    {
        "Inverse" => value is true ? Visibility.Collapsed : Visibility.Visible,
        "NotNull" => value is null or "" ? Visibility.Collapsed : Visibility.Visible,
        "NotZero" => value is 0 ? Visibility.Collapsed : Visibility.Visible,
        "Empty" => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed,
        _ => throw new InvalidOperationException(Kind),
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
