using UsbIntegrityTester.Core.Devices;
using UsbIntegrityTester.Core.History;
using UsbIntegrityTester.Core.Reporting;
using UsbIntegrityTester.Core.Testing;
using UsbIntegrityTester.Core.Verdict;

namespace UsbIntegrityTester.Tests;

public class DriveHistoryStoreTests
{
    private static ReportModel BuildSampleReport() => new()
    {
        TimestampUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        DriveLabel = "Sample Drive",
        SerialNumber = "SN123",
        ClaimedCapacityBytes = 64_000_000_000,
        ClaimedReadSpeedMegabytesPerSecond = 100,
        ClaimedWriteSpeedMegabytesPerSecond = 80,
        NegotiatedLinkSpeed = UsbLinkSpeed.Super5Gbps,
        TestResult = new TestResult
        {
            Capacity = new CapacityVerificationResult
            {
                VerifiedGoodBytes = 64_000_000_000,
                TotalBytesTested = 64_000_000_000,
                BlocksTested = 100,
                BlocksFailed = 0,
                WriteMegabytesPerSecond = 90,
                ReadMegabytesPerSecond = 95,
            },
            Slot1 = new SpeedTestSlotResult(
                SpeedTestCategory.LargeFiles,
                new SpeedTestResult { AverageMegabytesPerSecond = 85, PeakBurstMegabytesPerSecond = 120, SampledMegabytesPerSecond = [80, 85, 90] },
                new SpeedTestResult { AverageMegabytesPerSecond = 95, PeakBurstMegabytesPerSecond = 130, SampledMegabytesPerSecond = [90, 95, 100] }),
        },
        Verdict = new VerdictResult { Verdict = DriveVerdict.Ok, Explanation = "Meets claims." },
        TestDurationSeconds = 123.4,
        CapacityScanDepth = CapacityScanDepth.Standard,
    };

    [Fact]
    public void SaveAndGetReport_RoundTripsFullReportModel()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"history-test-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DriveHistoryStore(dbPath);
            var report = BuildSampleReport();

            store.Save(report);
            var entries = store.GetAllHistory();

            Assert.Single(entries);
            var loaded = store.GetReport(entries[0].Id);

            Assert.NotNull(loaded);
            Assert.Equal(report.SerialNumber, loaded!.SerialNumber);
            Assert.Equal(report.TimestampUtc, loaded.TimestampUtc);
            Assert.Equal(report.TestResult.Slot1!.Write.AverageMegabytesPerSecond, loaded.TestResult.Slot1!.Write.AverageMegabytesPerSecond);
            Assert.Equal(report.Verdict.Verdict, loaded.Verdict.Verdict);
        }
        finally
        {
            // SQLite pools connections by connection string, so the file stays open until the pool
            // is cleared — without this the temp file can't be deleted right after the test runs.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetReport_ReturnsNull_ForUnknownId()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"history-test-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DriveHistoryStore(dbPath);
            Assert.Null(store.GetReport(999));
        }
        finally
        {
            // SQLite pools connections by connection string, so the file stays open until the pool
            // is cleared — without this the temp file can't be deleted right after the test runs.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
