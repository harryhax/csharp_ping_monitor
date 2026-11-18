using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.App.Views;

/// <summary>Traffic-light colours. Always shown alongside a text label.</summary>
public sealed class HealthLevelToBrushConverter : IValueConverter
{
    public static readonly HealthLevelToBrushConverter Instance = new();

    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2E9D4A"));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#E0A100"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D93025"));
    public static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#8A8F98"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HealthLevel.Green => Green,
        HealthLevel.Yellow => Yellow,
        HealthLevel.Red => Red,
        _ => Grey,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter SupportedRouting = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? HealthLevelToBrushConverter.Green : HealthLevelToBrushConverter.Red;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
