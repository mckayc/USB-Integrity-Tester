using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbIntegrityTester.Core.Testing;

/// <summary>
/// Locks and dismounts a volume before destructive raw-disk writes so Windows (and any
/// background process holding the volume open) doesn't fight the test or reintroduce
/// cached state. Must be held for the duration of a write/capacity test.
/// </summary>
public sealed class VolumeLocker : IDisposable
{
    private readonly SafeFileHandle _handle;

    private VolumeLocker(SafeFileHandle handle)
    {
        _handle = handle;
    }

    /// <param name="volumeLetter">e.g. "E:" (no trailing backslash).</param>
    public static VolumeLocker LockAndDismount(string volumeLetter)
    {
        var path = $@"\\.\{volumeLetter}";
        var handle = Native.CreateFile(
            path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"Failed to open volume {volumeLetter} (Win32 error {Marshal.GetLastWin32Error()}).");

        if (!Native.DeviceIoControl(handle, Native.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Failed to lock volume {volumeLetter} — close any open handles (Explorer, other apps) and retry. (Win32 error {error})");
        }

        if (!Native.DeviceIoControl(handle, Native.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Failed to dismount volume {volumeLetter}. (Win32 error {error})");
        }

        return new VolumeLocker(handle);
    }

    public void Dispose()
    {
        Native.DeviceIoControl(_handle, Native.FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        _handle.Dispose();
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FSCTL_LOCK_VOLUME = 0x00090018;
        public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
            IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
    }
}
