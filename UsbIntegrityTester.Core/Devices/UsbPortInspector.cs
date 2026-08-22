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
/// Reads Low/Full/High/Super from the base IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, then
/// cross-checks against IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2's operating-speed flags —
/// both to tell Gen 1 (5 Gbps) apart from Gen 2-or-higher (10/20 Gbps), and because the base
/// IOCTL's Speed byte is known to misreport on some USB-to-NVMe/SATA bridge chips (a device
/// sustaining hundreds of MB/s while the base IOCTL claims High-Speed is a strong tell). EX_V2's
/// flags win whenever the two disagree. This still cannot tell Gen 2 (10 Gbps) apart from Gen 2x2
/// (20 Gbps) — that needs the device's SuperSpeedPlus BOS descriptor, which this doesn't read.
/// </remarks>
public sealed record UsbDeviceDetails
{
    public string? VendorId { get; init; }
    public string? ProductId { get; init; }
    public string? DriverServiceName { get; init; }
    public required string TransportDescription { get; init; }

    /// <summary>How many hub layers between the drive and the root hub — 0 means plugged straight into a root port.</summary>
    public required int HubHopsFromRoot { get; init; }
}

public static class UsbPortInspector
{
    /// <param name="log">Optional diagnostic sink — reports which step succeeded/failed, since this
    /// walk has several points where Windows' device tree can legitimately not resolve.</param>
    public static UsbLinkSpeed? GetNegotiatedLinkSpeed(int physicalDriveIndex, Action<string>? log = null)
    {
        var pnpDeviceId = GetDiskPnpDeviceId(physicalDriveIndex);
        if (pnpDeviceId is null)
        {
            log?.Invoke($"No WMI PNPDeviceID found for PhysicalDrive{physicalDriveIndex}.");
            return null;
        }
        log?.Invoke($"PNPDeviceID: {pnpDeviceId}");

        if (Native.CM_Locate_DevNodeW(out var diskDevInst, pnpDeviceId, 0) != Native.CR_SUCCESS)
        {
            log?.Invoke("CM_Locate_DevNodeW failed to resolve a devnode for that PNPDeviceID.");
            return null;
        }

        // Walk up from the disk devnode to the USB device node (USB\VID_xxxx&PID_xxxx\...).
        var usbDevInst = FindAncestorUsbDeviceNode(diskDevInst, log);
        if (usbDevInst is null)
        {
            log?.Invoke("Could not find a USB\\ device node within 6 levels above the disk in the device tree.");
            return null;
        }

        var portNumber = GetDevNodeAddress(usbDevInst.Value);
        if (portNumber is null)
        {
            log?.Invoke("CM_Get_DevNode_Registry_PropertyW(CM_DRP_ADDRESS) failed — couldn't read the USB device's port number.");
            return null;
        }
        log?.Invoke($"USB device is on hub port {portNumber}.");

        if (Native.CM_Get_Parent(out var hubDevInst, usbDevInst.Value, 0) != Native.CR_SUCCESS)
        {
            log?.Invoke("CM_Get_Parent failed to find the hub devnode above the USB device node.");
            return null;
        }

        var hubInstanceId = GetDeviceId(hubDevInst);
        if (hubInstanceId is null)
        {
            log?.Invoke("CM_Get_Device_IDW failed to resolve the hub's instance ID.");
            return null;
        }
        log?.Invoke($"Hub instance ID: {hubInstanceId}");

        var hubPath = FindHubDeviceInterfacePath(hubInstanceId);
        if (hubPath is null)
        {
            log?.Invoke("Could not find a GUID_DEVINTERFACE_USB_HUB device interface path matching that hub instance ID — is it a root hub with a driver that doesn't expose one?");
            return null;
        }
        log?.Invoke($"Hub device path: {hubPath}");

        var speed = QueryLinkSpeed(hubPath, portNumber.Value, log);
        log?.Invoke(speed is null
            ? "IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX failed on the hub handle (try running as Administrator)."
            : $"Reported speed: {speed}.");

        return speed;
    }

