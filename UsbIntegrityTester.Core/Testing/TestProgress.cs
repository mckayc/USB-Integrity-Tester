namespace UsbIntegrityTester.Core.Testing;

public enum TestPhase
{
    WritingCapacityPattern,
    VerifyingCapacityPattern,

    /// <summary>The speed test's 4 configurable slots — each simulates whichever <see cref="SpeedTestCategory"/> was picked for that slot, with randomized file sizes across its range.</summary>
    MeasuringSpeedTestSlot1WriteSpeed,
    MeasuringSpeedTestSlot1ReadSpeed,
    MeasuringSpeedTestSlot2WriteSpeed,
    MeasuringSpeedTestSlot2ReadSpeed,
    MeasuringSpeedTestSlot3WriteSpeed,
    MeasuringSpeedTestSlot3ReadSpeed,
    MeasuringSpeedTestSlot4WriteSpeed,
    MeasuringSpeedTestSlot4ReadSpeed,

    /// <summary>The optional Real-World File Copy test — copies the user's own chosen files to the
    /// drive, then back to a folder on the computer while verifying each one against the original
    /// (by hash), instead of measuring synthetic data.</summary>
    CopyingRealFilesToUsb,
    CopyingAndVerifyingRealFilesFromUsb,

    /// <summary>File Mode only — deleting leftover test files, before a run (clearing the drive) or
    /// after one that was cancelled or failed. Can take a while if a lot of files were left behind.</summary>
    CleaningUp,

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

    /// <summary>0-based index of the file currently being copied, for per-file tests (currently just
    /// Real-World File Copy) — null for phases that don't work file-by-file. Paired with a caller's
    /// own already-known file list, this is enough to show "which files are done, which are next"
    /// without the engine having to ship the whole list on every progress tick.</summary>
    public int? CurrentFileIndex { get; init; }
    public string? CurrentFileRelativePath { get; init; }

    public double FractionComplete => TotalBytes == 0 ? 0 : (double)BytesProcessed / TotalBytes;
}
