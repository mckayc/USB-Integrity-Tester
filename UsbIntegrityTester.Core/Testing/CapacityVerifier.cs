namespace UsbIntegrityTester.Core.Testing;

public sealed record CapacityVerificationResult
{
    /// <summary>Bytes counting up from offset 0 that verified correctly with no gaps — the trustworthy capacity.</summary>
    public required ulong VerifiedGoodBytes { get; init; }
    public required ulong TotalBytesTested { get; init; }
    public required int BlocksTested { get; init; }
    public required int BlocksFailed { get; init; }
    public bool AllBlocksPassed => BlocksFailed == 0;
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
    public async Task<CapacityVerificationResult> WriteAndVerifyAsync(
        RawDiskAccessor accessor,
        ulong totalBytes,
        int blockSize,
        ulong seed,
        IReadOnlyList<ulong> blockOffsets,
        IProgress<TestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[blockSize];
        var totalWork = (ulong)blockOffsets.Count * (ulong)blockSize * 2; // write pass + verify pass
        ulong workDone = 0;

        // Write pass: every block gets data derived from (seed, its own offset).
        foreach (var offset in blockOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FillDeterministicBlock(buffer, seed, offset);
            accessor.WriteBlock(offset, buffer, blockSize);

            workDone += (ulong)blockSize;
            progress?.Report(new TestProgress
            {
                Phase = TestPhase.WritingCapacityPattern,
                BytesProcessed = workDone,
                TotalBytes = totalWork,
            });
        }

        // Verify pass: read each block back and confirm it holds *its own* expected data.
        var expected = new byte[blockSize];
        var readBuffer = new byte[blockSize];
        var blocksFailed = 0;
        ulong verifiedGoodBytes = 0;
        var sawFailure = false;

        var orderedOffsets = blockOffsets.OrderBy(o => o).ToList();
        foreach (var offset in orderedOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            progress?.Report(new TestProgress
            {
                Phase = TestPhase.VerifyingCapacityPattern,
                BytesProcessed = workDone,
                TotalBytes = totalWork,
            });
        }

        return new CapacityVerificationResult
        {
            VerifiedGoodBytes = verifiedGoodBytes,
            TotalBytesTested = totalBytes,
            BlocksTested = blockOffsets.Count,
            BlocksFailed = blocksFailed,
        };
    }

    /// <summary>Produces deterministic pseudo-random bytes derived from (seed, blockOffset), reproducible without storing test data.</summary>
    internal static void FillDeterministicBlock(byte[] buffer, ulong seed, ulong blockOffset)
    {
        var state = seed ^ (blockOffset * 0x9E3779B97F4A7C15UL);
        for (var i = 0; i < buffer.Length; i += 8)
        {
            state = SplitMix64Next(state);
            var bytes = BitConverter.GetBytes(state);
            var count = Math.Min(8, buffer.Length - i);
            Array.Copy(bytes, 0, buffer, i, count);
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
