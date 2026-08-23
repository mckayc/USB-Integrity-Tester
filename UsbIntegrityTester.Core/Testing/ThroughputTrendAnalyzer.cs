namespace UsbIntegrityTester.Core.Testing;

public enum ThroughputTrend { Stable, Declining, Improving }

/// <summary>Compares the early portion of a throughput sample series against the late portion —
/// e.g. "did this test's speed fall off a cliff partway through and stay down" — so the report can
/// call that out explicitly instead of just showing an average that quietly blends a fast start and
/// a slow finish into one misleading number.</summary>
public sealed record ThroughputTrendAnalysis
{
    public required ThroughputTrend Trend { get; init; }
    public required double EarlyAverageMegabytesPerSecond { get; init; }
    public required double LateAverageMegabytesPerSecond { get; init; }
}

public static class ThroughputTrendAnalyzer
{
    /// <summary>The early-vs-late averages must differ by at least this fraction of the early
    /// average to call it a real trend rather than ordinary sample-to-sample noise.</summary>
    private const double SignificantChangeFraction = 0.35;

    public static ThroughputTrendAnalysis Analyze(IReadOnlyList<double> samples)
    {
        // Too few samples for "early" and "late" thirds to mean anything — a short synthetic test
        // or a tiny real-file-copy selection shouldn't get a trend verdict off a handful of points.
        if (samples.Count < 9)
            return new ThroughputTrendAnalysis { Trend = ThroughputTrend.Stable, EarlyAverageMegabytesPerSecond = 0, LateAverageMegabytesPerSecond = 0 };

        var thirdSize = samples.Count / 3;
        var early = samples.Take(thirdSize).Average();
        var late = samples.Skip(samples.Count - thirdSize).Average();

        var trend = early <= 0
            ? ThroughputTrend.Stable
            : (early - late) / early >= SignificantChangeFraction
                ? ThroughputTrend.Declining
                : (late - early) / early >= SignificantChangeFraction
                    ? ThroughputTrend.Improving
                    : ThroughputTrend.Stable;

        return new ThroughputTrendAnalysis { Trend = trend, EarlyAverageMegabytesPerSecond = early, LateAverageMegabytesPerSecond = late };
    }
}
