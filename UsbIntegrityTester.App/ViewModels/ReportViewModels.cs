namespace UsbIntegrityTester.App.ViewModels;

/// <summary>Plain display data for the Report page — text and numbers only, no charts/bars, so the
/// page reads as a set of concrete figures someone can act on rather than something to eyeball.</summary>

/// <summary>Shared MB/s -> "MB/s" or "GB/s" formatting so a fast drive's numbers stay readable
/// (e.g. "3.88 GB/s" instead of "3,882.7 MB/s") everywhere a throughput number is shown.</summary>
public static class ThroughputFormat
{
    public static string Format(double megabytesPerSecond) => megabytesPerSecond >= 1000
        ? $"{megabytesPerSecond / 1000:N2} GB/s"
        : $"{megabytesPerSecond:N1} MB/s";
}

public sealed record ReportCapacitySummary
{
    public required double ClaimedGb { get; init; }
    public required double VerifiedGb { get; init; }
    public required double MissingGb { get; init; }
    public required double VerifiedFraction { get; init; }
    public required int BlocksTested { get; init; }
    public required int BlocksFailed { get; init; }
    public required bool HasData { get; init; }

    /// <summary>How thoroughly the capacity was actually checked — reflects the scan depth that was
    /// used, not just "full capacity wasn't tested," so a Quick scan's result reads as "fast but
    /// less thorough" rather than as if it came up short.</summary>
    public required string ScanThoroughnessText { get; init; }

    public bool AllBlocksPassed => BlocksFailed == 0;
    public string HeadlineText => HasData ? $"{VerifiedFraction:P0} of claimed capacity verified" : "Capacity test not run";
    public string DetailText => HasData
        ? $"{VerifiedGb:N1} GB verified of {ClaimedGb:N1} GB claimed" + (MissingGb > 0.01 ? $" — {MissingGb:N1} GB missing" : "")
        : string.Empty;

    public static readonly ReportCapacitySummary Empty = new()
    {
        ClaimedGb = 0, VerifiedGb = 0, MissingGb = 0, VerifiedFraction = 0,
        BlocksTested = 0, BlocksFailed = 0, HasData = false, ScanThoroughnessText = string.Empty,
    };
}

/// <summary>One speed test slot's full report row — the category tested, claimed-vs-measured
/// numbers for both directions, and a practical "how long would this actually take" translation.</summary>
public sealed record SpeedTestSlotReportRow
{
    public required string CategoryName { get; init; }
    public required string WorkloadDescription { get; init; }
    public required string SizeRangeText { get; init; }

    public required double WriteAvgMbps { get; init; }
    public required double WritePeakMbps { get; init; }
    public double? ClaimedWriteMbps { get; init; }

    public required double ReadAvgMbps { get; init; }
    public required double ReadPeakMbps { get; init; }
    public double? ClaimedReadMbps { get; init; }

    public required string PracticalTimeText { get; init; }

    public string WriteAvgText => ThroughputFormat.Format(WriteAvgMbps);
    public string WritePeakText => $"peak {ThroughputFormat.Format(WritePeakMbps)}";
    public string WriteClaimText => ClaimedWriteMbps is { } c and > 0 ? $"claimed {ThroughputFormat.Format(c)}" : string.Empty;
    public bool WriteMeetsClaim => ClaimedWriteMbps is not { } c || c <= 0 || WriteAvgMbps >= c;

    public string ReadAvgText => ThroughputFormat.Format(ReadAvgMbps);
    public string ReadPeakText => $"peak {ThroughputFormat.Format(ReadPeakMbps)}";
    public string ReadClaimText => ClaimedReadMbps is { } c and > 0 ? $"claimed {ThroughputFormat.Format(c)}" : string.Empty;
    public bool ReadMeetsClaim => ClaimedReadMbps is not { } c || c <= 0 || ReadAvgMbps >= c;
}

/// <summary>A simple labeled number for the report's expanded stats grid.</summary>
public sealed record ReportStat(string Label, string Value);
