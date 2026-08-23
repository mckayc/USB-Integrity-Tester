using System.Diagnostics;
using System.Security.Cryptography;

namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// The "Real-World File Copy" test: copies the user's own chosen folder to the drive, then copies
/// it back to a folder on the computer, hashing every file both times and comparing — a genuine
/// data-integrity check using real, high-entropy user data, alongside real elapsed-time numbers for
/// "how long would copying this actually take," instead of a synthetic MB/s figure. Deliberately
/// uses plain buffered <see cref="FileStream"/> I/O (whatever the OS/drive's own caching is already
/// configured to do) rather than the unbuffered FILE_FLAG_NO_BUFFERING approach used by the
/// synthetic speed tests elsewhere in this app — the whole point here is to measure what a real
/// file copy feels like to this user, cache and all, not the raw hardware.
/// </summary>
public sealed class RealFileCopyEngine
{
    private const int CopyChunkBytes = 1024 * 1024;

    public static string GetUsbCopyFolder(string volumeLetter) =>
        Path.Combine(FileModeEngine.GetTestFolderPath(volumeLetter), "RealFiles");

    public static string BuildComputerCopyFolder(string destinationParentFolder) =>
        Path.Combine(destinationParentFolder, $"UsbIntegrityTest-RoundTrip-{DateTime.Now:yyyyMMdd-HHmmss}");

