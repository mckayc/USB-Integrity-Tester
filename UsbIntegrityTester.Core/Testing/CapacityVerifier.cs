using System.Buffers.Binary;
using System.Diagnostics;

namespace UsbIntegrityTester.Core.Testing;

public sealed record CapacityVerificationResult
{
    /// <summary>Bytes counting up from offset 0 that verified correctly with no gaps — the trustworthy capacity.</summary>
    public required ulong VerifiedGoodBytes { get; init; }
    public required ulong TotalBytesTested { get; init; }
    public required int BlocksTested { get; init; }
    public required int BlocksFailed { get; init; }
    public bool AllBlocksPassed => BlocksFailed == 0;

    /// <summary>
    /// Average write throughput measured incidentally during the write pass — since this test
    /// already writes across the whole scanned region (not just the small capped region the
    /// dedicated speed test reuses), it doubles as a second, independent write measurement.
    /// </summary>
    public double WriteMegabytesPerSecond { get; init; }

    /// <summary>Same idea for the verify pass, which reads every tested block back.</summary>
    public double ReadMegabytesPerSecond { get; init; }
}

/// <summary>
/// Thrown when a capacity test is cancelled partway through, carrying whatever had already been
/// proven — a multi-hour scan that gets stopped shouldn't throw that work away. Deliberately not
/// tied to the run's CancellationToken: a Task.Run delegate that throws an OperationCanceledException
/// carrying its own token becomes a "cancelled" task, and awaiting that rethrows a bare
/// TaskCanceledException with this data gone. Still an OperationCanceledException, so callers that
/// don't care about the partial result keep treating it as an ordinary cancel.
/// </summary>
public sealed class CapacityTestCancelledException : OperationCanceledException
{
    /// <summary>Bytes of test pattern written before the stop.</summary>
    public ulong BytesWritten { get; }

    /// <summary>Bytes read back and checked before the stop — 0 if it was stopped during the write pass.</summary>
    public ulong BytesVerified { get; }

    /// <summary>Bytes counting up from the start that verified correctly with no gaps, as far as verification got.</summary>
    public ulong VerifiedGoodBytes { get; }

    public int BlocksFailed { get; }

    public CapacityTestCancelledException(ulong bytesWritten, ulong bytesVerified, ulong verifiedGoodBytes, int blocksFailed)
        : base("Capacity test cancelled.")
    {
        BytesWritten = bytesWritten;
        BytesVerified = bytesVerified;
        VerifiedGoodBytes = verifiedGoodBytes;
        BlocksFailed = blocksFailed;
    }
}

/// <summary>
/// Detects fake-capacity ("counterfeit") flash drives. Naive tests (write zeros, read zeros back)
/// are fooled by drives that alias a small amount of real flash across a larger reported address
/// space. Instead this writes data that is uniquely derived from its own block offset, so a block
/// that reads back with the *wrong* offset's data — or garbage — proves that offset isn't real
/// storage, rather than just proving "some byte pattern survived."
/// </summary>
public sealed class CapacityVerifier
{
    // See the matching comment in SpeedTester — a window closes on whichever comes first, enough
    // blocks or enough elapsed time, so a large configured block size can't stall progress reporting.
    private const int SampleWindowBlocks = 8;
    private static readonly TimeSpan SampleWindowInterval = TimeSpan.FromMilliseconds(250);

    public Task<CapacityVerificationResult> WriteAndVerifyAsync(
        RawDiskAccessor accessor,
        ulong totalBytes,
        int blockSize,
        ulong seed,
        IReadOnlyList<ulong> blockOffsets,
        IProgress<TestProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var buffer = new byte[blockSize];
        var totalWork = (ulong)blockOffsets.Count * (ulong)blockSize * 2; // write pass + verify pass
        ulong workDone = 0;

        // Write pass: every block gets data derived from (seed, its own offset).
        var writeStopwatch = Stopwatch.StartNew();
        var windowStopwatch = Stopwatch.StartNew();
        var blocksInWindow = 0;

        foreach (var offset in blockOffsets)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new CapacityTestCancelledException(workDone, 0, 0, 0);
            FillDeterministicBlock(buffer, seed, offset);
            accessor.WriteBlock(offset, buffer, blockSize);

            workDone += (ulong)blockSize;
            blocksInWindow++;

            double? currentMbPerSec = null;
            if (blocksInWindow >= SampleWindowBlocks || windowStopwatch.Elapsed >= SampleWindowInterval)
            {
                var windowSeconds = windowStopwatch.Elapsed.TotalSeconds;
                var windowMegabytes = blocksInWindow * blockSize / 1_000_000.0;
                currentMbPerSec = windowSeconds > 0 ? windowMegabytes / windowSeconds : 0;
                blocksInWindow = 0;
                windowStopwatch.Restart();
            }

            progress?.Report(new TestProgress
            {
                Phase = TestPhase.WritingCapacityPattern,
                BytesProcessed = workDone,
                TotalBytes = totalWork,
                CurrentThroughputMegabytesPerSecond = currentMbPerSec,
            });
        }