    /// <summary>
    /// Extra diagnostic info beyond raw link speed: vendor/product ID, storage driver (which tells
    /// you whether the drive uses UAS — USB Attached SCSI, faster with command queuing — or the
    /// older BOT/Bulk-Only Transport protocol), and how many hub hops it's behind the root.
    /// </summary>
    public static UsbDeviceDetails? GetDeviceDetails(int physicalDriveIndex, Action<string>? log = null)
    {
        var pnpDeviceId = GetDiskPnpDeviceId(physicalDriveIndex);
        if (pnpDeviceId is null) return null;

        if (Native.CM_Locate_DevNodeW(out var diskDevInst, pnpDeviceId, 0) != Native.CR_SUCCESS) return null;

        var usbDevInst = FindAncestorUsbDeviceNode(diskDevInst, log);
        if (usbDevInst is null) return null;

        var usbInstanceId = GetDeviceId(usbDevInst.Value);
        if (usbInstanceId is null) return null;

        var vidMatch = System.Text.RegularExpressions.Regex.Match(usbInstanceId, @"VID_([0-9A-Fa-f]{4})");
        var pidMatch = System.Text.RegularExpressions.Regex.Match(usbInstanceId, @"PID_([0-9A-Fa-f]{4})");

        var driverInfName = GetDriverInfName(usbInstanceId);
        log?.Invoke($"Storage driver INF: {driverInfName ?? "(not found)"}");

        return new UsbDeviceDetails
        {
            VendorId = vidMatch.Success ? vidMatch.Groups[1].Value.ToUpperInvariant() : null,
            ProductId = pidMatch.Success ? pidMatch.Groups[1].Value.ToUpperInvariant() : null,
            DriverServiceName = driverInfName,
            TransportDescription = DescribeTransport(driverInfName),
            HubHopsFromRoot = CountHubHops(usbDevInst.Value),
        };
    }

    private static string? GetDriverInfName(string deviceInstanceId)
    {
        // Win32_PnPSignedDriver has no "Service" property (that's Win32_SystemDriver) — the
        // driver identity here comes from InfName instead ("usbstor.inf" = BOT, "uaspstor.inf" =
        // UAS). Also: a plain WHERE DeviceID='...' equality throws "Invalid query" for these
        // backslash-containing instance IDs no matter how the backslashes are escaped (verified
        // directly against the live WMI provider) — a LIKE match on the unique tail after the
        // last backslash (the device's serial number) sidesteps the problem entirely.
        var tail = deviceInstanceId[(deviceInstanceId.LastIndexOf('\\') + 1)..];

        using var searcher = new ManagementObjectSearcher(
            $"SELECT InfName FROM Win32_PnPSignedDriver WHERE DeviceID LIKE '%{tail}'");

        foreach (ManagementObject driver in searcher.Get())
            return (string?)driver["InfName"];

        return null;
    }

    private static string DescribeTransport(string? driverInfName) => driverInfName?.ToLowerInvariant() switch
    {
        "usbstor.inf" => "BOT (Bulk-Only Transport) — the older USB mass-storage protocol, one command in flight at a time.",
        "uaspstor.inf" => "UAS (USB Attached SCSI) — faster, supports multiple commands in flight (command queuing).",
        null => "Unknown — couldn't find a matching driver via WMI.",
        _ => $"Unknown transport (driver INF: {driverInfName}) — likely a vendor-specific UAS driver.",
    };

    private static int CountHubHops(uint usbDevInst)
    {
        var hops = 0;
        var current = usbDevInst;

        for (var i = 0; i < 10; i++)
        {
            if (Native.CM_Get_Parent(out var parent, current, 0) != Native.CR_SUCCESS) break;

            var id = GetDeviceId(parent);
            if (id is null) break;
            if (id.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) break;

            hops++;
            current = parent;
        }

        return hops;
    }

    private static string? GetDiskPnpDeviceId(int physicalDriveIndex)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index={physicalDriveIndex}");

        foreach (ManagementObject disk in searcher.Get())
            return (string?)disk["PNPDeviceID"];

