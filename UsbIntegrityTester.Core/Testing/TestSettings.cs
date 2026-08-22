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

/// <summary>One of the speed test's 4 configurable slots — whether it runs, and which category it simulates (a real-world file-size range, or one of the CrystalDiskMark-replica benchmarks).</summary>
public sealed record SpeedTestSlotSettings(bool Enabled, SpeedTestCategory Category);

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

    // Default to CrystalDiskMark's own 4-test suite (in its usual top-to-bottom order) so a run of
    // this app's speed test is directly comparable to a CrystalDiskMark run on the same drive.
    public SpeedTestSlotSettings SpeedTestSlot1 { get; init; } = new(true, SpeedTestCategory.Seq1MQ8T1);
    public SpeedTestSlotSettings SpeedTestSlot2 { get; init; } = new(true, SpeedTestCategory.Seq1MQ1T1);
    public SpeedTestSlotSettings SpeedTestSlot3 { get; init; } = new(true, SpeedTestCategory.Rnd4KQ32T1);
    public SpeedTestSlotSettings SpeedTestSlot4 { get; init; } = new(true, SpeedTestCategory.Rnd4KQ1T1);

    public CapacityScanDepth CapacityScanDepth { get; init; } = CapacityScanDepth.Quick;
    public int BlockSizeBytes { get; init; } = 1024 * 1024; // 1 MiB — capacity test's block granularity
    public int QuickScanSampleCount { get; init; } = 256;
    public double StandardScanSampleFraction { get; init; } = 0.05;
    public TimeSpan SustainedSpeedTestDuration { get; init; } = TimeSpan.FromSeconds(20);
}
