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

    /// <summary>True when the drive reported one of Windows'/a generic controller firmware's
    /// template model strings instead of a real vendor/model name — see <see cref="FraudDetector"/>'s
    /// remarks for why this is informational only, never a verdict-changing signal on its own.</summary>
    public bool HasGenericModelName { get; init; }
}

/// <summary>
/// Compares test results against the user's claimed stats and classifies the outcome.
/// </summary>
/// <remarks>
/// Deliberately does NOT check the drive's reported VID/PID against a "known counterfeit chipset"
/// list — counterfeit-capacity drives routinely clone the exact VID/PID of a genuine popular
/// product specifically to defeat identity checks like that, so a blocklist would mostly catch
/// honest generic drives while missing the drives it's meant to catch. What's actually informative
/// without being spoofable that easily is the *model string* Windows reports: genuine branded
/// drives (SanDisk, Samsung, Kingston, Crucial, ...) essentially always report their real product
/// name, while generic/no-name flash controllers commonly ship firmware that reports one of a small
/// set of template strings verbatim (or Windows falls back to its own generic USB mass-storage
/// class name when the device provides no descriptor at all). That's not proof of fraud by itself —
/// plenty of genuine unbranded drives exist — but it's a free, honest, corroborating signal worth
/// surfacing alongside the capacity/speed evidence.
/// </remarks>
public static class FraudDetector
{
    // Flash drives commonly under-report a little due to decimal-vs-binary GB conventions;
    // don't flag that as fraud.
    private const double CapacityToleranceFraction = 0.05;
    private const double SpeedToleranceFraction = 0.30;

    // Windows' own USB mass-storage class driver fallback, and firmware strings seen verbatim
    // across a huge population of generic/no-name flash controllers (not any one specific chip —
    // deliberately not trying to enumerate chip-specific VID/PIDs, see the class remarks above).
    private static readonly string[] GenericModelMarkers =
    {
        "Generic MassStorageClass USB Device",
        "USB Mass Storage Device",
        "Mass Storage Device",
        "USB2.0 Flash Disk",
        "USB 2.0 Flash Disk",
        "USB Flash Disk",
        "Generic Flash Disk",
        "USB DISK",
    };

    private static bool IsGenericModelName(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && GenericModelMarkers.Any(marker => model.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static VerdictResult Evaluate(
        ulong claimedCapacityBytes, double? claimedReadSpeedMegabytesPerSecond,
        double? claimedWriteSpeedMegabytesPerSecond, TestResult result, string? driveModel = null)
    {
        var isGenericModel = IsGenericModelName(driveModel);
        var genericModelNote = isGenericModel
            ? " Note: this drive reports a generic controller/model name rather than a specific vendor product — common on unbranded drives, but also common in counterfeit-capacity listings, so treat these results with extra scrutiny."
            : string.Empty;
        if (result.Capacity is null && result.Slot1 is null && result.Slot2 is null && result.Slot3 is null
            && result.Slot4 is null && result.RealFileCopy is null)
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

        // Compared against slot 1's results — it defaults to the Standard Benchmark (one large
        // synthetic sequential file), which is what packaging claims are conventionally based on.
        // If the user swapped slot 1 for a different category, the comparison follows it there.
        double? readAccuracy = claimedReadSpeedMegabytesPerSecond is > 0 && result.Slot1 is not null
            ? result.Slot1.Read.AverageMegabytesPerSecond / claimedReadSpeedMegabytesPerSecond.Value
            : null;

        double? writeAccuracy = claimedWriteSpeedMegabytesPerSecond is > 0 && result.Slot1 is not null
            ? result.Slot1.Write.AverageMegabytesPerSecond / claimedWriteSpeedMegabytesPerSecond.Value
            : null;

        var worstSpeedAccuracy = new[] { readAccuracy, writeAccuracy }.Where(a => a is not null).Select(a => a!.Value)
            .DefaultIfEmpty(double.NaN).Min();
        var isUnderperforming = !double.IsNaN(worstSpeedAccuracy) && worstSpeedAccuracy < 1 - SpeedToleranceFraction;

        if (isCounterfeit)
        {
            return new VerdictResult
            {
                Verdict = DriveVerdict.Counterfeit,
                Explanation = $"Only {capacityAccuracy:P0} of the claimed capacity verified as real, writable storage — this is the signature of a fake-capacity drive.{genericModelNote}",
                CapacityAccuracyFraction = capacityAccuracy,
                ReadSpeedAccuracyFraction = readAccuracy,
                WriteSpeedAccuracyFraction = writeAccuracy,
                HasGenericModelName = isGenericModel,
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
                Explanation = $"Capacity checks out, but sustained throughput fell short — {string.Join(" and ", parts)}.{genericModelNote}",
                CapacityAccuracyFraction = capacityAccuracy,
                ReadSpeedAccuracyFraction = readAccuracy,
                WriteSpeedAccuracyFraction = writeAccuracy,
                HasGenericModelName = isGenericModel,
            };
        }

        return new VerdictResult
        {
            Verdict = DriveVerdict.Ok,
            Explanation = $"Capacity and speed both matched the claimed stats within tolerance.{genericModelNote}",
            CapacityAccuracyFraction = capacityAccuracy,
            ReadSpeedAccuracyFraction = readAccuracy,
            WriteSpeedAccuracyFraction = writeAccuracy,
            HasGenericModelName = isGenericModel,
        };
    }
}