        return null;
    }

    private static uint? FindAncestorUsbDeviceNode(uint startDevInst, Action<string>? log)
    {
        var current = startDevInst;
        for (var depth = 0; depth < 6; depth++)
        {
            var id = GetDeviceId(current);
            log?.Invoke($"  ancestor[{depth}]: {id ?? "(unknown)"}");
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

    private static UsbLinkSpeed? QueryLinkSpeed(string hubDevicePath, uint portNumber, Action<string>? log)
    {
        using var handle = Native.CreateFile(
            hubDevicePath, Native.GENERIC_WRITE, Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            log?.Invoke($"CreateFile on hub path failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return null;
        }

        var baseSpeed = QueryBaseSpeed(handle, portNumber);
        log?.Invoke($"Base IOCTL speed byte decoded as: {baseSpeed?.ToString() ?? "(IOCTL failed)"}.");

        // The legacy EX IOCTL's Speed byte is known to misreport on some USB-to-NVMe/SATA bridge
        // chips (observed with an ASMedia ASM235CM bridge reporting High-Speed while the drive
        // sustained 250-350 MB/s — physically impossible over a real 480 Mbps link). The newer
        // EX_V2 IOCTL's operating-speed flags are more reliable, so query them unconditionally and
        // trust them over the base byte whenever they disagree, rather than only using EX_V2 to
        // refine an already-SuperSpeed result.
        var v2Flags = QueryV2Flags(handle, portNumber);
        if (v2Flags is not { } flags)
        {
            log?.Invoke("EX_V2 IOCTL failed — falling back to the base IOCTL result only.");
            return baseSpeed;
        }

        log?.Invoke($"EX_V2 flags: operatingAtSuperSpeedOrHigher={flags.OperatingAtSuperSpeedOrHigher}, operatingAtSuperSpeedPlusOrHigher={flags.OperatingAtSuperSpeedPlusOrHigher}.");

        if (flags.OperatingAtSuperSpeedPlusOrHigher)
        {
            if (baseSpeed != UsbLinkSpeed.SuperPlus10Gbps)
                log?.Invoke($"EX_V2 reports SuperSpeedPlus-or-higher but base IOCTL said {baseSpeed?.ToString() ?? "(failed)"} — trusting EX_V2.");
            return UsbLinkSpeed.SuperPlus10Gbps;
        }

        if (flags.OperatingAtSuperSpeedOrHigher)
        {
            if (baseSpeed != UsbLinkSpeed.Super5Gbps)
                log?.Invoke($"EX_V2 reports SuperSpeed-or-higher but base IOCTL said {baseSpeed?.ToString() ?? "(failed)"} — trusting EX_V2 (some bridge chips report stale speed via the legacy IOCTL).");
            return UsbLinkSpeed.Super5Gbps;
        }

        return baseSpeed;
    }

    private static UsbLinkSpeed? QueryBaseSpeed(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint portNumber)
    {
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

    private readonly record struct V2SpeedFlags(bool OperatingAtSuperSpeedOrHigher, bool OperatingAtSuperSpeedPlusOrHigher);

    private static V2SpeedFlags? QueryV2Flags(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint portNumber)
    {
        const int bufferSize = 16; // ConnectionIndex(4) + Length(4) + SupportedUsbProtocols(4) + Flags(4)
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, (int)portNumber);
            Marshal.WriteInt32(buffer, 4, bufferSize);
            Marshal.WriteInt32(buffer, 8, Native.USB_PROTOCOLS_USB300_BIT); // request USB 3.0+ protocol info

            var ok = Native.DeviceIoControl(
                handle, Native.IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2,
                buffer, bufferSize, buffer, bufferSize, out _, IntPtr.Zero);

            if (!ok) return null;

            var flags = Marshal.ReadInt32(buffer, 12);
            return new V2SpeedFlags(
                (flags & Native.FLAG_DEVICE_IS_OPERATING_AT_SUPERSPEED_OR_HIGHER) != 0,
                (flags & Native.FLAG_DEVICE_IS_OPERATING_AT_SUPERSPEEDPLUS_OR_HIGHER) != 0);
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

        // CTL_CODE(FILE_DEVICE_USB=0x22, 279, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
        public const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;

        // USB_PROTOCOLS bitfield: Usb110 = bit 0, Usb200 = bit 1, Usb300 = bit 2.
        public const int USB_PROTOCOLS_USB300_BIT = 1 << 2;

        // USB_NODE_CONNECTION_INFORMATION_EX_V2_FLAGS bitfield.
        public const int FLAG_DEVICE_IS_OPERATING_AT_SUPERSPEED_OR_HIGHER = 1 << 0;
        public const int FLAG_DEVICE_IS_OPERATING_AT_SUPERSPEEDPLUS_OR_HIGHER = 1 << 2;

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
