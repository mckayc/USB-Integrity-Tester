namespace UsbIntegrityTester.Core.Testing;

/// <summary>How much of the claimed capacity gets tested for fake-capacity fraud. Has no effect on the speed test.</summary>
public enum CapacityScanDepth
{
    /// <summary>Samples a fixed number of blocks spread across the claimed capacity — fast, catches gross fraud.</summary>
    Quick,

    /// <summary>Samples roughly 5% of blocks, evenly spread — a middle ground for a longer but not exhaustive check.</summary>
    Standard,

    /// <summary>Tests every block across the full claimed capacity — slow, catches everything F3/H2testw would.</summary>
    Full,
}

/// <summary>How the test physically accesses the drive.</summary>
public enum TestMode
{
    /// <summary>Writes real, individually-named files onto the drive's mounted filesystem — visible live in Explorer.</summary>
    File,

    /// <summary>Writes directly to raw sectors via the physical drive handle. Most thorough, but the volume is
    /// dismounted for the duration and nothing is visible in Explorer.</summary>
    Raw,
}

/// <summary>What happens to test files after a File Mode run.</summary>
public enum TestCleanupPolicy
{
    DeleteAfterTest,
    DeleteOnAppClose,
    KeepUntilManual,
}

public sealed record TestSettings
{
    public TestMode TestMode { get; init; } = TestMode.File;

    /// <summary>File Mode only. If true, existing files on the drive are deleted first so the test can use
    /// nearly the whole claimed capacity. If false, only current free space is used and results only cover
    /// that free space — the caller is responsible for surfacing that caveat to the user.</summary>
    public bool ClearExistingData { get; init; } = true;

    public TestCleanupPolicy CleanupPolicy { get; init; } = TestCleanupPolicy.DeleteAfterTest;

    public bool RunCapacityTest { get; init; } = true;
    public bool RunSpeedTest { get; init; } = true;
    public bool RunLargeFileSpeedTest { get; init; } = true;
    public bool RunSmallFileSpeedTest { get; init; } = true;
    public bool RunHugeFileSpeedTest { get; init; }
    public CapacityScanDepth CapacityScanDepth { get; init; } = CapacityScanDepth.Quick;
    public int BlockSizeBytes { get; init; } = 1024 * 1024; // 1 MiB — simulates copying one large file
    public int SmallFileBlockSizeBytes { get; init; } = 4096; // 4 KiB — simulates copying many small files
    public int HugeFileBlockSizeBytes { get; init; } = 100 * 1024 * 1024; // 100 MiB — simulates copying one huge file
    public int QuickScanSampleCount { get; init; } = 256;
    public double StandardScanSampleFraction { get; init; } = 0.05;
    public TimeSpan SustainedSpeedTestDuration { get; init; } = TimeSpan.FromSeconds(20);
}
