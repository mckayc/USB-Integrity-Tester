namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// A practical, real-world file-size workload to simulate for a speed test slot — replaces the old
/// fixed "large/small/huge" split with named categories matching what people actually move onto a
/// USB drive, each tested with randomized file sizes across its range rather than one repeated size.
/// </summary>
public enum SpeedTestCategory
{
    StandardBenchmark,
    VerySmallFiles,
    SmallFiles,
    MediumFiles,
    LargeFiles,
    VeryLargeFiles,
    HugeFiles,
}

public sealed record SpeedTestCategoryInfo(string DisplayName, string WorkloadDescription, long MinFileSizeBytes, long MaxFileSizeBytes)
{
    /// <summary>Same value for min and max means every file in this category is that exact size (used only by Standard Benchmark).</summary>
    public bool IsFixedSize => MinFileSizeBytes == MaxFileSizeBytes;
}

public static class SpeedTestCategoryCatalog
{
    public static SpeedTestCategoryInfo Get(SpeedTestCategory category) => category switch
    {
        SpeedTestCategory.StandardBenchmark => new("Standard Benchmark", "Synthetic sequential test file", 1_000_000_000, 1_000_000_000),
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
