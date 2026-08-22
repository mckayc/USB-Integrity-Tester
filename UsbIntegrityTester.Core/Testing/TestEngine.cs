namespace UsbIntegrityTester.Core.Testing;

/// <summary>One speed test slot's result — which category it simulated, plus its write/read measurements.</summary>
public sealed record SpeedTestSlotResult(SpeedTestCategory Category, SpeedTestResult Write, SpeedTestResult Read);

public sealed record TestResult
{
    public CapacityVerificationResult? Capacity { get; init; }
    public SpeedTestSlotResult? Slot1 { get; init; }
    public SpeedTestSlotResult? Slot2 { get; init; }
    public SpeedTestSlotResult? Slot3 { get; init; }
    public SpeedTestSlotResult? Slot4 { get; init; }
}

/// <summary>Orchestrates capacity verification and speed measurement against a raw physical drive.</summary>
public sealed class TestEngine
{
    private readonly CapacityVerifier _capacityVerifier = new();
    private readonly SpeedTester _speedTester = new();

    /// <param name="waitForNextTest">
    /// Called between top-level tests (Capacity, then each enabled speed test slot — never between
    /// a test's own write and read/verify passes) so a caller can pause the run there, e.g. to let
    /// a person manually advance one test at a time while recording. Never called before the first
    /// test that actually runs. Pass null to run straight through with no pauses.
    /// </param>
    public async Task<TestResult> RunAsync(
        RawDiskAccessor accessor, ulong claimedCapacityBytes, TestSettings settings,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? waitForNextTest = null)
    {
        CapacityVerificationResult? capacityResult = null;
        SpeedTestSlotResult? slot1Result = null;
        SpeedTestSlotResult? slot2Result = null;
        SpeedTestSlotResult? slot3Result = null;
        SpeedTestSlotResult? slot4Result = null;

        var hasRunAnyTest = false;
        async Task GateAsync()
        {
            if (hasRunAnyTest && waitForNextTest is not null) await waitForNextTest(cancellationToken);
            hasRunAnyTest = true;
        }

        if (settings.RunCapacityTest)
        {
            await GateAsync();
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

            slot1Result = await RunSlotAsync(accessor, settings.SpeedTestSlot1, availableBytes, settings,
                TestPhase.MeasuringSpeedTestSlot1WriteSpeed, TestPhase.MeasuringSpeedTestSlot1ReadSpeed, GateAsync, progress, cancellationToken);
            slot2Result = await RunSlotAsync(accessor, settings.SpeedTestSlot2, availableBytes, settings,
                TestPhase.MeasuringSpeedTestSlot2WriteSpeed, TestPhase.MeasuringSpeedTestSlot2ReadSpeed, GateAsync, progress, cancellationToken);
            slot3Result = await RunSlotAsync(accessor, settings.SpeedTestSlot3, availableBytes, settings,
                TestPhase.MeasuringSpeedTestSlot3WriteSpeed, TestPhase.MeasuringSpeedTestSlot3ReadSpeed, GateAsync, progress, cancellationToken);
            slot4Result = await RunSlotAsync(accessor, settings.SpeedTestSlot4, availableBytes, settings,
                TestPhase.MeasuringSpeedTestSlot4WriteSpeed, TestPhase.MeasuringSpeedTestSlot4ReadSpeed, GateAsync, progress, cancellationToken);
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
            Slot1 = slot1Result,
            Slot2 = slot2Result,
            Slot3 = slot3Result,
            Slot4 = slot4Result,
        };
    }

    private async Task<SpeedTestSlotResult?> RunSlotAsync(
        RawDiskAccessor accessor, SpeedTestSlotSettings slot, ulong availableBytes, TestSettings settings,
        TestPhase writePhase, TestPhase readPhase, Func<Task> gateAsync,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        if (!slot.Enabled) return null;

        var info = SpeedTestCategoryCatalog.Get(slot.Category);

        // A tiny (or tiny-because-fake) drive might not have room for even the category's smallest
        // file — skip rather than write past real storage. When it does have room but not quite
        // enough for the category's largest file, shrink the effective max down to what's actually
        // available instead of refusing to run at all.
        if (availableBytes < (ulong)info.MinFileSizeBytes) return null;
        var effectiveMaxBytes = Math.Min(info.MaxFileSizeBytes, (long)availableBytes);
        var effectiveMinBytes = Math.Min(info.MinFileSizeBytes, effectiveMaxBytes);

        // The working region needs room for a few files at the chosen size so writes actually wrap
        // and rotate rather than just overwriting the same handful of bytes — but never more than
        // what's actually verified-good.
        var desiredRegion = Math.Max(256UL * 1024 * 1024, (ulong)effectiveMaxBytes * 2);
        var regionSize = Math.Clamp(desiredRegion, (ulong)effectiveMaxBytes, availableBytes);

        await gateAsync();

        var writeResult = await _speedTester.MeasureWriteSpeedAsync(
            accessor, 0, regionSize, effectiveMinBytes, effectiveMaxBytes, settings.SustainedSpeedTestDuration,
            writePhase, progress, cancellationToken);

        var readResult = await _speedTester.MeasureReadSpeedAsync(
            accessor, 0, regionSize, effectiveMinBytes, effectiveMaxBytes, settings.SustainedSpeedTestDuration,
            readPhase, progress, cancellationToken);

        return new SpeedTestSlotResult(slot.Category, writeResult, readResult);
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
