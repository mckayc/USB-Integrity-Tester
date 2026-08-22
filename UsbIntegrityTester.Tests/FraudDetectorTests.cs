using UsbIntegrityTester.Core.Testing;
using UsbIntegrityTester.Core.Verdict;

namespace UsbIntegrityTester.Tests;

public class FraudDetectorTests
{
    private const ulong OneGb = 1_000_000_000;

    [Fact]
    public void Evaluate_FlagsCounterfeit_WhenVerifiedCapacityIsMuchLessThanClaimed()
    {
        var result = new TestResult
        {
            Capacity = new CapacityVerificationResult
            {
                VerifiedGoodBytes = OneGb * 8, // only 8 GB verified
                TotalBytesTested = OneGb * 64,
                BlocksTested = 100,
                BlocksFailed = 20,
            },
        };

        var verdict = FraudDetector.Evaluate(
            OneGb * 64, claimedReadSpeedMegabytesPerSecond: null, claimedWriteSpeedMegabytesPerSecond: null, result);

        Assert.Equal(DriveVerdict.Counterfeit, verdict.Verdict);
    }

    [Fact]
    public void Evaluate_FlagsUnderperforming_WhenCapacityOkButReadSpeedFallsShort()
    {
        var result = new TestResult
        {
            Capacity = new CapacityVerificationResult
            {
                VerifiedGoodBytes = OneGb * 64,
                TotalBytesTested = OneGb * 64,
                BlocksTested = 100,
                BlocksFailed = 0,
            },
            Slot1 = new SpeedTestSlotResult(
                SpeedTestCategory.Seq1MQ8T1,
                Write: new SpeedTestResult { AverageMegabytesPerSecond = 0, PeakBurstMegabytesPerSecond = 0, SampledMegabytesPerSecond = Array.Empty<double>() },
                Read: new SpeedTestResult
                {
                    AverageMegabytesPerSecond = 20,
                    PeakBurstMegabytesPerSecond = 40,
                    SampledMegabytesPerSecond = new List<double> { 20 },
                }),
        };

        var verdict = FraudDetector.Evaluate(
            OneGb * 64, claimedReadSpeedMegabytesPerSecond: 100, claimedWriteSpeedMegabytesPerSecond: null, result);

        Assert.Equal(DriveVerdict.Underperforming, verdict.Verdict);
    }

    [Fact]
    public void Evaluate_FlagsUnderperforming_WhenWriteSpeedFallsShortEvenIfReadIsFine()
    {
        var result = new TestResult
        {
            Capacity = new CapacityVerificationResult
            {
                VerifiedGoodBytes = OneGb * 64,
                TotalBytesTested = OneGb * 64,
                BlocksTested = 100,
                BlocksFailed = 0,
            },
            Slot1 = new SpeedTestSlotResult(
                SpeedTestCategory.Seq1MQ8T1,
                Write: new SpeedTestResult
                {
                    AverageMegabytesPerSecond = 5,
                    PeakBurstMegabytesPerSecond = 8,
                    SampledMegabytesPerSecond = new List<double> { 5 },
                },
                Read: new SpeedTestResult
                {
                    AverageMegabytesPerSecond = 95,
                    PeakBurstMegabytesPerSecond = 110,
                    SampledMegabytesPerSecond = new List<double> { 95 },
                }),
        };

        var verdict = FraudDetector.Evaluate(
            OneGb * 64, claimedReadSpeedMegabytesPerSecond: 100, claimedWriteSpeedMegabytesPerSecond: 30, result);

        Assert.Equal(DriveVerdict.Underperforming, verdict.Verdict);
    }

    [Fact]
    public void Evaluate_ReturnsOk_WhenCapacityAndBothSpeedsMatchClaims()
    {
        var result = new TestResult
        {
            Capacity = new CapacityVerificationResult
            {
                VerifiedGoodBytes = OneGb * 64,
                TotalBytesTested = OneGb * 64,
                BlocksTested = 100,
                BlocksFailed = 0,
            },
            Slot1 = new SpeedTestSlotResult(
                SpeedTestCategory.Seq1MQ8T1,
                Write: new SpeedTestResult
                {
                    AverageMegabytesPerSecond = 28,
                    PeakBurstMegabytesPerSecond = 35,
                    SampledMegabytesPerSecond = new List<double> { 28 },
                },
                Read: new SpeedTestResult
                {
                    AverageMegabytesPerSecond = 95,
                    PeakBurstMegabytesPerSecond = 110,
                    SampledMegabytesPerSecond = new List<double> { 95 },
                }),
        };

        var verdict = FraudDetector.Evaluate(
            OneGb * 64, claimedReadSpeedMegabytesPerSecond: 100, claimedWriteSpeedMegabytesPerSecond: 30, result);

        Assert.Equal(DriveVerdict.Ok, verdict.Verdict);
    }
}
