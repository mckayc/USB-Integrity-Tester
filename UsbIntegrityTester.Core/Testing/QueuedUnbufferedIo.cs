using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// Runs sequential unbuffered I/O against a single fixed-size file with a real queue depth — up to
/// <c>queueDepth</c> overlapped requests kept in flight at once, issued and reaped by one thread —
/// to reproduce benchmarks like CrystalDiskMark's "SEQ1M Q8T1" (1 MiB blocks, queue depth 8, one
/// thread), instead of the one-request-at-a-time synchronous pattern used elsewhere in this app.
/// A single outstanding request (queue depth 1) can leave a lot of a fast USB link's bandwidth
/// unused, particularly over UASP, which supports multiple in-flight commands; queuing several
/// requests keeps the link saturated the way a real multi-file copy or a QD8 benchmark would.
/// </summary>
internal static class QueuedUnbufferedIo
{
    /// <summary>Creates (or truncates) <paramref name="path"/> and reserves exactly <paramref name="sizeBytes"/> bytes
    /// up front via SetEndOfFile, so every offset the queued loop writes to is valid from the start.</summary>
    public static void PreallocateFile(string path, long sizeBytes)
    {
        using var handle = Native.CreateFile(
            path, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.CREATE_ALWAYS, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32IOException($"Failed to create {path} for preallocation", Marshal.GetLastWin32Error());

        if (!Native.SetFilePointerEx(handle, sizeBytes, IntPtr.Zero, Native.FILE_BEGIN))
            throw new Win32IOException($"SetFilePointerEx failed while preallocating {path}", Marshal.GetLastWin32Error());

        if (!Native.SetEndOfFile(handle))
            throw new Win32IOException($"SetEndOfFile failed while preallocating {path}", Marshal.GetLastWin32Error());
    }

    /// <summary>Writes pseudo-random content — distinct per block, keyed by its offset, wrapping at
    /// <paramref name="regionSizeBytes"/> — for <paramref name="duration"/>, keeping <paramref name="queueDepth"/>
    /// requests in flight at once. Deliberately not the same block repeated: a flash controller that
    /// detects a run of bit-for-bit identical writes can fast-path them, inflating the result the same
    /// way a compressible fill pattern would — CrystalDiskMark avoids this for the same reason (see its
    /// FAQ note that results "depend on test data").</summary>
    /// <param name="passCount">When set, the loop runs exactly this many complete sequential passes
    /// over <paramref name="regionSizeBytes"/> instead of running for a fixed duration — matching
    /// CrystalDiskMark's model (a fixed loop count over the whole test file, averaged across passes)
    /// rather than "whatever fits in N seconds," which can land at a different point in a drive's
    /// cache-fill/cache-drain cycle depending on how fast it happens to be running that moment.
    /// <paramref name="duration"/> still applies as a safety timeout either way, so a dead-slow or
    /// stalled drive can't hang the test indefinitely.</param>
    /// <param name="randomAccess">False (default): offsets step sequentially through the region,
    /// wrapping at the end — SEQ1M. True: each request lands at a uniformly random block-aligned
    /// offset within the region — RND4K.</param>
    public static SpeedTestResult RunTimedWrite(
        string path, ulong contentSeed, int blockSizeBytes, long regionSizeBytes, int queueDepth,
        TimeSpan duration, int? passCount, bool randomAccess, TestPhase phase, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        using var handle = Native.CreateFile(
            path, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED | Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32IOException($"Failed to open {path} for queued write", Marshal.GetLastWin32Error());

        return RunQueuedLoop(handle, isWrite: true, contentSeed, blockSizeBytes, regionSizeBytes, queueDepth,
            duration, passCount, randomAccess, phase, progress, cancellationToken);
    }

    /// <summary>Reads back <paramref name="regionSizeBytes"/> worth of a previously-written file, keeping
    /// <paramref name="queueDepth"/> requests in flight at once. See <see cref="RunTimedWrite"/> for
    /// <paramref name="passCount"/>/<paramref name="duration"/> semantics.</summary>
    public static SpeedTestResult RunTimedRead(
        string path, int blockSizeBytes, long regionSizeBytes, int queueDepth,
        TimeSpan duration, int? passCount, bool randomAccess, TestPhase phase, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        using var handle = Native.CreateFile(
            path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED | Native.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32IOException($"Failed to open {path} for queued read", Marshal.GetLastWin32Error());

        return RunQueuedLoop(handle, isWrite: false, 0, blockSizeBytes, regionSizeBytes, queueDepth,
            duration, passCount, randomAccess, phase, progress, cancellationToken);
    }

    private static SpeedTestResult RunQueuedLoop(
        SafeFileHandle handle, bool isWrite, ulong contentSeed, int blockSizeBytes, long regionSizeBytes, int queueDepth,
        TimeSpan duration, int? passCount, bool randomAccess, TestPhase phase, IProgress<TestProgress>? progress, CancellationToken cancellationToken)
    {
        var blockCount = regionSizeBytes / blockSizeBytes;
        var rng = randomAccess ? new Random() : null;

        // Sampling *with* replacement would let a request accidentally re-land on a block another
        // request touched moments earlier in the same run — and a drive's own cache can serve that
        // repeat almost instantly, quietly inflating the result (especially at low queue depth,
        // where throughput is latency-bound and a single cache hit skews the whole sample). A
        // shuffled permutation of every block, replayed once per pass and reshuffled between passes,
        // guarantees no block is revisited within a pass — the standard way IOPS benchmarks (and,
        // per its own docs, CrystalDiskMark) avoid this artifact.
        var blockOrder = rng is not null ? CreateIdentityBlockOrder(blockCount) : null;
        var shuffleCursor = blockCount; // forces a shuffle before the very first draw

        long NextRandomOffset()
        {
            if (shuffleCursor >= blockCount)
            {
                FisherYatesShuffle(blockOrder!, rng!);
                shuffleCursor = 0;
            }
            return blockOrder![shuffleCursor++] * blockSizeBytes;
        }
        var targetBytes = passCount is { } passes ? (long)passes * regionSizeBytes : long.MaxValue;
        // Pass-count mode has no natural time budget to report fractional progress against, and to
        // scale the "elapsed" progress fraction it also needs a safety ceiling — reuse the caller's
        // duration as a generous timeout instead of the primary stop condition in that mode.
        var safetyTimeout = passCount is null ? duration : TimeSpan.FromSeconds(duration.TotalSeconds * 6);
        var overlappedSize = Marshal.SizeOf<OVERLAPPED>();
        var events = new IntPtr[queueDepth];
        var eventHandles = new IntPtr[queueDepth];
        var overlappedPtrs = new IntPtr[queueDepth];
        // Every slot needs its own buffer regardless of direction: reads because each in-flight
        // request lands in a different place, writes because each block must carry distinct content
        // (see RunTimedWrite) rather than one buffer shared/reused across concurrent requests.
        var buffers = new IntPtr[queueDepth];
        var pending = new bool[queueDepth];
        var writeScratch = isWrite ? new byte[blockSizeBytes] : null;

        try
        {
            for (var i = 0; i < queueDepth; i++)
            {
                var evt = Native.CreateEvent(IntPtr.Zero, manualReset: false, initialState: false, name: null);
                if (evt == IntPtr.Zero)
                    throw new Win32IOException("CreateEvent failed", Marshal.GetLastWin32Error());
                events[i] = evt;
                eventHandles[i] = evt;
                overlappedPtrs[i] = Marshal.AllocHGlobal(overlappedSize);
                buffers[i] = Marshal.AllocHGlobal(blockSizeBytes);
            }

            var stopwatch = Stopwatch.StartNew();
            var windowStopwatch = Stopwatch.StartNew();
            var sampleWindowInterval = TimeSpan.FromMilliseconds(250);
            var samples = new List<double>();
            long bytesInWindow = 0;
            long bytesIssued = 0;
            long bytesCompleted = 0;
            double peak = 0;
            long nextOffset = 0;

            long Issue(int slot, long offset)
            {
                WriteOverlappedOffset(overlappedPtrs[slot], offset, events[slot]);
                var buffer = buffers[slot];

                if (isWrite)
                {
                    // Content keyed by block index (not a static/shared buffer) so no two blocks in
                    // the file are identical — see RunTimedWrite's remarks.
                    CapacityVerifier.FillDeterministicBlock(writeScratch!, contentSeed, (ulong)(offset / blockSizeBytes));
                    Marshal.Copy(writeScratch!, 0, buffer, blockSizeBytes);
                }

                var ok = isWrite
                    ? Native.WriteFile(handle, buffer, blockSizeBytes, out _, overlappedPtrs[slot])
                    : Native.ReadFile(handle, buffer, blockSizeBytes, out _, overlappedPtrs[slot]);

                if (!ok)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING)
                        throw new Win32IOException($"Queued {(isWrite ? "write" : "read")} failed to start", err);
                }

                pending[slot] = true;
                bytesIssued += blockSizeBytes;

                if (rng is not null)
                    return NextRandomOffset();

                var next = offset + blockSizeBytes;
                return next >= regionSizeBytes ? 0 : next;
            }

            void Reap(int slot)
            {
                if (!Native.GetOverlappedResult(handle, overlappedPtrs[slot], out var transferred, wait: true) || transferred != blockSizeBytes)
                    throw new Win32IOException($"Queued {(isWrite ? "write" : "read")} completion failed", Marshal.GetLastWin32Error());
                pending[slot] = false;
            }

            for (var i = 0; i < queueDepth && bytesIssued < targetBytes; i++)
                nextOffset = Issue(i, nextOffset);

            while (stopwatch.Elapsed < safetyTimeout && bytesCompleted < targetBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var waitResult = Native.WaitForMultipleObjects((uint)queueDepth, eventHandles, waitAll: false, Native.INFINITE);
                if (waitResult >= queueDepth)
                    throw new Win32IOException("WaitForMultipleObjects failed", Marshal.GetLastWin32Error());

                var slot = (int)waitResult;
                Reap(slot);
                bytesInWindow += blockSizeBytes;
                bytesCompleted += blockSizeBytes;
                if (bytesIssued < targetBytes)
                    nextOffset = Issue(slot, nextOffset);

                if (windowStopwatch.Elapsed >= sampleWindowInterval)
                {
                    var windowSeconds = windowStopwatch.Elapsed.TotalSeconds;
                    var windowMegabytes = bytesInWindow / 1_000_000.0;
                    var mbPerSec = windowSeconds > 0 ? windowMegabytes / windowSeconds : 0;
                    samples.Add(mbPerSec);
                    peak = Math.Max(peak, mbPerSec);

                    var elapsedFraction = passCount is null
                        ? Math.Clamp(stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds, 0, 1)
                        : Math.Clamp((double)bytesCompleted / targetBytes, 0, 1);
                    progress?.Report(new TestProgress
                    {
                        Phase = phase,
                        BytesProcessed = (ulong)(elapsedFraction * 1000),
                        TotalBytes = 1000,
                        CurrentThroughputMegabytesPerSecond = mbPerSec,
                    });

                    bytesInWindow = 0;
                    windowStopwatch.Restart();
                }
            }

            // Drain whatever's still in flight so no request outlives the buffers/handle it points to.
            for (var i = 0; i < queueDepth; i++)
                if (pending[i]) Reap(i);

            return new SpeedTestResult
            {
                AverageMegabytesPerSecond = samples.Count > 0 ? samples.Average() : 0,
                PeakBurstMegabytesPerSecond = peak,
                SampledMegabytesPerSecond = samples,
            };
        }
        finally
        {
            for (var i = 0; i < queueDepth; i++)
            {
                if (overlappedPtrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(overlappedPtrs[i]);
                if (buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(buffers[i]);
                if (events[i] != IntPtr.Zero) Native.CloseHandle(events[i]);
            }
        }
    }

    private static long[] CreateIdentityBlockOrder(long blockCount)
    {
        var order = new long[blockCount];
        for (var i = 0L; i < blockCount; i++) order[i] = i;
        return order;
    }

    private static void FisherYatesShuffle(long[] order, Random rng)
    {
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    private static void WriteOverlappedOffset(IntPtr overlappedPtr, long offset, IntPtr eventHandle)
    {
        var overlapped = new OVERLAPPED
        {
            Internal = UIntPtr.Zero,
            InternalHigh = UIntPtr.Zero,
            OffsetLow = unchecked((uint)offset),
            OffsetHigh = unchecked((uint)(offset >> 32)),
            hEvent = eventHandle,
        };
        Marshal.StructureToPtr(overlapped, overlappedPtr, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public UIntPtr Internal;
        public UIntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint CREATE_ALWAYS = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        public const uint FILE_BEGIN = 0;
        public const int ERROR_IO_PENDING = 997;
        public const uint INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            SafeFileHandle handle, IntPtr buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            SafeFileHandle handle, IntPtr buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(
            SafeFileHandle handle, IntPtr overlapped, out int numberOfBytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle handle, long distanceToMove, IntPtr newFilePointer, uint moveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEndOfFile(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
