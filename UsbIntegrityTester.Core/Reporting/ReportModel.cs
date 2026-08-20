using UsbIntegrityTester.Core.Devices;
using UsbIntegrityTester.Core.Testing;
using UsbIntegrityTester.Core.Verdict;

namespace UsbIntegrityTester.Core.Reporting;

public sealed record ReportModel
{
    public required DateTime TimestampUtc { get; init; }
    public required string DriveLabel { get; init; }
    public required string SerialNumber { get; init; }
    public required ulong ClaimedCapacityBytes { get; init; }
    public required double? ClaimedReadSpeedMegabytesPerSecond { get; init; }
    public required double? ClaimedWriteSpeedMegabytesPerSecond { get; init; }
    public required UsbLinkSpeed? NegotiatedLinkSpeed { get; init; }
    public required TestResult TestResult { get; init; }
    public required VerdictResult Verdict { get; init; }
    public required double TestDurationSeconds { get; init; }
}
