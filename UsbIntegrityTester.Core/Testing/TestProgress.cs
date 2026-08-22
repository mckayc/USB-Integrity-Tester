namespace UsbIntegrityTester.Core.Testing;

public enum TestPhase
{
    WritingCapacityPattern,
    VerifyingCapacityPattern,

    /// <summary>The speed test's 4 configurable slots — each simulates whichever <see cref="SpeedTestCategory"/> was picked for that slot, with randomized file sizes across its range (or, for the CrystalDiskMark-replica categories, that fixed benchmark's own access pattern).</summary>
    MeasuringSpeedTestSlot1WriteSpeed,
    MeasuringSpeedTestSlot1ReadSpeed,
    MeasuringSpeedTestSlot2WriteSpeed,
    MeasuringSpeedTestSlot2ReadSpeed,
    MeasuringSpeedTestSlot3WriteSpeed,
    MeasuringSpeedTestSlot3ReadSpeed,
    MeasuringSpeedTestSlot4WriteSpeed,
    MeasuringSpeedTestSlot4ReadSpeed,

    /// <summary>Writes a large throwaway payload after all CrystalDiskMark-replica slots have been
    /// written but before any of them are read back, so even the most-recently-written slot's data
    /// gets evicted from the drive's own cache — without this, that one slot's read would still
    /// come back artificially fast since nothing pushed it out of cache yet.</summary>
    FlushingSpeedTestCache,

    Complete,
}

public sealed record TestProgress
{
    public required TestPhase Phase { get; init; }
    public required ulong BytesProcessed { get; init; }
    public required ulong TotalBytes { get; init; }
    public double? CurrentThroughputMegabytesPerSecond { get; init; }

    /// <summary>Running count of blocks that failed verification so far — lets callers react the moment a fake/corrupted block is found, without waiting for the run to finish.</summary>
    public int BlocksFailedSoFar { get; init; }

    public double FractionComplete => TotalBytes == 0 ? 0 : (double)BytesProcessed / TotalBytes;
}
