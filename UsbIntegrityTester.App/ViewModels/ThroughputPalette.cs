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

    // One identity color per speed test slot (position-based, not category-based — a slot keeps
    // its color even when the user swaps which category it's testing).
    public static readonly Brush Slot1Neutral = Freeze(Color.FromRgb(0x9B, 0x59, 0xB6));
    public static readonly Brush Slot1NeutralFill = Freeze(Color.FromArgb(0x40, 0x9B, 0x59, 0xB6));

    public static readonly Brush Slot2Neutral = Freeze(Color.FromRgb(0xF3, 0x9C, 0x12));
    public static readonly Brush Slot2NeutralFill = Freeze(Color.FromArgb(0x40, 0xF3, 0x9C, 0x12));

    public static readonly Brush Slot3Neutral = Freeze(Color.FromRgb(0x1A, 0xBC, 0x9C));
    public static readonly Brush Slot3NeutralFill = Freeze(Color.FromArgb(0x40, 0x1A, 0xBC, 0x9C));

    // Deliberately not the same red as Bad/BadFill above — a slot's identity color needs to stay
    // neutral regardless of whether that run happened to miss its claim.
    public static readonly Brush Slot4Neutral = Freeze(Color.FromRgb(0xE8, 0x43, 0x93));
    public static readonly Brush Slot4NeutralFill = Freeze(Color.FromArgb(0x40, 0xE8, 0x43, 0x93));

    public static readonly Brush MissingCapacity = Freeze(Color.FromRgb(0x55, 0x55, 0x55));

    // A fixed identity color for every "Read"/"Verify" channel, regardless of which track it's
    // in — write always uses the track's own family color above, read/verify always uses this.
    // Previously the line itself was recolored green/red by whether it met its claim, which meant
    // two very different numbers (e.g. a 0.4 MB/s write and a 67 MB/s read) could render as the
    // identical color if both happened to fall short of their claims, making them look like the
    // same series. Claim status is now a separate badge instead (see ClaimStatusGlyph).
    public static readonly Brush ReadAccent = Freeze(Color.FromRgb(0x17, 0xA2, 0xB8));
    public static readonly Brush ReadAccentFill = Freeze(Color.FromArgb(0x40, 0x17, 0xA2, 0xB8));

    public static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
