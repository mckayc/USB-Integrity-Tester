using System.Diagnostics;

namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// Runs capacity and speed tests by writing real, individually-named files onto the drive's
/// mounted filesystem — visible live in Explorer while the test runs — instead of raw sectors.
/// Uses the same offset/index-derived verification data as the raw-mode CapacityVerifier, so a
/// drive that silently aliases addresses still gets caught: earlier files read back wrong once
/// later writes wrap onto the same real flash.
/// </summary>
public sealed class FileModeEngine
{
    public const string TestFolderName = "UsbIntegrityTest";

    private const int RotatingSpeedTestFileCount = 4;

    public static string GetTestFolderPath(string volumeLetter) =>
        Path.Combine(volumeLetter.TrimEnd('\\') + "\\", TestFolderName);

    /// <summary>Best-effort cleanup — used after a test, on app close, or via a manual "clean up now" action.</summary>
    public static void TryDeleteTestFolder(string volumeLetter)
    {
        try
        {
            var folder = GetTestFolderPath(volumeLetter);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Files still open elsewhere (e.g. Explorer preview) — leave them, nothing destructive about it.
        }
    }

    public async Task<TestResult> RunAsync(
        string volumeLetter, ulong claimedCapacityBytes, TestSettings settings,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var driveInfo = new System.IO.DriveInfo(volumeLetter);
        var testFolder = GetTestFolderPath(volumeLetter);

        if (settings.ClearExistingData)
            ClearDrive(volumeLetter, testFolder);
        else
            Directory.CreateDirectory(testFolder);

        // Recompute available space after any clearing, so the fill target reflects reality.
        driveInfo = new System.IO.DriveInfo(volumeLetter);
        var usableBytes = settings.ClearExistingData
            ? (ulong)(driveInfo.TotalSize * 0.95)
            : (ulong)(driveInfo.AvailableFreeSpace * 0.95);
        var fullTarget = Math.Min(claimedCapacityBytes, usableBytes);

        CapacityVerificationResult? capacityResult = null;
        if (settings.RunCapacityTest)
        {
            var targetBytes = settings.CapacityScanDepth switch
            {
                CapacityScanDepth.Quick => Math.Min(fullTarget, 1_000_000_000UL), // up to 1 GB
                CapacityScanDepth.Standard => (ulong)(fullTarget * 0.25),
                CapacityScanDepth.Full => fullTarget,
                _ => fullTarget,
            };

            capacityResult = await RunCapacityTestAsync(
                testFolder, targetBytes, claimedCapacityBytes, settings.BlockSizeBytes, progress, cancellationToken);
        }

        SpeedTestResult? writeResult = null, readResult = null;
        SpeedTestResult? smallFileWriteResult = null, smallFileReadResult = null;
        SpeedTestResult? hugeFileWriteResult = null, hugeFileReadResult = null;

        if (settings.RunSpeedTest)
        {
            if (settings.RunLargeFileSpeedTest)
            {
                writeResult = await MeasureWriteSpeedAsync(testFolder, "large", settings.BlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringLargeFileWriteSpeed, progress, cancellationToken);
                readResult = await MeasureReadSpeedAsync(testFolder, "large", settings.BlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringLargeFileReadSpeed, progress, cancellationToken);
            }

            if (settings.RunSmallFileSpeedTest)
            {
                smallFileWriteResult = await MeasureWriteSpeedAsync(testFolder, "small", settings.SmallFileBlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringSmallFileWriteSpeed, progress, cancellationToken);
                smallFileReadResult = await MeasureReadSpeedAsync(testFolder, "small", settings.SmallFileBlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringSmallFileReadSpeed, progress, cancellationToken);
            }

            if (settings.RunHugeFileSpeedTest && fullTarget >= (ulong)settings.HugeFileBlockSizeBytes * RotatingSpeedTestFileCount)
            {
                hugeFileWriteResult = await MeasureWriteSpeedAsync(testFolder, "huge", settings.HugeFileBlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringHugeFileWriteSpeed, progress, cancellationToken);
                hugeFileReadResult = await MeasureReadSpeedAsync(testFolder, "huge", settings.HugeFileBlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringHugeFileReadSpeed, progress, cancellationToken);
            }
        }

        if (settings.CleanupPolicy == TestCleanupPolicy.DeleteAfterTest)
            TryDeleteTestFolder(volumeLetter);

        progress?.Report(new TestProgress { Phase = TestPhase.Complete, BytesProcessed = 1, TotalBytes = 1 });

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

    private static void ClearDrive(string volumeLetter, string testFolder)
    {
        var root = volumeLetter.TrimEnd('\\') + "\\";
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip files we can't remove (in use, protected system folders like $RECYCLE.BIN) —
                // not fatal, just leaves a little less free space for the test to use.
            }
        }

