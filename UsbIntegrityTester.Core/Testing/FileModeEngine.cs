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

    /// <summary>Physical I/O granularity for the huge-file workload. Large/Small write or read
    /// their whole file in one call, which is fine when that call takes milliseconds — but a 100 MB
    /// huge file can take several seconds at USB speed, so doing it in one call means only a single
    /// throughput sample for the whole file, and a trend line with almost nothing to draw. Chunking
    /// it gives ~25 samples per 100 MB file instead of 1, while the file on disk is still one real,
    /// contiguous 100 MB file — only the physical I/O calls are smaller.</summary>
    private const int HugeFileIoChunkBytes = 4 * 1024 * 1024;

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

    /// <param name="waitForNextTest">
    /// Called between top-level tests (Capacity, Large, Small, Huge — never between a test's own
    /// write and read passes) so a caller can pause the run there, e.g. to let a person manually
    /// advance one test at a time while recording. Never called before the first test that
    /// actually runs. Pass null to run straight through with no pauses.
    /// </param>
    public async Task<TestResult> RunAsync(
        string volumeLetter, ulong claimedCapacityBytes, TestSettings settings,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? waitForNextTest = null)
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

        var hasRunAnyTest = false;
        async Task GateAsync()
        {
            if (hasRunAnyTest && waitForNextTest is not null) await waitForNextTest(cancellationToken);
            hasRunAnyTest = true;
        }

        CapacityVerificationResult? capacityResult = null;
        if (settings.RunCapacityTest)
        {
            await GateAsync();

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
                await GateAsync();
                writeResult = await MeasureWriteSpeedAsync(testFolder, "large", settings.BlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringLargeFileWriteSpeed, progress, cancellationToken);
                readResult = await MeasureReadSpeedAsync(testFolder, "large", settings.BlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringLargeFileReadSpeed, progress, cancellationToken);
            }

            if (settings.RunSmallFileSpeedTest)
            {
                await GateAsync();
                smallFileWriteResult = await MeasureWriteSpeedAsync(testFolder, "small", settings.SmallFileBlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringSmallFileWriteSpeed, progress, cancellationToken);
                smallFileReadResult = await MeasureReadSpeedAsync(testFolder, "small", settings.SmallFileBlockSizeBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringSmallFileReadSpeed, progress, cancellationToken);
            }

            if (settings.RunHugeFileSpeedTest && fullTarget >= (ulong)settings.HugeFileBlockSizeBytes * RotatingSpeedTestFileCount)
            {
                await GateAsync();
                hugeFileWriteResult = await MeasureHugeFileWriteSpeedAsync(testFolder, settings.HugeFileBlockSizeBytes, HugeFileIoChunkBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringHugeFileWriteSpeed, progress, cancellationToken);
                hugeFileReadResult = await MeasureHugeFileReadSpeedAsync(testFolder, settings.HugeFileBlockSizeBytes, HugeFileIoChunkBytes, settings.SustainedSpeedTestDuration, TestPhase.MeasuringHugeFileReadSpeed, progress, cancellationToken);
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
                UnbufferedFile.WriteAllBytes(path, buffer, blockSize);
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
        var actual = new byte[blockSize];
        var blocksFailed = 0;
        ulong verifiedGoodBytes = 0;
        var sawFailure = false;
        var workDone = bytesWritten;
        var readStopwatch = Stopwatch.StartNew();

        for (var i = 0; i < writtenPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapacityVerifier.FillDeterministicBlock(expected, seed, (ulong)i);

            bool readOk;
            try
            {
                UnbufferedFile.ReadAllBytes(writtenPaths[i], actual, blockSize);
                readOk = true;
            }
            catch (IOException)
            {
                readOk = false;
            }

            if (readOk && actual.AsSpan().SequenceEqual(expected))
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
            UnbufferedFile.WriteAllBytes(path, buffer, blockSize);
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
            if (!File.Exists(path)) UnbufferedFile.WriteAllBytes(path, buffer, blockSize);
        }

        // Only 4 files rotate for the whole test — with ordinary buffered I/O, Windows' file
        // cache would hold onto them after this first pass and every subsequent "read" would
        // measure RAM bandwidth, not the drive. UnbufferedFile forces every read to the device.
        return RunTimedFileLoop(duration, phase, progress, cancellationToken, fileIndex =>
        {
            var path = Path.Combine(folder, $"{prefix}_speedtest_{fileIndex % RotatingSpeedTestFileCount}.bin");
            UnbufferedFile.ReadAllBytes(path, buffer, blockSize);
        }, blockSize);
    }, cancellationToken);

    /// <summary>Writes the huge-file workload in <see cref="HugeFileIoChunkBytes"/>-sized chunks
    /// rather than one call per whole 100 MB file, so the trend line actually has something to
    /// draw. Cycles through the same rotating file set as the other workloads.</summary>
    private static Task<SpeedTestResult> MeasureHugeFileWriteSpeedAsync(
        string folder, int fileSizeBytes, int chunkSizeBytes, TimeSpan duration, TestPhase phase,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var chunkBuffer = new byte[chunkSizeBytes];
        CapacityVerifier.FillDeterministicBlock(chunkBuffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        UnbufferedFile.ChunkedWriter? writer = null;
        var bytesInCurrentFile = 0;
        var fileIndex = 0;

        try
        {
            return RunTimedFileLoop(duration, phase, progress, cancellationToken, _ =>
            {
                if (writer is null || bytesInCurrentFile >= fileSizeBytes)
                {
                    writer?.Dispose();
                    var path = Path.Combine(folder, $"huge_speedtest_{fileIndex % RotatingSpeedTestFileCount}.bin");
                    writer = new UnbufferedFile.ChunkedWriter(path);
                    bytesInCurrentFile = 0;
                    fileIndex++;
                }

                writer.WriteChunk(chunkBuffer, chunkSizeBytes);
                bytesInCurrentFile += chunkSizeBytes;
            }, chunkSizeBytes);
        }
        finally
        {
            writer?.Dispose();
        }
    }, cancellationToken);

    /// <summary>The read counterpart to <see cref="MeasureHugeFileWriteSpeedAsync"/>.</summary>
    private static Task<SpeedTestResult> MeasureHugeFileReadSpeedAsync(
        string folder, int fileSizeBytes, int chunkSizeBytes, TimeSpan duration, TestPhase phase,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var seedBuffer = new byte[chunkSizeBytes];
        CapacityVerifier.FillDeterministicBlock(seedBuffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        // Read needs the rotating files to already exist, fully sized — write them once, untimed, first.
        for (var i = 0; i < RotatingSpeedTestFileCount; i++)
        {
            var path = Path.Combine(folder, $"huge_speedtest_{i}.bin");
            if (File.Exists(path) && new FileInfo(path).Length == fileSizeBytes) continue;

            using var seedWriter = new UnbufferedFile.ChunkedWriter(path);
            for (var written = 0; written < fileSizeBytes; written += chunkSizeBytes)
                seedWriter.WriteChunk(seedBuffer, chunkSizeBytes);
        }

        var chunkBuffer = new byte[chunkSizeBytes];
        UnbufferedFile.ChunkedReader? reader = null;
        var bytesInCurrentFile = 0;
        var fileIndex = 0;

        try
        {
            return RunTimedFileLoop(duration, phase, progress, cancellationToken, _ =>
            {
                if (reader is null || bytesInCurrentFile >= fileSizeBytes)
                {
                    reader?.Dispose();
                    var path = Path.Combine(folder, $"huge_speedtest_{fileIndex % RotatingSpeedTestFileCount}.bin");
                    reader = new UnbufferedFile.ChunkedReader(path);
                    bytesInCurrentFile = 0;
                    fileIndex++;
                }

                reader.ReadChunk(chunkBuffer, chunkSizeBytes);
                bytesInCurrentFile += chunkSizeBytes;
            }, chunkSizeBytes);
        }
        finally
        {
            reader?.Dispose();
        }
    }, cancellationToken);

    private static SpeedTestResult RunTimedFileLoop(
        TimeSpan duration, TestPhase phase, IProgress<TestProgress>? progress,
        CancellationToken cancellationToken, Action<int> performFileIo, int blockSize)
    {
        const int SampleWindowFiles = 3;
        var sampleWindowInterval = TimeSpan.FromMilliseconds(250);

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

            if (filesInWindow >= SampleWindowFiles || windowStopwatch.Elapsed >= sampleWindowInterval)
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
