using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.Core.Verdict;

public enum DriveVerdict
{
    /// <summary>Capacity and speed both meet what was claimed, within tolerance.</summary>
    Ok,

    /// <summary>Capacity checks out but sustained speed falls meaningfully short of the claim.</summary>
    Underperforming,

    /// <summary>Verified good capacity is meaningfully less than claimed — classic counterfeit signature.</summary>
    Counterfeit,

    /// <summary>The test itself could not complete (I/O errors, drive disconnected, etc).</summary>
    Failed,
}

public sealed record VerdictResult
{
    public required DriveVerdict Verdict { get; init; }
    public required string Explanation { get; init; }
    public double? CapacityAccuracyFraction { get; init; }
    public double? ReadSpeedAccuracyFraction { get; init; }
    public double? WriteSpeedAccuracyFraction { get; init; }
}

/// <summary>Compares test results against the user's claimed stats and classifies the outcome.</summary>
public static class FraudDetector
{
    // Flash drives commonly under-report a little due to decimal-vs-binary GB conventions;
    // don't flag that as fraud.
    private const double CapacityToleranceFraction = 0.05;
    private const double SpeedToleranceFraction = 0.30;

    public static VerdictResult Evaluate(
        ulong claimedCapacityBytes, double? claimedReadSpeedMegabytesPerSecond,
        double? claimedWriteSpeedMegabytesPerSecond, TestResult result)
    {
        if (result.Capacity is null && result.Write is null && result.Read is null)
        {
            return new VerdictResult { Verdict = DriveVerdict.Failed, Explanation = "No test data was collected." };
        }

        double? capacityAccuracy = null;
        var isCounterfeit = false;

        if (result.Capacity is not null)
        {
            capacityAccuracy = claimedCapacityBytes == 0
                ? null
                : (double)result.Capacity.VerifiedGoodBytes / claimedCapacityBytes;

            isCounterfeit = !result.Capacity.AllBlocksPassed
                && capacityAccuracy is not null
                && capacityAccuracy < 1 - CapacityToleranceFraction;
        }

        // Compared against the large-file (sequential) results — that's what packaging claims are
        // conventionally based on, not the small-file numbers.
        double? readAccuracy = claimedReadSpeedMegabytesPerSecond is > 0 && result.Read is not null
            ? result.Read.AverageMegabytesPerSecond / claimedReadSpeedMegabytesPerSecond.Value
            : null;

        double? writeAccuracy = claimedWriteSpeedMegabytesPerSecond is > 0 && result.Write is not null
            ? result.Write.AverageMegabytesPerSecond / claimedWriteSpeedMegabytesPerSecond.Value
            : null;

        var worstSpeedAccuracy = new[] { readAccuracy, writeAccuracy }.Where(a => a is not null).Select(a => a!.Value)
            .DefaultIfEmpty(double.NaN).Min();
        var isUnderperforming = !double.IsNaN(worstSpeedAccuracy) && worstSpeedAccuracy < 1 - SpeedToleranceFraction;

        if (isCounterfeit)
        {
            return new VerdictResult
            {
                Verdict = DriveVerdict.Counterfeit,
                Explanation = $"Only {capacityAccuracy:P0} of the claimed capacity verified as real, writable storage — this is the signature of a fake-capacity drive.",
                CapacityAccuracyFraction = capacityAccuracy,
                ReadSpeedAccuracyFraction = readAccuracy,
                WriteSpeedAccuracyFraction = writeAccuracy,
            };
        }

        if (isUnderperforming)
        {
            var parts = new List<string>();
            if (readAccuracy is { } ra && ra < 1 - SpeedToleranceFraction) parts.Add($"read was {ra:P0} of claimed");
            if (writeAccuracy is { } wa && wa < 1 - SpeedToleranceFraction) parts.Add($"write was {wa:P0} of claimed");

            return new VerdictResult
            {
                Verdict = DriveVerdict.Underperforming,
                Explanation = $"Capacity checks out, but sustained throughput fell short — {string.Join(" and ", parts)}.",
                CapacityAccuracyFraction = capacityAccuracy,
                ReadSpeedAccuracyFraction = readAccuracy,
                WriteSpeedAccuracyFraction = writeAccuracy,
            };
        }

        return new VerdictResult
        {
            Verdict = DriveVerdict.Ok,
            Explanation = "Capacity and speed both matched the claimed stats within tolerance.",
            CapacityAccuracyFraction = capacityAccuracy,
            ReadSpeedAccuracyFraction = readAccuracy,
            WriteSpeedAccuracyFraction = writeAccuracy,
        };
    }
}
