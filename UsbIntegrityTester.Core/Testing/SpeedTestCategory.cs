namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// A workload to simulate for a speed test slot — either a practical real-world file-size range
/// (randomized sizes across that range) or one of CrystalDiskMark's fixed named benchmarks
/// (SEQ1M/RND4K at a specific queue depth), kept as exact replicas so results from this app can be
/// compared against a CrystalDiskMark run on the same drive.
/// </summary>
public enum SpeedTestCategory
{
    Seq1MQ8T1,
    Seq1MQ1T1,
    Rnd4KQ32T1,
    Rnd4KQ1T1,
    VerySmallFiles,
    SmallFiles,
    MediumFiles,
    LargeFiles,
    VeryLargeFiles,
    HugeFiles,
}

public sealed record SpeedTestCategoryInfo(
    string DisplayName, string WorkloadDescription, long MinFileSizeBytes, long MaxFileSizeBytes,
    int IoBlockSizeBytes = 4 * 1024 * 1024, int IoQueueDepth = 1, int? IoPassCount = null, bool IsRandomAccess = false)
{
    /// <summary>Same value for min and max means every file in this category is that exact size (used by the CrystalDiskMark-replica categories, which all exercise one fixed-size region).</summary>
    public bool IsFixedSize => MinFileSizeBytes == MaxFileSizeBytes;
}

public static class SpeedTestCategoryCatalog
{
    /// <summary>Every CrystalDiskMark-replica category shares this test region size — CDM applies one "Test Size" across all of its sub-benchmarks, and the queued I/O engine's pass-count model (see <see cref="SpeedTestCategoryInfo.IoPassCount"/>) needs a single fixed region to run passes over.</summary>
    private const long CrystalDiskMarkRegionBytes = 1_073_741_824; // 1 GiB

    public static SpeedTestCategoryInfo Get(SpeedTestCategory category) => category switch
    {
        // The four CrystalDiskMark replicas below share one region size, pass count, and (for File
        // Mode) the queued I/O engine — they differ only in block size, queue depth, and sequential
        // vs. random access, exactly mirroring CDM's own SEQ1M/RND4K Q.../T1 test names. Raw Mode
        // still runs all four at queue depth 1, sequential — see QueuedUnbufferedIo's remarks.
        //
        // Pass-count (not a fixed duration) matters here: a drive with a sizeable write cache
        // measures very differently depending on how much of a run lands before vs. after the cache
        // fills up, and a timer can land at an arbitrary point in that cache-fill/cache-drain cycle
        // instead of averaging across full passes the way CrystalDiskMark does.
        SpeedTestCategory.Seq1MQ8T1 => new("SEQ1M Q8T1", "sequential access, 1 MiB blocks, queue depth 8",
            CrystalDiskMarkRegionBytes, CrystalDiskMarkRegionBytes, IoBlockSizeBytes: 1_048_576, IoQueueDepth: 8, IoPassCount: 3),
        SpeedTestCategory.Seq1MQ1T1 => new("SEQ1M Q1T1", "sequential access, 1 MiB blocks, queue depth 1",
            CrystalDiskMarkRegionBytes, CrystalDiskMarkRegionBytes, IoBlockSizeBytes: 1_048_576, IoQueueDepth: 1, IoPassCount: 3),
        SpeedTestCategory.Rnd4KQ32T1 => new("RND4K Q32T1", "random access, 4 KiB blocks, queue depth 32",
            CrystalDiskMarkRegionBytes, CrystalDiskMarkRegionBytes, IoBlockSizeBytes: 4096, IoQueueDepth: 32, IoPassCount: 3, IsRandomAccess: true),
        SpeedTestCategory.Rnd4KQ1T1 => new("RND4K Q1T1", "random access, 4 KiB blocks, queue depth 1",
            CrystalDiskMarkRegionBytes, CrystalDiskMarkRegionBytes, IoBlockSizeBytes: 4096, IoQueueDepth: 1, IoPassCount: 3, IsRandomAccess: true),

        SpeedTestCategory.VerySmallFiles => new("Very Small Files", "Text files, configs, thumbnails, small documents", 100_000, 5_000_000),
        SpeedTestCategory.SmallFiles => new("Small Files", "Photos, PDFs, Office documents", 5_000_000, 25_000_000),
        SpeedTestCategory.MediumFiles => new("Medium Files", "Large photos, PDFs, installers, compressed files", 25_000_000, 100_000_000),
        SpeedTestCategory.LargeFiles => new("Large Files", "HD video, software, archives", 100_000_000, 500_000_000),
        SpeedTestCategory.VeryLargeFiles => new("Very Large Files", "4K video, large archives", 500_000_000, 1_000_000_000),
        SpeedTestCategory.HugeFiles => new("Huge Files", "Video, backups, disk images", 1_000_000_000, 3_000_000_000),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static IReadOnlyList<SpeedTestCategory> All { get; } = Enum.GetValues<SpeedTestCategory>();
}
