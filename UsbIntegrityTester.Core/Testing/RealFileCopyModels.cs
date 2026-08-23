namespace UsbIntegrityTester.Core.Testing;

/// <summary>One file found under the scanned source folder.</summary>
public sealed record RealFileEntry(string FullPath, string RelativePath, long SizeBytes);

/// <summary>File-type breakdown for the report — "134 mp3 files, 450 MB" style summary.</summary>
public sealed record ExtensionBreakdownEntry(string Extension, int FileCount, long TotalBytes);

/// <summary>
/// Result of recursively walking the user's chosen source folder — used both to preflight
/// available-space checks before the test starts and, re-scanned fresh, to drive the actual copy.
/// </summary>
public sealed record RealFileCopyScanResult
{
    public required string SourceFolder { get; init; }
    public required IReadOnlyList<RealFileEntry> Files { get; init; }
    public required long TotalBytes { get; init; }
    public required IReadOnlyList<ExtensionBreakdownEntry> ExtensionBreakdown { get; init; }

    /// <summary>Files/folders skipped during the walk (in use, access denied, etc.) — not fatal, just noted.</summary>
    public required IReadOnlyList<string> SkippedPaths { get; init; }

    public int FileCount => Files.Count;
}

/// <summary>One file that failed to copy or came back from the drive not matching the original.</summary>
public sealed record RealFileCopyIssue(string RelativePath, string Message);

public sealed record RealFileCopyResult
{
    public required string SourceFolder { get; init; }
    public required string UsbCopyFolder { get; init; }
    public required string ComputerCopyFolder { get; init; }

    public required long TotalBytes { get; init; }
    public required IReadOnlyList<ExtensionBreakdownEntry> ExtensionBreakdown { get; init; }

    public required TimeSpan WriteToUsbDuration { get; init; }
    public required double WriteAverageMegabytesPerSecond { get; init; }
    public required double WritePeakMegabytesPerSecond { get; init; }
    public required IReadOnlyList<double> WriteSamples { get; init; }

    public required TimeSpan ReadFromUsbDuration { get; init; }
    public required double ReadAverageMegabytesPerSecond { get; init; }
    public required double ReadPeakMegabytesPerSecond { get; init; }
    public required IReadOnlyList<double> ReadSamples { get; init; }

    public required int FilesCopied { get; init; }
    public required int FilesVerifiedIdentical { get; init; }
    public required IReadOnlyList<RealFileCopyIssue> Issues { get; init; }

    public bool AllVerified => Issues.Count == 0 && FilesCopied == FilesVerifiedIdentical;
}
