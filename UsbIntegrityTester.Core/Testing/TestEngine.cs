namespace UsbIntegrityTester.Core.Testing;

public sealed record TestResult
{
    public CapacityVerificationResult? Capacity { get; init; }
    public SpeedTestResult? Write { get; init; }
    public SpeedTestResult? Read { get; init; }
}

/// <summary>Orchestrates capacity verification and speed measurement against a raw physical drive.</summary>
public sealed class TestEngine
{
    private readonly CapacityVerifier _capacityVerifier = new();
    private readonly SpeedTester _speedTester = new();

    public async Task<TestResult> RunAsync(
        RawDiskAccessor accessor, ulong claimedCapacityBytes, TestSettings settings,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        CapacityVerificationResult? capacityResult = null;
        SpeedTestResult? writeResult = null;
        SpeedTestResult? readResult = null;

        if (settings.RunCapacityTest)
        {
            var offsets = BuildBlockOffsets(claimedCapacityBytes, settings);
            capacityResult = await _capacityVerifier.WriteAndVerifyAsync(
                accessor, claimedCapacityBytes, settings.BlockSizeBytes, seed: GenerateRunSeed(),
                offsets, progress, cancellationToken);
        }

        if (settings.RunSpeedTest)
        {
            // Measure against a region known to be real storage: the verified-good region if we
            // just ran a capacity test, otherwise the first slice of the claimed capacity.
            var regionSize = Math.Min(
                capacityResult?.VerifiedGoodBytes ?? claimedCapacityBytes,
                256UL * 1024 * 1024);
            regionSize = Math.Max(regionSize, (ulong)settings.BlockSizeBytes);

            writeResult = await _speedTester.MeasureWriteSpeedAsync(
                accessor, 0, regionSize, settings.BlockSizeBytes,
                settings.SustainedSpeedTestDuration, progress, cancellationToken);

            readResult = await _speedTester.MeasureReadSpeedAsync(
                accessor, 0, regionSize, settings.BlockSizeBytes,
                settings.SustainedSpeedTestDuration, progress, cancellationToken);
        }

        progress?.Report(new TestProgress
        {
            Phase = TestPhase.Complete,
            BytesProcessed = 1,
            TotalBytes = 1,
        });

        return new TestResult { Capacity = capacityResult, Write = writeResult, Read = readResult };
    }

    private static ulong GenerateRunSeed() => (ulong)DateTime.UtcNow.Ticks;

    private static IReadOnlyList<ulong> BuildBlockOffsets(ulong claimedCapacityBytes, TestSettings settings)
    {
        var blockSize = (ulong)settings.BlockSizeBytes;
        var blockCount = claimedCapacityBytes / blockSize;

        if (settings.ScanMode == ScanMode.Full || blockCount <= (ulong)settings.QuickScanSampleCount)
        {
            var all = new List<ulong>((int)blockCount);
            for (ulong i = 0; i < blockCount; i++) all.Add(i * blockSize);
            return all;
        }

        // Quick scan: evenly spaced samples across the full claimed range, so a fake drive that
        // only aliases the first N GB is still caught even though we're not testing every byte.
        var sampleCount = (ulong)settings.QuickScanSampleCount;
        var offsets = new List<ulong>((int)sampleCount);
        for (ulong i = 0; i < sampleCount; i++)
        {
            var blockIndex = i * blockCount / sampleCount;
            offsets.Add(blockIndex * blockSize);
        }

        return offsets;
    }
}
