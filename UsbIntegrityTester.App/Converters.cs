using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UsbIntegrityTester.App.Logging;
using UsbIntegrityTester.Core.Devices;
using UsbIntegrityTester.Core.Testing;
using UsbIntegrityTester.Core.Verdict;
using Wpf.Ui.Controls;

namespace UsbIntegrityTester.App;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
}

/// <summary>Two-way binds a RadioButton's IsChecked to a specific value of any enum.</summary>
public sealed class EnumEqualityConverter : IValueConverter
{
    public required object Target { get; init; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && value.Equals(Target);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Target : Binding.DoNothing;
}

/// <summary>Shows its target only when TestMode is File — used to hide File Mode-only options in Raw Mode.</summary>
public sealed class TestModeToFileVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TestMode.File ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts a byte count (ulong or ulong?) to gigabytes for display.</summary>
public sealed class BytesToGbConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ulong bytes => bytes / 1_000_000_000.0,
        _ => null,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True (elevated) shows as a success banner; false shows as a warning banner.</summary>
public sealed class ElevationSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? InfoBarSeverity.Success : InfoBarSeverity.Warning;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a DriveVerdict to the color that carries its meaning throughout the report.</summary>
public sealed class DriveVerdictToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DriveVerdict.Ok => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
        DriveVerdict.Underperforming => new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)),
        DriveVerdict.Counterfeit => new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
        DriveVerdict.Failed => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a DriveVerdict to a representative icon.</summary>
public sealed class DriveVerdictToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DriveVerdict.Ok => SymbolRegular.CheckmarkCircle24,
        DriveVerdict.Underperforming => SymbolRegular.Warning24,
        DriveVerdict.Counterfeit => SymbolRegular.DismissCircle24,
        DriveVerdict.Failed => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a log entry's level to a readable accent color.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)),
        _ => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a log entry's level to an icon.</summary>
public sealed class LogLevelToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => SymbolRegular.ErrorCircle24,
        LogLevel.Warning => SymbolRegular.Warning24,
        _ => SymbolRegular.Info24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders a UsbLinkSpeed as its human-friendly display name.</summary>
public sealed class UsbLinkSpeedDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        UsbLinkSpeed speed => speed.DisplayName(),
        _ => "Auto-detected",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when its bound string is null/empty — used for optional detail text (e.g. a card's GB/ETA line that's blank until its test is actually running).</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims a Run Test card when its test type wasn't selected on the Setup tab.</summary>
public sealed class BoolToCardOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.45;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a 0..1 fraction into a Star-sized GridLength, for building proportional bar rows out of plain Grid columns instead of a charting library.</summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(value is double d ? Math.Max(0, d) : 0, GridUnitType.Star);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True hides, false shows — the inverse of the standard BooleanToVisibilityConverter.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
