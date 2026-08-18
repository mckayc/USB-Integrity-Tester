using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbIntegrityTester.Core.Devices;

/// <summary>
/// Walks the PnP device tree to find the actual negotiated USB link speed for a drive,
/// as opposed to the nominal speed printed on the port. This is what distinguishes a
/// "USB 3.0 port" from "USB 3.0 port with a USB 2.0 hub silently in the path."
/// </summary>
/// <remarks>
/// Distinguishes Low/Full/High/Super speed reliably. Disambiguating SuperSpeed Gen1 vs
/// Gen2 vs Gen2x2 requires IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, which is a
/// reasonable next step but out of scope for the initial scaffold — for now Gen2+ links
/// report as Super5Gbps.
/// </remarks>
public static class UsbPortInspector
{
    public static UsbLinkSpeed? GetNegotiatedLinkSpeed(int physicalDriveIndex)
    {
        var pnpDeviceId = GetDiskPnpDeviceId(physicalDriveIndex);
        if (pnpDeviceId is null) return null;

        if (Native.CM_Locate_DevNodeW(out var diskDevInst, pnpDeviceId, 0) != Native.CR_SUCCESS)
            return null;

        // Walk up from the disk devnode to the USB device node (USB\VID_xxxx&PID_xxxx\...).
        var usbDevInst = FindAncestorUsbDeviceNode(diskDevInst);
        if (usbDevInst is null) return null;

        var portNumber = GetDevNodeAddress(usbDevInst.Value);
        if (portNumber is null) return null;

        if (Native.CM_Get_Parent(out var hubDevInst, usbDevInst.Value, 0) != Native.CR_SUCCESS)
            return null;

        var hubInstanceId = GetDeviceId(hubDevInst);
        if (hubInstanceId is null) return null;

        var hubPath = FindHubDeviceInterfacePath(hubInstanceId);
        if (hubPath is null) return null;

        return QueryLinkSpeed(hubPath, portNumber.Value);
    }

    private static string? GetDiskPnpDeviceId(int physicalDriveIndex)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index={physicalDriveIndex}");

        foreach (ManagementObject disk in searcher.Get())
            return (string?)disk["PNPDeviceID"];

        return null;
    }

    private static uint? FindAncestorUsbDeviceNode(uint startDevInst)
    {
        var current = startDevInst;
        for (var depth = 0; depth < 6; depth++)
        {
            var id = GetDeviceId(current);
            if (id is not null && id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                return current;

            if (Native.CM_Get_Parent(out var parent, current, 0) != Native.CR_SUCCESS)
                return null;

            current = parent;
        }

        return null;
    }

    private static string? GetDeviceId(uint devInst)
    {
        var buffer = new StringBuilder(512);
        return Native.CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Capacity, 0) == Native.CR_SUCCESS
            ? buffer.ToString()
            : null;
    }

    private static uint? GetDevNodeAddress(uint devInst)
    {
        var length = (uint)Marshal.SizeOf<uint>();
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            var result = Native.CM_Get_DevNode_Registry_PropertyW(
                devInst, Native.CM_DRP_ADDRESS, out _, buffer, ref length, 0);

            return result == Native.CR_SUCCESS ? (uint)Marshal.ReadInt32(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? FindHubDeviceInterfacePath(string targetHubInstanceId)
    {
        var hubGuid = Native.GUID_DEVINTERFACE_USB_HUB;
        var deviceInfoSet = Native.SetupDiGetClassDevs(
            ref hubGuid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1) return null;

        try
        {
            var interfaceData = new Native.SP_DEVICE_INTERFACE_DATA();
            interfaceData.cbSize = Marshal.SizeOf(interfaceData);

            for (uint index = 0; Native.SetupDiEnumDeviceInterfaces(
                     deviceInfoSet, IntPtr.Zero, ref hubGuid, index, ref interfaceData); index++)
            {
                var devInfoData = new Native.SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf(devInfoData);

                Native.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);

                var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // cbSize of the detail struct is 6 on x64 (4-byte field + wide char alignment).
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                    if (!Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out _, ref devInfoData))
                        continue;

                    var idBuffer = new StringBuilder(512);
                    if (!Native.SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfoData, idBuffer, (uint)idBuffer.Capacity, out _))
                        continue;

                    if (!string.Equals(idBuffer.ToString(), targetHubInstanceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pathPtr = IntPtr.Add(detailBuffer, 4);
                    return Marshal.PtrToStringAuto(pathPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return null;
    }

    private static UsbLinkSpeed? QueryLinkSpeed(string hubDevicePath, uint portNumber)
    {
        using var handle = Native.CreateFile(
            hubDevicePath, Native.GENERIC_WRITE, Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid) return null;

        const int bufferSize = 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, (int)portNumber); // ConnectionIndex at offset 0

            var ok = Native.DeviceIoControl(
                handle, Native.IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                buffer, bufferSize, buffer, bufferSize, out _, IntPtr.Zero);

            if (!ok) return null;

            var speedByte = Marshal.ReadByte(buffer, Native.SpeedFieldOffset);
            return speedByte switch
            {
                0 => UsbLinkSpeed.Low1_5Mbps,
                1 => UsbLinkSpeed.Full12Mbps,
                2 => UsbLinkSpeed.High480Mbps,
                3 => UsbLinkSpeed.Super5Gbps,
                _ => UsbLinkSpeed.Unknown,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class Native
    {
        public const int CR_SUCCESS = 0;
        public const uint CM_DRP_ADDRESS = 0x1D;
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        // ConnectionIndex(4) + USB_DEVICE_DESCRIPTOR(18) + CurrentConfigurationValue(1) = offset 23.
        public const int SpeedFieldOffset = 23;

        // CTL_CODE(FILE_DEVICE_USB=0x22, 274, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
        public const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;

        public static Guid GUID_DEVINTERFACE_USB_HUB = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_Registry_PropertyW(
            uint dnDevInst, uint ulProperty, out uint pulRegDataType, IntPtr buffer, ref uint pulLength, uint ulFlags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId,
            uint deviceInstanceIdSize, out uint requiredSize);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioControlCode,
            IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);
    }
}
