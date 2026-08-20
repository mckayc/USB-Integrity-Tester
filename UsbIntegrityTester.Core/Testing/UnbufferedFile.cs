using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// Writes/reads a whole file via CreateFile with FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
/// bypassing the Windows file cache — the same technique <see cref="RawDiskAccessor"/> uses for
/// Raw Mode. File Mode's speed test rotates through only a handful of files for its whole
/// duration; with normal buffered File.ReadAllBytes/WriteAllBytes, those files land in RAM after
/// the first pass and every read after that measures memory bandwidth, not the drive — multiple
/// GB/s "read speeds" on a USB flash drive. This forces every read and write to actually reach
/// the device.
/// </summary>
internal static class UnbufferedFile
{
    /// <summary>Safe alignment for both 512e and 4Kn drives — matches RawDiskAccessor. Buffer lengths passed here must be a multiple of this.</summary>
    public const int SectorSize = 4096;

    public static void WriteAllBytes(string path, byte[] buffer, int length)
    {
        ValidateAlignment(length);

        using var handle = Native.CreateFile(
            path, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.CREATE_ALWAYS, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32IOException($"Failed to open {path} for unbuffered write", Marshal.GetLastWin32Error());

        var unmanaged = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(buffer, 0, unmanaged, length);
            if (!Native.WriteFile(handle, unmanaged, length, out var written, IntPtr.Zero) || written != length)
                throw new Win32IOException($"Unbuffered write to {path} failed", Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(unmanaged);
        }
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes into <paramref name="destination"/>, which must be at least that large.</summary>
    public static void ReadAllBytes(string path, byte[] destination, int length)
    {
        ValidateAlignment(length);

        using var handle = Native.CreateFile(
            path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32IOException($"Failed to open {path} for unbuffered read", Marshal.GetLastWin32Error());

        var unmanaged = Marshal.AllocHGlobal(length);
        try
        {
            if (!Native.ReadFile(handle, unmanaged, length, out var read, IntPtr.Zero) || read != length)
                throw new Win32IOException($"Unbuffered read of {path} failed", Marshal.GetLastWin32Error());
            Marshal.Copy(unmanaged, destination, 0, length);
        }
        finally
        {
            Marshal.FreeHGlobal(unmanaged);
        }
    }

    private static void ValidateAlignment(int length)
    {
        if (length % SectorSize != 0)
            throw new ArgumentException($"Length {length} is not sector-aligned ({SectorSize} bytes) — required for unbuffered file I/O.");
    }

    /// <summary>An open handle for writing a file as a sequence of chunks instead of one call for
    /// the whole thing — needed once a single file is large enough that one write could take
    /// several seconds, which would otherwise mean only one throughput sample for that entire file.</summary>
    public sealed class ChunkedWriter : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public ChunkedWriter(string path)
        {
            _handle = Native.CreateFile(
                path, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.CREATE_ALWAYS, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new Win32IOException($"Failed to open {path} for chunked unbuffered write", Marshal.GetLastWin32Error());
        }

        public void WriteChunk(byte[] buffer, int length)
        {
            ValidateAlignment(length);
            var unmanaged = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(buffer, 0, unmanaged, length);
                if (!Native.WriteFile(_handle, unmanaged, length, out var written, IntPtr.Zero) || written != length)
                    throw new Win32IOException("Chunked unbuffered write failed", Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(unmanaged);
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    /// <summary>The read counterpart to <see cref="ChunkedWriter"/>.</summary>
    public sealed class ChunkedReader : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public ChunkedReader(string path)
        {
            _handle = Native.CreateFile(
                path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new Win32IOException($"Failed to open {path} for chunked unbuffered read", Marshal.GetLastWin32Error());
        }

        public void ReadChunk(byte[] destination, int length)
        {
            ValidateAlignment(length);
            var unmanaged = Marshal.AllocHGlobal(length);
            try
            {
                if (!Native.ReadFile(_handle, unmanaged, length, out var read, IntPtr.Zero) || read != length)
                    throw new Win32IOException("Chunked unbuffered read failed", Marshal.GetLastWin32Error());
                Marshal.Copy(unmanaged, destination, 0, length);
            }
            finally
            {
                Marshal.FreeHGlobal(unmanaged);
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint CREATE_ALWAYS = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            SafeFileHandle handle, IntPtr buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            SafeFileHandle handle, IntPtr buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);
    }
}
