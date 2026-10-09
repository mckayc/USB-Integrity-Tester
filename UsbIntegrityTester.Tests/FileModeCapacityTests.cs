using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.Tests;

/// <summary>Runs File Mode's capacity fill against a real temp folder, with small files so it's fast.</summary>
public class FileModeCapacityTests : IDisposable
{
    private const int BlockSize = 1024 * 1024;
    private const long FileSize = 3L * BlockSize;
    private const ulong TargetBytes = 10UL * BlockSize; // -> files of 3, 3, 3 and 1 MiB

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "UsbIntegrityTesterTests_" + Guid.NewGuid().ToString("N"));

    public FileModeCapacityTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task HealthyStorage_VerifiesEveryBlock_InAFewLargeFiles()
    {
        var result = await FileModeEngine.RunCapacityTestAsync(
            _folder, TargetBytes, TargetBytes, BlockSize, FileSize, progress: null, CancellationToken.None);

        Assert.Equal(4, Directory.GetFiles(_folder).Length);
        Assert.Equal(10, result.BlocksTested);
        Assert.Equal(0, result.BlocksFailed);
        Assert.Equal(TargetBytes, result.VerifiedGoodBytes);
    }

    [Fact]
    public async Task CorruptedBlock_IsCaught_AndVerifiedCapacityStopsJustBeforeIt()
    {
        var tampered = false;
        var progress = new SynchronousProgress(p =>
        {
            if (tampered || p.Phase != TestPhase.VerifyingCapacityPattern) return;
            tampered = true;

            // Third file starts at 6 MiB; flipping a byte 1.5 MiB into it damages the block at 7 MiB.
            using var stream = new FileStream(Path.Combine(_folder, "capacity_00002.bin"), FileMode.Open, FileAccess.ReadWrite);
            stream.Position = BlockSize + BlockSize / 2;
            var original = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)~original);
        });

        var result = await FileModeEngine.RunCapacityTestAsync(
            _folder, TargetBytes, TargetBytes, BlockSize, FileSize, progress, CancellationToken.None);

        Assert.Equal(1, result.BlocksFailed);
        Assert.Equal(7UL * BlockSize, result.VerifiedGoodBytes);
    }

    [Fact]
    public async Task Cancelling_CarriesPartialProgress_InsteadOfABareCancel()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(p =>
        {
            if (p.BytesProcessed >= 4UL * BlockSize) cts.Cancel();
        });

        var ex = await Assert.ThrowsAsync<CapacityTestCancelledException>(() => FileModeEngine.RunCapacityTestAsync(
            _folder, TargetBytes, TargetBytes, BlockSize, FileSize, progress, cts.Token));

        Assert.Equal(6UL * BlockSize, ex.BytesWritten);
        Assert.Equal(0UL, ex.BytesVerified);
    }

    /// <summary>Unlike <see cref="Progress{T}"/>, runs the callback inline on the reporting thread, so a test can act at an exact point.</summary>
    private sealed class SynchronousProgress(Action<TestProgress> onReport) : IProgress<TestProgress>
    {
        public void Report(TestProgress value) => onReport(value);
    }
}
