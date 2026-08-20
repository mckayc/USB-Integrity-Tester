namespace UsbIntegrityTester.Core.Testing;

public enum TestPhase
{
    WritingCapacityPattern,
    VerifyingCapacityPattern,

    /// <summary>Large sequential blocks (1 MiB) — simulates copying a big single file.</summary>
    MeasuringLargeFileWriteSpeed,
    MeasuringLargeFileReadSpeed,

    /// <summary>Small blocks (4 KiB) — simulates copying many small files, often the weak point of cheap drives.</summary>
    MeasuringSmallFileWriteSpeed,
    MeasuringSmallFileReadSpeed,

    /// <summary>Huge blocks (100 MiB) — simulates copying one very large file, e.g. a video export or disk image.</summary>
    MeasuringHugeFileWriteSpeed,
    MeasuringHugeFileReadSpeed,

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