        Directory.CreateDirectory(testFolder);
    }

    private static Task<CapacityVerificationResult> RunCapacityTestAsync(
        string testFolder, ulong targetBytes, ulong claimedCapacityBytes, int blockSize,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var seed = (ulong)DateTime.UtcNow.Ticks;
        var fileCount = (ulong)(targetBytes / (ulong)blockSize);
        var totalWork = fileCount * (ulong)blockSize * 2;

        var buffer = new byte[blockSize];
        var writtenPaths = new List<string>((int)Math.Min(fileCount, int.MaxValue));
        ulong bytesWritten = 0;
        var writeStopwatch = Stopwatch.StartNew();

        for (ulong i = 0; i < fileCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapacityVerifier.FillDeterministicBlock(buffer, seed, i);
            var path = Path.Combine(testFolder, $"block_{i:D8}.bin");

            try
            {
                File.WriteAllBytes(path, buffer);
            }
            catch (IOException)
            {
                // Drive reported more free space than it actually has — that's itself a fraud
                // signal, and exactly what F3-style tools rely on. Stop and verify what we got.
                break;
            }

            writtenPaths.Add(path);
            bytesWritten += (ulong)blockSize;

            progress?.Report(new TestProgress
            {
                Phase = TestPhase.WritingCapacityPattern,
                BytesProcessed = bytesWritten,
                TotalBytes = totalWork,
            });
        }

        var writeElapsedSeconds = writeStopwatch.Elapsed.TotalSeconds;
        var writeMegabytesPerSecond = writeElapsedSeconds > 0
            ? writtenPaths.Count * blockSize / 1_000_000.0 / writeElapsedSeconds
            : 0;

        var expected = new byte[blockSize];
        var blocksFailed = 0;
        ulong verifiedGoodBytes = 0;
        var sawFailure = false;
        var workDone = bytesWritten;
        var readStopwatch = Stopwatch.StartNew();

        for (var i = 0; i < writtenPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapacityVerifier.FillDeterministicBlock(expected, seed, (ulong)i);

            byte[] actual;
            try
            {
                actual = File.ReadAllBytes(writtenPaths[i]);
            }
            catch (IOException)
            {
                actual = Array.Empty<byte>();
            }

            if (actual.Length == blockSize && actual.AsSpan().SequenceEqual(expected))
            {
                if (!sawFailure) verifiedGoodBytes = (ulong)(i + 1) * (ulong)blockSize;
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
                BlocksFailedSoFar = blocksFailed,
            });
        }

        var readElapsedSeconds = readStopwatch.Elapsed.TotalSeconds;
        var readMegabytesPerSecond = readElapsedSeconds > 0
            ? writtenPaths.Count * blockSize / 1_000_000.0 / readElapsedSeconds
            : 0;

        return new CapacityVerificationResult
        {
            VerifiedGoodBytes = verifiedGoodBytes,
            TotalBytesTested = claimedCapacityBytes,
            BlocksTested = writtenPaths.Count,
            BlocksFailed = blocksFailed,
            WriteMegabytesPerSecond = writeMegabytesPerSecond,
            ReadMegabytesPerSecond = readMegabytesPerSecond,
        };
    }, cancellationToken);

    private static Task<SpeedTestResult> MeasureWriteSpeedAsync(
        string folder, string prefix, int blockSize, TimeSpan duration, TestPhase phase,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var buffer = new byte[blockSize];
        CapacityVerifier.FillDeterministicBlock(buffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        return RunTimedFileLoop(duration, phase, progress, cancellationToken, fileIndex =>
        {
            var path = Path.Combine(folder, $"{prefix}_speedtest_{fileIndex % RotatingSpeedTestFileCount}.bin");
            File.WriteAllBytes(path, buffer);
        }, blockSize);
    }, cancellationToken);

    private static Task<SpeedTestResult> MeasureReadSpeedAsync(
        string folder, string prefix, int blockSize, TimeSpan duration, TestPhase phase,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var buffer = new byte[blockSize];
        CapacityVerifier.FillDeterministicBlock(buffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        // Read needs the rotating files to already exist — write them once, untimed, first.
        for (var i = 0; i < RotatingSpeedTestFileCount; i++)
        {
            var path = Path.Combine(folder, $"{prefix}_speedtest_{i}.bin");
            if (!File.Exists(path)) File.WriteAllBytes(path, buffer);
        }

        return RunTimedFileLoop(duration, phase, progress, cancellationToken, fileIndex =>
        {
            var path = Path.Combine(folder, $"{prefix}_speedtest_{fileIndex % RotatingSpeedTestFileCount}.bin");
            _ = File.ReadAllBytes(path);
        }, blockSize);
    }, cancellationToken);

    private static SpeedTestResult RunTimedFileLoop(
        TimeSpan duration, TestPhase phase, IProgress<TestProgress>? progress,
        CancellationToken cancellationToken, Action<int> performFileIo, int blockSize)
    {
        const int SampleWindowFiles = 3;

        var stopwatch = Stopwatch.StartNew();
        var windowStopwatch = Stopwatch.StartNew();
        var samples = new List<double>();
        var filesInWindow = 0;
        var fileIndex = 0;
        double peak = 0;

        while (stopwatch.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();

            performFileIo(fileIndex);
            fileIndex++;
            filesInWindow++;

            if (filesInWindow >= SampleWindowFiles)
            {
                var windowSeconds = windowStopwatch.Elapsed.TotalSeconds;
                var windowMegabytes = filesInWindow * blockSize / 1_000_000.0;
                var mbPerSec = windowSeconds > 0 ? windowMegabytes / windowSeconds : 0;
                samples.Add(mbPerSec);
                peak = Math.Max(peak, mbPerSec);

                var elapsedFraction = Math.Clamp(stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds, 0, 1);
                progress?.Report(new TestProgress
                {
                    Phase = phase,
                    BytesProcessed = (ulong)(elapsedFraction * 1000),
                    TotalBytes = 1000,
                    CurrentThroughputMegabytesPerSecond = mbPerSec,
                });

                filesInWindow = 0;
                windowStopwatch.Restart();
            }
        }

        return new SpeedTestResult
        {
            AverageMegabytesPerSecond = samples.Count > 0 ? samples.Average() : 0,
            PeakBurstMegabytesPerSecond = peak,
            SampledMegabytesPerSecond = samples,
        };
    }
}
