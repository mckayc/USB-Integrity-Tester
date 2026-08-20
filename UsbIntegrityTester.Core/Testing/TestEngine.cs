namespace UsbIntegrityTester.Core.Testing;

public sealed record TestResult
{
    public CapacityVerificationResult? Capacity { get; init; }

    /// <summary>Large sequential blocks (1 MiB) — what you'd get copying one big file.</summary>
    public SpeedTestResult? Write { get; init; }
    public SpeedTestResult? Read { get; init; }

    /// <summary>Small blocks (4 KiB) — what you'd get copying many small files. Often much slower on cheap drives.</summary>
    public SpeedTestResult? SmallFileWrite { get; init; }
    public SpeedTestResult? SmallFileRead { get; init; }

    /// <summary>Huge blocks (100 MiB) — what you'd get copying one very large file (video export, disk image).</summary>
    public SpeedTestResult? HugeFileWrite { get; init; }
    public SpeedTestResult? HugeFileRead { get; init; }
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
        SpeedTestResult? smallFileWriteResult = null;
        SpeedTestResult? smallFileReadResult = null;
        SpeedTestResult? hugeFileWriteResult = null;
        SpeedTestResult? hugeFileReadResult = null;

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
            var availableBytes = capacityResult?.VerifiedGoodBytes ?? claimedCapacityBytes;
            var regionSize = Math.Min(availableBytes, 256UL * 1024 * 1024);
            regionSize = Math.Max(regionSize, (ulong)settings.BlockSizeBytes);

            if (settings.RunLargeFileSpeedTest)
            {
                // Large-file simulation: big sequential blocks — the best-case number drives are marketed on.
                writeResult = await _speedTester.MeasureWriteSpeedAsync(
                    accessor, 0, regionSize, settings.BlockSizeBytes, settings.SustainedSpeedTestDuration,
                    TestPhase.MeasuringLargeFileWriteSpeed, progress, cancellationToken);

                readResult = await _speedTester.MeasureReadSpeedAsync(
                    accessor, 0, regionSize, settings.BlockSizeBytes, settings.SustainedSpeedTestDuration,
                    TestPhase.MeasuringLargeFileReadSpeed, progress, cancellationToken);
            }

            if (settings.RunSmallFileSpeedTest)
            {
                // Small-file simulation: same region, much smaller blocks — this is where cheap
                // controllers and thin caches usually fall apart, and it's rarely what's advertised.
                smallFileWriteResult = await _speedTester.MeasureWriteSpeedAsync(
                    accessor, 0, regionSize, settings.SmallFileBlockSizeBytes, settings.SustainedSpeedTestDuration,
                    TestPhase.MeasuringSmallFileWriteSpeed, progress, cancellationToken);

                smallFileReadResult = await _speedTester.MeasureReadSpeedAsync(
                    accessor, 0, regionSize, settings.SmallFileBlockSizeBytes, settings.SustainedSpeedTestDuration,
                    TestPhase.MeasuringSmallFileReadSpeed, progress, cancellationToken);
            }

            // Guarded separately: a 100 MiB block needs a region at least that big, which a tiny
            // (or tiny-because-fake) drive might not have — skip rather than write past real storage.
            if (settings.RunHugeFileSpeedTest && availableBytes >= (ulong)settings.HugeFileBlockSizeBytes)
            {
                var hugeRegionSize = Math.Min(availableBytes, 256UL * 1024 * 1024);
                hugeRegionSize = Math.Max(hugeRegionSize, (ulong)settings.HugeFileBlockSizeBytes);

                hugeFileWriteResult = await _speedTester.MeasureWriteSpeedAsync(
                    accessor, 0, hugeRegionSize, settings.HugeFileBlockSizeBytes, settings.SustainedSpeedTestDuration,
                    TestPhase.MeasuringHugeFileWriteSpeed, progress, cancellationToken);

                hugeFileReadResult = await _speedTester.MeasureReadSpeedAsync(
                    accessor, 0, hugeRegionSize, settings.HugeFileBlockSizeBytes, settings.SustainedSpeedTestDuration,
                    TestPhase.MeasuringHugeFileReadSpeed, progress, cancellationToken);
            }
        }

        progress?.Report(new TestProgress
        {
            Phase = TestPhase.Complete,
            BytesProcessed = 1,
            TotalBytes = 1,
        });

        return new TestResult
        {
            Capacity = capacityResult,
            Write = writeResult,
            Read = readResult,
            SmallFileWrite = smallFileWriteResult,
            SmallFileRead = smallFileReadResult,
            HugeFileWrite = hugeFileWriteResult,
            HugeFileRead = hugeFileReadResult,
        };
    }

    private static ulong GenerateRunSeed() => (ulong)DateTime.UtcNow.Ticks;

    private static IReadOnlyList<ulong> BuildBlockOffsets(ulong claimedCapacityBytes, TestSettings settings)
    {
        var blockSize = (ulong)settings.BlockSizeBytes;
        var blockCount = claimedCapacityBytes / blockSize;

        if (settings.CapacityScanDepth == CapacityScanDepth.Full)
            return AllOffsets(blockCount, blockSize);

        var sampleCount = settings.CapacityScanDepth switch
        {
            CapacityScanDepth.Quick => (ulong)settings.QuickScanSampleCount,
            CapacityScanDepth.Standard => (ulong)Math.Max(
                settings.QuickScanSampleCount, (long)(blockCount * settings.StandardScanSampleFraction)),
            _ => (ulong)settings.QuickScanSampleCount,
        };

        if (blockCount <= sampleCount)
            return AllOffsets(blockCount, blockSize);

        // Evenly spaced samples across the full claimed range, so a fake drive that only aliases
        // the first N GB is still caught even though we're not testing every byte.
        var offsets = new List<ulong>((int)sampleCount);
        for (ulong i = 0; i < sampleCount; i++)
        {
            var blockIndex = i * blockCount / sampleCount;
            offsets.Add(blockIndex * blockSize);
        }

        return offsets;
    }

    private static IReadOnlyList<ulong> AllOffsets(ulong blockCount, ulong blockSize)
    {
        var all = new List<ulong>((int)blockCount);
        for (ulong i = 0; i < blockCount; i++) all.Add(i * blockSize);
        return all;
    }
}
