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

    /// <summary>Physical I/O granularity for the speed test. A file can be anywhere from 100 KB to
    /// 3 GB depending on the category — one call per whole file would take several seconds for the
    /// bigger categories, leaving almost nothing for the trend line to draw. Chunking keeps samples
    /// flowing regardless of file size, while the file on disk is still one real, contiguous file.</summary>
    private const int IoChunkBytes = 4 * 1024 * 1024;

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
    /// Called between top-level tests (Capacity, then each enabled speed test slot — never between
    /// a test's own write and read passes) so a caller can pause the run there, e.g. to let a person
    /// manually advance one test at a time while recording. Never called before the first test that
    /// actually runs. Pass null to run straight through with no pauses.
    /// </param>
    public async Task<TestResult> RunAsync(
        string volumeLetter, ulong claimedCapacityBytes, TestSettings settings,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? waitForNextTest = null)
    {
        var driveInfo = new System.IO.DriveInfo(volumeLetter);
        var testFolder = GetTestFolderPath(volumeLetter);

        // Deleting everything on the drive — potentially tens of thousands of leftover files from
        // a previous Full capacity scan — can take minutes. This method is called directly from the
        // UI thread's command handler, and by default every `await` in it resumes back on that same
        // thread, so without Task.Run this blocks the whole window with no way to tell it's still
        // working rather than hung. Same reasoning applies everywhere else in this method that
        // touches the filesystem synchronously.
        await Task.Run(() =>
        {
            if (settings.ClearExistingData)
                ClearDrive(volumeLetter, testFolder);
            else
                Directory.CreateDirectory(testFolder);
        }, cancellationToken);

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

        SpeedTestSlotResult? slot1Result = null, slot2Result = null, slot3Result = null;
        if (settings.RunSpeedTest)
        {
            // Clear whatever the previous test left behind (capacity test verification blocks, or
            // an earlier slot's rotating files) before each enabled slot — not just at the very end
            // of the whole run. A Full capacity scan can leave the drive nearly full; without this,
            // the very next test can fail with a disk-full error before it even starts. Only ever
            // one test's worth of files exists on disk at a time this way; the *last* test that
            // runs is still left alone here, governed by CleanupPolicy at the end as before.
            if (settings.SpeedTestSlot1.Enabled)
            {
                await Task.Run(() => ClearTestFolderContents(testFolder), cancellationToken);
                slot1Result = await RunSlotAsync(testFolder, volumeLetter, "slot1", settings.SpeedTestSlot1,
                    TestPhase.MeasuringSpeedTestSlot1WriteSpeed, TestPhase.MeasuringSpeedTestSlot1ReadSpeed,
                    settings.SustainedSpeedTestDuration, GateAsync, progress, cancellationToken);
            }

            if (settings.SpeedTestSlot2.Enabled)
            {
                await Task.Run(() => ClearTestFolderContents(testFolder), cancellationToken);
                slot2Result = await RunSlotAsync(testFolder, volumeLetter, "slot2", settings.SpeedTestSlot2,
                    TestPhase.MeasuringSpeedTestSlot2WriteSpeed, TestPhase.MeasuringSpeedTestSlot2ReadSpeed,
                    settings.SustainedSpeedTestDuration, GateAsync, progress, cancellationToken);
            }

            if (settings.SpeedTestSlot3.Enabled)
            {
                await Task.Run(() => ClearTestFolderContents(testFolder), cancellationToken);
                slot3Result = await RunSlotAsync(testFolder, volumeLetter, "slot3", settings.SpeedTestSlot3,
                    TestPhase.MeasuringSpeedTestSlot3WriteSpeed, TestPhase.MeasuringSpeedTestSlot3ReadSpeed,
                    settings.SustainedSpeedTestDuration, GateAsync, progress, cancellationToken);
            }
        }

        if (settings.CleanupPolicy == TestCleanupPolicy.DeleteAfterTest)
            await Task.Run(() => TryDeleteTestFolder(volumeLetter), CancellationToken.None);

        progress?.Report(new TestProgress { Phase = TestPhase.Complete, BytesProcessed = 1, TotalBytes = 1 });

        return new TestResult
        {
            Capacity = capacityResult,
            Slot1 = slot1Result,
            Slot2 = slot2Result,
            Slot3 = slot3Result,
        };
    }

    private static async Task<SpeedTestSlotResult?> RunSlotAsync(
        string testFolder, string volumeLetter, string filePrefix, SpeedTestSlotSettings slot,
        TestPhase writePhase, TestPhase readPhase, TimeSpan duration, Func<Task> gateAsync,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        if (!slot.Enabled) return null;

        var info = SpeedTestCategoryCatalog.Get(slot.Category);

        // Query the volume's real, current free space rather than trusting a number computed once
        // at the very start of the run — that number (95% of total drive size) has no relationship
        // to what's actually free after a capacity test and however many earlier slots have run, and
        // trusting it is exactly what caused this slot to try writing into a drive that turned out
        // to still be full. A 10% margin covers filesystem overhead and whatever couldn't be cleared.
        var availableBytes = (ulong)(new System.IO.DriveInfo(volumeLetter).AvailableFreeSpace * 0.9);

        // A tiny (or tiny-because-fake, or still-full) drive might not have room for a handful of
        // even the category's smallest file — skip rather than fail this slot outright.
        if (availableBytes < (ulong)info.MinFileSizeBytes * RotatingSpeedTestFileCount) return null;
        var effectiveMaxBytes = Math.Min(info.MaxFileSizeBytes, (long)availableBytes / RotatingSpeedTestFileCount);
        var effectiveMinBytes = Math.Min(info.MinFileSizeBytes, effectiveMaxBytes);

        await gateAsync();

        var writeResult = await MeasureWriteSpeedAsync(
            testFolder, filePrefix, effectiveMinBytes, effectiveMaxBytes, duration, writePhase, progress, cancellationToken);
        var readResult = await MeasureReadSpeedAsync(
            testFolder, filePrefix, effectiveMinBytes, effectiveMaxBytes, duration, readPhase, progress, cancellationToken);

        return new SpeedTestSlotResult(slot.Category, writeResult, readResult);
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

    /// <summary>Deletes everything inside the test folder (but recreates the folder itself) — used
    /// between tests within a single run to free the space a previous test's files were using,
    /// independent of the user's end-of-run CleanupPolicy. A Full capacity scan can leave tens of
    /// thousands of tiny verification-block files behind; deleting them one at a time while also
    /// enumerating the same directory is exactly the kind of thing that silently under-deletes on
    /// some filesystems, so this removes the whole directory tree in one call instead — the same
    /// proven approach <see cref="TryDeleteTestFolder"/> already uses.</summary>
    private static void ClearTestFolderContents(string testFolder)
    {
        try
        {
            if (Directory.Exists(testFolder)) Directory.Delete(testFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort, same tolerance as ClearDrive/TryDeleteTestFolder — the fresh free-space
            // check in RunSlotAsync is what actually protects the next test, not this succeeding.
        }
        finally
        {
            Directory.CreateDirectory(testFolder);
        }
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

    /// <summary>Writes a rotating set of files with randomized sizes in [minFileSizeBytes,
    /// maxFileSizeBytes], each in <see cref="IoChunkBytes"/>-sized physical writes so even a
    /// multi-gigabyte file still produces plenty of throughput samples.</summary>
    private static Task<SpeedTestResult> MeasureWriteSpeedAsync(
        string folder, string prefix, long minFileSizeBytes, long maxFileSizeBytes, TimeSpan duration, TestPhase phase,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var rng = Random.Shared;
        var chunkBuffer = new byte[IoChunkBytes];
        CapacityVerifier.FillDeterministicBlock(chunkBuffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        UnbufferedFile.ChunkedWriter? writer = null;
        long targetFileSize = 0;
        long bytesInCurrentFile = 0;
        var fileIndex = 0;

        try
        {
            return RunTimedByteLoop(duration, phase, progress, cancellationToken, () =>
            {
                if (writer is null || bytesInCurrentFile >= targetFileSize)
                {
                    writer?.Dispose();
                    targetFileSize = FileSizeRandomizer.NextAlignedSize(rng, minFileSizeBytes, maxFileSizeBytes);
                    var path = Path.Combine(folder, $"{prefix}_speedtest_{fileIndex % RotatingSpeedTestFileCount}.bin");
                    writer = new UnbufferedFile.ChunkedWriter(path);
                    bytesInCurrentFile = 0;
                    fileIndex++;
                }

                var thisChunk = (int)Math.Min(IoChunkBytes, targetFileSize - bytesInCurrentFile);
                writer.WriteChunk(chunkBuffer, thisChunk);
                bytesInCurrentFile += thisChunk;
                return thisChunk;
            });
        }
        finally
        {
            writer?.Dispose();
        }
    }, cancellationToken);

    /// <summary>The read counterpart to <see cref="MeasureWriteSpeedAsync"/> — pre-writes a rotating
    /// set of randomly-sized files up front (untimed), then reads them back in chunks.</summary>
    private static Task<SpeedTestResult> MeasureReadSpeedAsync(
        string folder, string prefix, long minFileSizeBytes, long maxFileSizeBytes, TimeSpan duration, TestPhase phase,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var rng = Random.Shared;
        var chunkBuffer = new byte[IoChunkBytes];
        CapacityVerifier.FillDeterministicBlock(chunkBuffer, seed: 0xA5A5A5A5UL, blockOffset: 0);

        var fileSizes = new long[RotatingSpeedTestFileCount];
        for (var i = 0; i < RotatingSpeedTestFileCount; i++)
        {
            var path = Path.Combine(folder, $"{prefix}_speedtest_{i}.bin");
            var size = FileSizeRandomizer.NextAlignedSize(rng, minFileSizeBytes, maxFileSizeBytes);
            fileSizes[i] = size;

            using var seedWriter = new UnbufferedFile.ChunkedWriter(path);
            for (long written = 0; written < size; written += IoChunkBytes)
                seedWriter.WriteChunk(chunkBuffer, (int)Math.Min(IoChunkBytes, size - written));
        }

        UnbufferedFile.ChunkedReader? reader = null;
        long currentFileSize = 0;
        long bytesInCurrentFile = 0;
        var fileIndex = 0;

        try
        {
            return RunTimedByteLoop(duration, phase, progress, cancellationToken, () =>
            {
                if (reader is null || bytesInCurrentFile >= currentFileSize)
                {
                    reader?.Dispose();
                    var slot = fileIndex % RotatingSpeedTestFileCount;
                    var path = Path.Combine(folder, $"{prefix}_speedtest_{slot}.bin");
                    reader = new UnbufferedFile.ChunkedReader(path);
                    currentFileSize = fileSizes[slot];
                    bytesInCurrentFile = 0;
                    fileIndex++;
                }

                var thisChunk = (int)Math.Min(IoChunkBytes, currentFileSize - bytesInCurrentFile);
                reader.ReadChunk(chunkBuffer, thisChunk);
                bytesInCurrentFile += thisChunk;
                return thisChunk;
            });
        }
        finally
        {
            reader?.Dispose();
        }
    }, cancellationToken);

    /// <summary><paramref name="performIoChunk"/> does one physical chunk of I/O and returns how
    /// many bytes it moved; throughput is sampled by elapsed time, not a fixed chunk count, so it
    /// stays accurate regardless of how big each chunk or simulated file is.</summary>
    private static SpeedTestResult RunTimedByteLoop(
        TimeSpan duration, TestPhase phase, IProgress<TestProgress>? progress,
        CancellationToken cancellationToken, Func<int> performIoChunk)
    {
        var sampleWindowInterval = TimeSpan.FromMilliseconds(250);

        var stopwatch = Stopwatch.StartNew();
        var windowStopwatch = Stopwatch.StartNew();
        var samples = new List<double>();
        long bytesInWindow = 0;
        double peak = 0;

        while (stopwatch.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesInWindow += performIoChunk();

            if (windowStopwatch.Elapsed >= sampleWindowInterval)
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
                    TotalBytes = 1000,
                    CurrentThroughputMegabytesPerSecond = mbPerSec,
                });

                bytesInWindow = 0;
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
