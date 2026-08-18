namespace UsbIntegrityTester.Core.Testing;

public enum ScanMode
{
    /// <summary>Tests sampled blocks spread across the claimed capacity — fast, catches gross fraud.</summary>
    Quick,

    /// <summary>Tests every block across the full claimed capacity — slow, catches everything F3/H2testw would.</summary>
    Full,
}

public sealed record TestSettings
{
    public bool RunCapacityTest { get; init; } = true;
    public bool RunSpeedTest { get; init; } = true;
    public ScanMode ScanMode { get; init; } = ScanMode.Quick;
    public int BlockSizeBytes { get; init; } = 1024 * 1024; // 1 MiB
    public int QuickScanSampleCount { get; init; } = 256;
    public TimeSpan SustainedSpeedTestDuration { get; init; } = TimeSpan.FromSeconds(30);
}
