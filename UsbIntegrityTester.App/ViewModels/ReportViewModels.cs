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

    /// <summary>Explains an early-fast/late-slow (or vice versa) pattern in this row's write or read
    /// samples — e.g. an SLC write-cache filling up partway through — empty if neither channel showed
    /// one. See MainViewModel's trend-blurb builder for the actual wording/reasoning.</summary>
    public string TrendBlurb { get; init; } = string.Empty;

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

/// <summary>The optional Real-World File Copy test's report data — a "benchmark vs. real world"
/// companion to the synthetic speed test rows above, using the user's own files instead of a
/// simulated size range.</summary>
public sealed record RealFileCopyReportSummary
{
    public required bool HasData { get; init; }
    public required int FileCount { get; init; }
    public required double TotalGb { get; init; }
    public required string ExtensionBreakdownText { get; init; }

    public required double WriteAvgMbps { get; init; }
    public required double WritePeakMbps { get; init; }
    public required TimeSpan WriteDuration { get; init; }

    public required double ReadAvgMbps { get; init; }
    public required double ReadPeakMbps { get; init; }
    public required TimeSpan ReadDuration { get; init; }

    public required int FilesVerifiedIdentical { get; init; }
    public required IReadOnlyList<string> IssueMessages { get; init; }

    /// <summary>Same "early vs. late" trend explanation as <see cref="SpeedTestSlotReportRow.TrendBlurb"/>, for the write and/or read pass.</summary>
    public string TrendBlurb { get; init; } = string.Empty;

    /// <summary>"Moving 5 GB worth of this would take..." translation, same reference amount used for
    /// the synthetic categories — lets a real-world result be compared apples-to-apples against them.</summary>
    public string PracticalTimeText { get; init; } = string.Empty;

    public string HeadlineText => HasData ? $"{FileCount:N0} files, {TotalGb:N2} GB" : "Real-World File Copy test not run";
    public string WriteAvgText => ThroughputFormat.Format(WriteAvgMbps);
    public string WritePeakText => $"peak {ThroughputFormat.Format(WritePeakMbps)}";
    public string ReadAvgText => ThroughputFormat.Format(ReadAvgMbps);
    public string ReadPeakText => $"peak {ThroughputFormat.Format(ReadPeakMbps)}";
    public string WriteDurationText => FormatDuration(WriteDuration);
    public string ReadDurationText => FormatDuration(ReadDuration);

    public bool AllVerified => HasData && IssueMessages.Count == 0 && FilesVerifiedIdentical == FileCount;
    public string VerificationText => !HasData
        ? string.Empty
        : AllVerified
            ? $"✓ All {FilesVerifiedIdentical:N0} files came back byte-for-byte identical after the round trip."
            : $"⚠ {FileCount - FilesVerifiedIdentical:N0} of {FileCount:N0} file(s) did not come back identical — see details below.";

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.TotalSeconds:N0}s";

    public static readonly RealFileCopyReportSummary Empty = new()
    {
        HasData = false, FileCount = 0, TotalGb = 0, ExtensionBreakdownText = string.Empty,
        WriteAvgMbps = 0, WritePeakMbps = 0, WriteDuration = TimeSpan.Zero,
        ReadAvgMbps = 0, ReadPeakMbps = 0, ReadDuration = TimeSpan.Zero,
        FilesVerifiedIdentical = 0, IssueMessages = Array.Empty<string>(),
    };
}
