using System.Diagnostics;

namespace UsbIntegrityTester.Core.Testing;

public sealed record SpeedTestResult
{
    public required double AverageMegabytesPerSecond { get; init; }
    public required double PeakBurstMegabytesPerSecond { get; init; }
    public required IReadOnlyList<double> SampledMegabytesPerSecond { get; init; }
}

/// <summary>
/// Measures sustained throughput long enough to see past SLC-cache burst speeds falling off a
/// cliff once the cache is exhausted — a short test only measures the cache, not the drive.
/// Simulates a category's real file-size range by picking a new random size (sector-aligned) each
/// time the "current file" completes, physically writing/reading it in fixed-size chunks so even a
/// multi-gigabyte simulated file still produces plenty of throughput samples.
/// </summary>
public sealed class SpeedTester
{
    private const int IoChunkBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan SampleWindowInterval = TimeSpan.FromMilliseconds(250);

    public Task<SpeedTestResult> MeasureWriteSpeedAsync(
        RawDiskAccessor accessor, ulong startOffset, ulong regionSizeBytes, long minFileSizeBytes, long maxFileSizeBytes,
        TimeSpan duration, TestPhase phase, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var chunkBuffer = new byte[IoChunkBytes];
        CapacityVerifier.FillDeterministicBlock(chunkBuffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        return RunAsync(phase, duration, progress, cancellationToken,
            (offset, length) => accessor.WriteBlock(offset, chunkBuffer, length),
            startOffset, regionSizeBytes, minFileSizeBytes, maxFileSizeBytes);
    }

    public Task<SpeedTestResult> MeasureReadSpeedAsync(
        RawDiskAccessor accessor, ulong startOffset, ulong regionSizeBytes, long minFileSizeBytes, long maxFileSizeBytes,
        TimeSpan duration, TestPhase phase, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var chunkBuffer = new byte[IoChunkBytes];

        return RunAsync(phase, duration, progress, cancellationToken,
            (offset, length) => accessor.ReadBlock(offset, chunkBuffer, length),
            startOffset, regionSizeBytes, minFileSizeBytes, maxFileSizeBytes);
    }

    private static Task<SpeedTestResult> RunAsync(
        TestPhase phase, TimeSpan duration, IProgress<TestProgress>? progress, CancellationToken cancellationToken,
        Action<ulong, int> performChunkIo, ulong startOffset, ulong regionSizeBytes, long minFileSizeBytes, long maxFileSizeBytes)
    {
        return Task.Run(() =>
        {
            var rng = Random.Shared;
            var stopwatch = Stopwatch.StartNew();
            var windowStopwatch = Stopwatch.StartNew();
            var samples = new List<double>();
            var offset = startOffset;
            var regionEnd = startOffset + regionSizeBytes;
            long bytesInWindow = 0;
            double peak = 0;

            long currentFileSize = 0;
            long bytesInCurrentFile = 0;

            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (bytesInCurrentFile >= currentFileSize)
                {
                    currentFileSize = FileSizeRandomizer.NextAlignedSize(rng, minFileSizeBytes, maxFileSizeBytes);
                    bytesInCurrentFile = 0;
                }

                var chunkLength = (int)Math.Min(IoChunkBytes, currentFileSize - bytesInCurrentFile);
                if (offset + (ulong)chunkLength > regionEnd) offset = startOffset; // wrap within the test region

                performChunkIo(offset, chunkLength);

                offset += (ulong)chunkLength;
                bytesInCurrentFile += chunkLength;
                bytesInWindow += chunkLength;

                if (windowStopwatch.Elapsed >= SampleWindowInterval)
                {
                    var windowSeconds = windowStopwatch.Elapsed.TotalSeconds;
                    var windowMegabytes = bytesInWindow / 1_000_000.0;
                    var mbPerSec = windowSeconds > 0 ? windowMegabytes / windowSeconds : 0;
                    samples.Add(mbPerSec);
                    peak = Math.Max(peak, mbPerSec);

                    var elapsedFraction = Math.Clamp(stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds, 0, 1);
                    progress?.Report(new TestProgress
                    {
                        Phase = phase,
                        BytesProcessed = (ulong)(elapsedFraction * 1000),
                        TotalBytes = 1000, // speed tests are time-bounded, not byte-bounded — progress reflects elapsed time
                        CurrentThroughputMegabytesPerSecond = mbPerSec,
                    });

                    bytesInWindow = 0;
                    windowStopwatch.Restart();
                }
            }

            var averageMbPerSec = samples.Count > 0 ? samples.Average() : 0;
            return new SpeedTestResult
            {
                AverageMegabytesPerSecond = averageMbPerSec,
                PeakBurstMegabytesPerSecond = peak,
                SampledMegabytesPerSecond = samples,
            };
        }, cancellationToken);
    }
}

/// <summary>Shared by both engines so File Mode and Raw Mode pick sizes the same way.</summary>
internal static class FileSizeRandomizer
{
    /// <summary>A random size in [min, max], rounded down to a multiple of the sector size (unbuffered I/O requires alignment) and never below one sector.</summary>
    public static long NextAlignedSize(Random rng, long minBytes, long maxBytes, int alignment = UnbufferedFile.SectorSize)
    {
        var raw = minBytes >= maxBytes ? minBytes : rng.NextInt64(minBytes, maxBytes + 1);
        var aligned = raw / alignment * alignment;
        return Math.Max(aligned, alignment);
    }
}
