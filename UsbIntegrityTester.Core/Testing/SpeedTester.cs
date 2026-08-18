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
/// </summary>
public sealed class SpeedTester
{
    private const int SampleWindowBlocks = 8;

    public async Task<SpeedTestResult> MeasureWriteSpeedAsync(
        RawDiskAccessor accessor, ulong startOffset, ulong regionSizeBytes, int blockSize,
        TimeSpan duration, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[blockSize];
        CapacityVerifier.FillDeterministicBlock(buffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        return await RunAsync(
            TestPhase.MeasuringWriteSpeed, blockSize, duration, progress, cancellationToken,
            (offset) =>
            {
                accessor.WriteBlock(offset, buffer, blockSize);
            },
            startOffset, regionSizeBytes);
    }

    public async Task<SpeedTestResult> MeasureReadSpeedAsync(
        RawDiskAccessor accessor, ulong startOffset, ulong regionSizeBytes, int blockSize,
        TimeSpan duration, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[blockSize];

        return await RunAsync(
            TestPhase.MeasuringReadSpeed, blockSize, duration, progress, cancellationToken,
            (offset) =>
            {
                accessor.ReadBlock(offset, buffer, blockSize);
            },
            startOffset, regionSizeBytes);
    }

    private static Task<SpeedTestResult> RunAsync(
        TestPhase phase, int blockSize, TimeSpan duration, IProgress<TestProgress>? progress,
        CancellationToken cancellationToken, Action<ulong> performBlockIo, ulong startOffset, ulong regionSizeBytes)
    {
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var samples = new List<double>();
            var windowStopwatch = Stopwatch.StartNew();
            var offset = startOffset;
            var regionEnd = startOffset + regionSizeBytes;
            ulong totalBytes = 0;
            var blocksInWindow = 0;
            double peak = 0;

            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();

                performBlockIo(offset);
                totalBytes += (ulong)blockSize;
                blocksInWindow++;

                offset += (ulong)blockSize;
                if (offset + (ulong)blockSize > regionEnd) offset = startOffset; // wrap within the test region

                if (blocksInWindow >= SampleWindowBlocks)
                {
                    var windowSeconds = windowStopwatch.Elapsed.TotalSeconds;
                    var windowMegabytes = blocksInWindow * blockSize / 1_000_000.0;
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

                    blocksInWindow = 0;
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
