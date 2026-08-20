using System.Windows.Media;

namespace UsbIntegrityTester.App.ViewModels;

/// <summary>
/// Shared color palette for live and report throughput visuals, so every card, sparkline, and bar
/// agrees on what "meeting the claim" vs "falling short" looks like, and each test type keeps a
/// consistent identity color across the Run Test cards and the Report page.
/// </summary>
internal static class ThroughputPalette
{
    public static readonly Brush Good = Freeze(Color.FromRgb(0x2E, 0xCC, 0x71));
    public static readonly Brush Bad = Freeze(Color.FromRgb(0xE7, 0x4C, 0x3C));
    public static readonly Brush GoodFill = Freeze(Color.FromArgb(0x40, 0x2E, 0xCC, 0x71));
    public static readonly Brush BadFill = Freeze(Color.FromArgb(0x40, 0xE7, 0x4C, 0x3C));

    public static readonly Brush CapacityNeutral = Freeze(Color.FromRgb(0x00, 0xA6, 0xFB));
    public static readonly Brush CapacityNeutralFill = Freeze(Color.FromArgb(0x40, 0x00, 0xA6, 0xFB));

    public static readonly Brush LargeFileNeutral = Freeze(Color.FromRgb(0x9B, 0x59, 0xB6));
    public static readonly Brush LargeFileNeutralFill = Freeze(Color.FromArgb(0x40, 0x9B, 0x59, 0xB6));

    public static readonly Brush SmallFileNeutral = Freeze(Color.FromRgb(0xF3, 0x9C, 0x12));
    public static readonly Brush SmallFileNeutralFill = Freeze(Color.FromArgb(0x40, 0xF3, 0x9C, 0x12));

    public static readonly Brush HugeFileNeutral = Freeze(Color.FromRgb(0x1A, 0xBC, 0x9C));
    public static readonly Brush HugeFileNeutralFill = Freeze(Color.FromArgb(0x40, 0x1A, 0xBC, 0x9C));

    public static readonly Brush MissingCapacity = Freeze(Color.FromRgb(0x55, 0x55, 0x55));

    public static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
