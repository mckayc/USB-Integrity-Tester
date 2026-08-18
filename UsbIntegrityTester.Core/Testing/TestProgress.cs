namespace UsbIntegrityTester.Core.Testing;

public enum TestPhase
{
    WritingCapacityPattern,
    VerifyingCapacityPattern,
    MeasuringWriteSpeed,
    MeasuringReadSpeed,
    Complete,
}

public sealed record TestProgress
{
    public required TestPhase Phase { get; init; }
    public required ulong BytesProcessed { get; init; }
    public required ulong TotalBytes { get; init; }
    public double? CurrentThroughputMegabytesPerSecond { get; init; }
    public double FractionComplete => TotalBytes == 0 ? 0 : (double)BytesProcessed / TotalBytes;
}
