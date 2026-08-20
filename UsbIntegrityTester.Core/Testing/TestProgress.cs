namespace UsbIntegrityTester.Core.Testing;

public enum TestPhase
{
    WritingCapacityPattern,
    VerifyingCapacityPattern,

    /// <summary>The speed test's 3 configurable slots — each simulates whichever <see cref="SpeedTestCategory"/> was picked for that slot, with randomized file sizes across its range.</summary>
    MeasuringSpeedTestSlot1WriteSpeed,
    MeasuringSpeedTestSlot1ReadSpeed,
    MeasuringSpeedTestSlot2WriteSpeed,
    MeasuringSpeedTestSlot2ReadSpeed,
    MeasuringSpeedTestSlot3WriteSpeed,
    MeasuringSpeedTestSlot3ReadSpeed,

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
