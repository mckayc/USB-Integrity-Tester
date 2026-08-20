using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbIntegrityTester.Core.Testing;

/// <summary>An I/O failure that captures the Win32 error code at the moment it occurred, so callers can act on it reliably.</summary>
public sealed class Win32IOException : IOException
{
    public int ErrorCode { get; }

    public Win32IOException(string message, int errorCode) : base($"{message} (Win32 error {errorCode}).")
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Sector-aligned, unbuffered access to a physical disk (\\.\PhysicalDriveN). Bypasses the
/// Windows file cache (FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH) so throughput and
/// verification measurements reflect the drive itself, not RAM.
/// </summary>
public sealed class RawDiskAccessor : IDisposable
{
    public const int SectorSize = 4096; // safe alignment for both 512e and 4Kn drives

    private readonly SafeFileHandle _handle;

    private RawDiskAccessor(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static RawDiskAccessor Open(string physicalDrivePath, bool writable)
    {
        var access = writable ? Native.GENERIC_READ | Native.GENERIC_WRITE : Native.GENERIC_READ;
        var handle = Native.CreateFile(
            physicalDrivePath, access, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32IOException($"Failed to open {physicalDrivePath}", Marshal.GetLastWin32Error());

        return new RawDiskAccessor(handle);
    }

    public ulong GetLengthInBytes()
    {
        var size = Marshal.SizeOf<long>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.DeviceIoControl(_handle, Native.IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0,
                    buffer, size, out _, IntPtr.Zero))
                throw new Win32IOException("IOCTL_DISK_GET_LENGTH_INFO failed", Marshal.GetLastWin32Error());

            return (ulong)Marshal.ReadInt64(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads exactly one sector-aligned block. <paramref name="byteOffset"/> and length must be sector-size multiples.</summary>
    public void ReadBlock(ulong byteOffset, byte[] destination, int length)
    {
        ValidateAlignment(byteOffset, length);

        var handleBuf = Marshal.AllocHGlobal(length);
        try
        {
            RetryOnDeviceNotReady(() =>
            {
                SeekTo(byteOffset);
                if (!Native.ReadFile(_handle, handleBuf, length, out var bytesRead, IntPtr.Zero) || bytesRead != length)
                    throw new Win32IOException($"ReadFile failed at offset {byteOffset}", Marshal.GetLastWin32Error());
            });

            Marshal.Copy(handleBuf, destination, 0, length);
        }
        finally
        {
            Marshal.FreeHGlobal(handleBuf);
        }
    }

    /// <summary>Writes exactly one sector-aligned block. <paramref name="byteOffset"/> and length must be sector-size multiples.</summary>
    public void WriteBlock(ulong byteOffset, byte[] source, int length)
    {
        ValidateAlignment(byteOffset, length);

        var handleBuf = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(source, 0, handleBuf, length);
            RetryOnDeviceNotReady(() =>
            {
                SeekTo(byteOffset);
                if (!Native.WriteFile(_handle, handleBuf, length, out var bytesWritten, IntPtr.Zero) || bytesWritten != length)
                    throw new Win32IOException($"WriteFile failed at offset {byteOffset}", Marshal.GetLastWin32Error());
            });
        }
        finally
        {
            Marshal.FreeHGlobal(handleBuf);
        }
    }

    // Cheap USB flash controllers commonly report ERROR_NOT_READY (21) for a brief moment right
    // after the volume is dismounted/locked, while the controller finishes internal housekeeping.
    // It's transient, not a real failure, so a short retry clears it up almost every time.
    private const int ErrorNotReady = 21;
    private const int MaxNotReadyRetries = 8;

    private static void RetryOnDeviceNotReady(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Win32IOException ex) when (ex.ErrorCode == ErrorNotReady && attempt < MaxNotReadyRetries)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private static void ValidateAlignment(ulong byteOffset, int length)
    {
        if (byteOffset % SectorSize != 0)
            throw new ArgumentException($"Offset {byteOffset} is not sector-aligned ({SectorSize} bytes).");
        if (length % SectorSize != 0)
            throw new ArgumentException($"Length {length} is not sector-aligned ({SectorSize} bytes).");
    }

    private void SeekTo(ulong byteOffset)
    {
        if (!Native.SetFilePointerEx(_handle, (long)byteOffset, IntPtr.Zero, Native.FILE_BEGIN))
            throw new Win32IOException($"Seek to {byteOffset} failed", Marshal.GetLastWin32Error());
    }

    public void Dispose() => _handle.Dispose();

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        public const uint FILE_BEGIN = 0;
        public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle handle, long distanceToMove, IntPtr newFilePointer, uint moveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
            IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
    }
}