        var writeElapsedSeconds = writeStopwatch.Elapsed.TotalSeconds;
        var writeMegabytesPerSecond = writeElapsedSeconds > 0
            ? blockOffsets.Count * blockSize / 1_000_000.0 / writeElapsedSeconds
            : 0;

        // Verify pass: read each block back and confirm it holds *its own* expected data.
        var expected = new byte[blockSize];
        var readBuffer = new byte[blockSize];
        var blocksFailed = 0;
        ulong verifiedGoodBytes = 0;
        var sawFailure = false;

        var readStopwatch = Stopwatch.StartNew();
        windowStopwatch.Restart();
        blocksInWindow = 0;

        var bytesWritten = workDone;
        var orderedOffsets = blockOffsets.OrderBy(o => o).ToList();
        foreach (var offset in orderedOffsets)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new CapacityTestCancelledException(bytesWritten, workDone - bytesWritten, verifiedGoodBytes, blocksFailed);
            FillDeterministicBlock(expected, seed, offset);
            accessor.ReadBlock(offset, readBuffer, blockSize);

            if (readBuffer.AsSpan().SequenceEqual(expected))
            {
                if (!sawFailure) verifiedGoodBytes = offset + (ulong)blockSize;
            }
            else
            {
                blocksFailed++;
                sawFailure = true;
            }

            workDone += (ulong)blockSize;
            blocksInWindow++;

            double? currentMbPerSec = null;
            if (blocksInWindow >= SampleWindowBlocks || windowStopwatch.Elapsed >= SampleWindowInterval)
            {
                var windowSeconds = windowStopwatch.Elapsed.TotalSeconds;
                var windowMegabytes = blocksInWindow * blockSize / 1_000_000.0;
                currentMbPerSec = windowSeconds > 0 ? windowMegabytes / windowSeconds : 0;
                blocksInWindow = 0;
                windowStopwatch.Restart();
            }

            progress?.Report(new TestProgress
            {
                Phase = TestPhase.VerifyingCapacityPattern,
                BytesProcessed = workDone,
                TotalBytes = totalWork,
                BlocksFailedSoFar = blocksFailed,
                CurrentThroughputMegabytesPerSecond = currentMbPerSec,
            });
        }

        var readElapsedSeconds = readStopwatch.Elapsed.TotalSeconds;
        var readMegabytesPerSecond = readElapsedSeconds > 0
            ? orderedOffsets.Count * blockSize / 1_000_000.0 / readElapsedSeconds
            : 0;

        return new CapacityVerificationResult
        {
            VerifiedGoodBytes = verifiedGoodBytes,
            TotalBytesTested = totalBytes,
            BlocksTested = blockOffsets.Count,
            BlocksFailed = blocksFailed,
            WriteMegabytesPerSecond = writeMegabytesPerSecond,
            ReadMegabytesPerSecond = readMegabytesPerSecond,
        };
    }, cancellationToken);

    /// <summary>Produces deterministic pseudo-random bytes derived from (seed, blockOffset), reproducible without storing test data.</summary>
    internal static void FillDeterministicBlock(Span<byte> buffer, ulong seed, ulong blockOffset)
    {
        var state = seed ^ (blockOffset * 0x9E3779B97F4A7C15UL);
        var i = 0;
        for (; i + 8 <= buffer.Length; i += 8)
        {
            state = SplitMix64Next(state);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(i, 8), state);
        }

        if (i < buffer.Length)
        {
            Span<byte> tail = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tail, SplitMix64Next(state));
            tail[..(buffer.Length - i)].CopyTo(buffer[i..]);
        }
    }

    private static ulong SplitMix64Next(ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
