namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// Recursively walks a folder to build the file list and file-type breakdown for the Real-World
/// File Copy test — both for the up-front space/preflight check and, re-run fresh right before the
/// actual copy, to pick up whatever's actually there at that moment.
/// </summary>
public static class RealFileCopyScanner
{
    public static RealFileCopyScanResult Scan(string sourceFolder, CancellationToken cancellationToken)
    {
        var files = new List<RealFileEntry>();
        var skipped = new List<string>();
        var byExtension = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        // A manual stack-based walk, not Directory.EnumerateFiles(..., AllDirectories) — that throws
        // and aborts the *entire* enumeration the moment it hits one access-denied subfolder partway
        // through a large tree. Walking one directory at a time means one bad folder just gets
        // skipped and noted, instead of losing everything found before it.
        var pending = new Stack<string>();
        pending.Push(sourceFolder);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            string[] subDirs;
            string[] filesHere;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                filesHere = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(dir);
                continue;
            }

            foreach (var subDir in subDirs) pending.Push(subDir);

            foreach (var filePath in filesHere)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long sizeBytes;
                try
                {
                    sizeBytes = new FileInfo(filePath).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(filePath);
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceFolder, filePath);
                files.Add(new RealFileEntry(filePath, relativePath, sizeBytes));

                var extension = Path.GetExtension(filePath);
                var key = string.IsNullOrEmpty(extension) ? "(no extension)" : extension.TrimStart('.').ToLowerInvariant();
                byExtension.TryGetValue(key, out var existing);
                byExtension[key] = (existing.Count + 1, existing.Bytes + sizeBytes);
            }
        }

        var breakdown = byExtension
            .Select(kv => new ExtensionBreakdownEntry(kv.Key, kv.Value.Count, kv.Value.Bytes))
            .OrderByDescending(e => e.TotalBytes)
            .ToList();

        return new RealFileCopyScanResult
        {
            SourceFolder = sourceFolder,
            Files = files,
            TotalBytes = files.Sum(f => f.SizeBytes),
            ExtensionBreakdown = breakdown,
            SkippedPaths = skipped,
        };
    }
}
