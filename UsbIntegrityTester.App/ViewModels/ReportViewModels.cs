using System.Windows.Media;

namespace UsbIntegrityTester.App.ViewModels;

/// <summary>Plain display data for the Report page's visuals — deliberately not chart-library
/// series objects, so the page can be built from ordinary WPF bars/sparklines that always match
/// the app's theme instead of a plotting library's own defaults.</summary>

public sealed record ReportCapacitySummary
{
    public required double ClaimedGb { get; init; }
    public required double VerifiedGb { get; init; }
    public required double MissingGb { get; init; }
    public required double VerifiedFraction { get; init; }
    public double RemainderFraction => Math.Max(0, 1 - VerifiedFraction);
    public required int BlocksTested { get; init; }
    public required int BlocksFailed { get; init; }
    public required bool HasData { get; init; }

    public bool AllBlocksPassed => BlocksFailed == 0;
    public string HeadlineText => HasData ? $"{VerifiedFraction:P0} of claimed capacity verified" : "Capacity test not run";
    public string DetailText => HasData
        ? $"{VerifiedGb:N1} GB verified of {ClaimedGb:N1} GB claimed" + (MissingGb > 0.01 ? $" — {MissingGb:N1} GB missing" : "")
        : string.Empty;
    public Brush BarBrush => !HasData ? ThroughputPalette.MissingCapacity : (AllBlocksPassed ? ThroughputPalette.Good : ThroughputPalette.Bad);

    public static readonly ReportCapacitySummary Empty = new()
    {
        ClaimedGb = 0, VerifiedGb = 0, MissingGb = 0, VerifiedFraction = 0,
        BlocksTested = 0, BlocksFailed = 0, HasData = false,
    };
}

/// <summary>One row of the "Claimed vs. Measured" bullet-style comparison — a bar sized to the
/// measured value, colored by whether it met the claim, with the claim and peak burst as text.</summary>
public sealed record SpeedComparisonRow
{
    public required string Label { get; init; }
    public required double MeasuredMbps { get; init; }
    public double? ClaimedMbps { get; init; }
    public double? PeakMbps { get; init; }

    /// <summary>0..1 — how far the bar should fill, relative to the largest measurement in its group so bars in the same section are comparable.</summary>
    public required double BarFraction { get; init; }
    public double RemainderFraction => Math.Max(0, 1 - BarFraction);

    public bool HasClaim => ClaimedMbps is { } c && c > 0;
    public bool MeetsClaim => !HasClaim || MeasuredMbps >= ClaimedMbps!.Value;
    public Brush BarBrush => HasClaim ? (MeetsClaim ? ThroughputPalette.Good : ThroughputPalette.Bad) : ThroughputPalette.CapacityNeutral;

    public string MeasuredText => $"{MeasuredMbps:N1} MB/s";
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (HasClaim) parts.Add($"claimed {ClaimedMbps!.Value:N1}");
            if (PeakMbps is { } p) parts.Add($"peak {p:N1}");
            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts) + " MB/s";
        }
    }
}

/// <summary>One small trend card in the Report's "burst vs. sustained" section — replaces the old
/// single crowded multi-line chart with one focused sparkline per test type.</summary>
public sealed record ReportTrendCard
{
    public required string Title { get; init; }
    public required IReadOnlyList<double> Points { get; init; }
    public required double AverageMbps { get; init; }
    public required double PeakMbps { get; init; }
    public required Brush Stroke { get; init; }
    public required Brush Fill { get; init; }

    public string AverageText => $"avg {AverageMbps:N1} MB/s";
    public string PeakText => $"peak {PeakMbps:N1} MB/s";
}

/// <summary>A simple labeled number for the report's expanded stats grid.</summary>
public sealed record ReportStat(string Label, string Value);