    /// <param name="sourceFolder">The user's chosen folder to copy from — re-scanned fresh here rather than trusting a scan taken earlier when the folder was first picked, in case its contents changed since.</param>
    public async Task<RealFileCopyResult> RunAsync(
        string sourceFolder, string usbVolumeLetter, string destinationParentFolder,
        IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var scan = await Task.Run(() => RealFileCopyScanner.Scan(sourceFolder, cancellationToken), cancellationToken);

        var usbCopyFolder = GetUsbCopyFolder(usbVolumeLetter);
        var computerCopyFolder = BuildComputerCopyFolder(destinationParentFolder);
        Directory.CreateDirectory(usbCopyFolder);
        Directory.CreateDirectory(computerCopyFolder);

        var totalBytes = scan.TotalBytes;
        var sourceHashes = new Dictionary<string, byte[]>();

        // Pass 1: source -> USB, hashing the source bytes as they're read (one read pass over the
        // originals covers both the copy and the "what should this look like when it comes back"
        // reference, instead of reading them again later just to hash them).
        var writeStopwatch = Stopwatch.StartNew();
        var writePeak = 0.0;
        var writeSamples = new List<double>();
        long bytesWrittenSoFar = 0;

        for (var fileIndex = 0; fileIndex < scan.Files.Count; fileIndex++)
        {
            var file = scan.Files[fileIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var destPath = Path.Combine(usbCopyFolder, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            var hash = await CopyFileWithHashAsync(file.FullPath, destPath, bytesInChunk =>
            {
                bytesWrittenSoFar += bytesInChunk;
                ReportProgress(progress, TestPhase.CopyingRealFilesToUsb, bytesWrittenSoFar, totalBytes,
                    writeStopwatch.Elapsed, writeSamples, ref writePeak, fileIndex, file.RelativePath);
            }, cancellationToken);

            sourceHashes[file.RelativePath] = hash;
        }

        writeStopwatch.Stop();

        // Pass 2: USB -> computer, hashing the round-tripped bytes as they're read and comparing
        // against pass 1's source hash — this pass *is* the verification, not a separate step, so
        // the round-tripped copy only ever gets read once.
        var readStopwatch = Stopwatch.StartNew();
        var readPeak = 0.0;
        var readSamples = new List<double>();
        long bytesReadSoFar = 0;
        var filesCopiedBack = 0;
        var filesVerified = 0;
        var issues = new List<RealFileCopyIssue>();

        for (var fileIndex = 0; fileIndex < scan.Files.Count; fileIndex++)
        {
            var file = scan.Files[fileIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var usbPath = Path.Combine(usbCopyFolder, file.RelativePath);
            var computerPath = Path.Combine(computerCopyFolder, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(computerPath)!);

            try
            {
                var hash = await CopyFileWithHashAsync(usbPath, computerPath, bytesInChunk =>
                {
                    bytesReadSoFar += bytesInChunk;
                    ReportProgress(progress, TestPhase.CopyingAndVerifyingRealFilesFromUsb, bytesReadSoFar, totalBytes,
                        readStopwatch.Elapsed, readSamples, ref readPeak, fileIndex, file.RelativePath);
                }, cancellationToken);

                filesCopiedBack++;

                if (sourceHashes.TryGetValue(file.RelativePath, out var expectedHash) && hash.AsSpan().SequenceEqual(expectedHash))
                    filesVerified++;
                else
                    issues.Add(new RealFileCopyIssue(file.RelativePath, "Copy read back from the drive does not match the original — possible data corruption or fake-capacity aliasing."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new RealFileCopyIssue(file.RelativePath, $"Failed to copy back: {ex.Message}"));
            }
        }

        readStopwatch.Stop();

        var writeSeconds = writeStopwatch.Elapsed.TotalSeconds;
        var readSeconds = readStopwatch.Elapsed.TotalSeconds;

        return new RealFileCopyResult
        {
            SourceFolder = sourceFolder,
            UsbCopyFolder = usbCopyFolder,
            ComputerCopyFolder = computerCopyFolder,
            TotalBytes = totalBytes,
            ExtensionBreakdown = scan.ExtensionBreakdown,
            WriteToUsbDuration = writeStopwatch.Elapsed,
            WriteAverageMegabytesPerSecond = writeSeconds > 0 ? totalBytes / 1_000_000.0 / writeSeconds : 0,
            WritePeakMegabytesPerSecond = writePeak,
            WriteSamples = writeSamples,
            ReadFromUsbDuration = readStopwatch.Elapsed,
            ReadAverageMegabytesPerSecond = readSeconds > 0 ? totalBytes / 1_000_000.0 / readSeconds : 0,
            ReadPeakMegabytesPerSecond = readPeak,
            ReadSamples = readSamples,
            FilesCopied = filesCopiedBack,
            FilesVerifiedIdentical = filesVerified,
            Issues = issues,
        };
    }

    private static void ReportProgress(
        IProgress<TestProgress>? progress, TestPhase phase, long bytesSoFar, long totalBytes,
        TimeSpan elapsed, List<double> samples, ref double peak, int fileIndex, string fileRelativePath)
    {
        if (progress is null) return;

        var mbPerSec = elapsed.TotalSeconds > 0 ? bytesSoFar / 1_000_000.0 / elapsed.TotalSeconds : 0;
        peak = Math.Max(peak, mbPerSec);
        samples.Add(mbPerSec);

        progress.Report(new TestProgress
        {
            Phase = phase,
            BytesProcessed = (ulong)bytesSoFar,
            TotalBytes = (ulong)Math.Max(totalBytes, 1),
            CurrentThroughputMegabytesPerSecond = mbPerSec,
            CurrentFileIndex = fileIndex,
            CurrentFileRelativePath = fileRelativePath,
        });
    }

    /// <summary>Streams <paramref name="sourcePath"/> to <paramref name="destPath"/> in chunks via
    /// plain buffered I/O, hashing the source bytes as they're read, and reports each chunk's size
    /// via <paramref name="onChunkCopied"/> so the caller can track live throughput.</summary>
    private static async Task<byte[]> CopyFileWithHashAsync(
        string sourcePath, string destPath, Action<int> onChunkCopied, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyChunkBytes, useAsync: true);
        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyChunkBytes, useAsync: true);

        var buffer = new byte[CopyChunkBytes];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, bytesRead);
            await dest.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            onChunkCopied(bytesRead);
        }

        return hasher.GetHashAndReset();
    }
}
