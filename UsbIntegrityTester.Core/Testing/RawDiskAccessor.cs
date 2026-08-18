using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbIntegrityTester.Core.Testing;

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
            throw new IOException($"Failed to open {physicalDrivePath} (Win32 error {Marshal.GetLastWin32Error()}).");

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
                throw new IOException($"IOCTL_DISK_GET_LENGTH_INFO failed (Win32 error {Marshal.GetLastWin32Error()}).");

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
        SeekTo(byteOffset);

        var handleBuf = Marshal.AllocHGlobal(length);
        try
        {
            if (!Native.ReadFile(_handle, handleBuf, length, out var bytesRead, IntPtr.Zero) || bytesRead != length)
                throw new IOException($"ReadFile failed at offset {byteOffset} (Win32 error {Marshal.GetLastWin32Error()}).");

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
        SeekTo(byteOffset);

        var handleBuf = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(source, 0, handleBuf, length);
            if (!Native.WriteFile(_handle, handleBuf, length, out var bytesWritten, IntPtr.Zero) || bytesWritten != length)
                throw new IOException($"WriteFile failed at offset {byteOffset} (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(handleBuf);
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
            throw new IOException($"Seek to {byteOffset} failed (Win32 error {Marshal.GetLastWin32Error()}).");
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
