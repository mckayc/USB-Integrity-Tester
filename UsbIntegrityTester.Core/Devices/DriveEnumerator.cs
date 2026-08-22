using System.Management;
using System.Runtime.InteropServices;

namespace UsbIntegrityTester.Core.Devices;

/// <summary>Finds removable USB disks currently attached to the system via WMI.</summary>
public static class DriveEnumerator
{
    public static IReadOnlyList<DriveInfo> GetRemovableUsbDrives() => GetRemovableUsbDrives(out _);

    /// <summary>
    /// Enumerates removable USB drives. <paramref name="diagnostics"/> gets one line per physical
    /// disk WMI reported — included or skipped, and why — so a drive that doesn't show up in the
    /// UI can be traced instead of just silently missing.
    /// </summary>
    public static IReadOnlyList<DriveInfo> GetRemovableUsbDrives(out IReadOnlyList<string> diagnostics)
    {
        var drives = new List<DriveInfo>();
        var diag = new List<string>();

        // Win32_DiskDrive's own InterfaceType/PNPDeviceID are unreliable for USB detection: a UASP
        // enclosure gets enumerated through the storage class driver and shows up as plain
        // "SCSI\DISK&VEN_..." with InterfaceType="SCSI" — nothing on that object hints it's USB.
        // MSFT_PhysicalDisk.BusType (from the newer Storage WMI namespace) reports the true bus even
        // in that case, so use it as the primary signal, keyed by physical drive index. Fall back to
        // the legacy heuristic if that namespace/class isn't available (older Windows, WMI repository
        // issues) so we still catch plain USB mass-storage devices.
        var usbBusIndexes = GetUsbBusPhysicalDriveIndexes(diag);

        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");

        foreach (ManagementObject disk in searcher.Get())
        {
            var deviceId = (string?)disk["DeviceID"] ?? string.Empty;
            var model = ((string?)disk["Model"] ?? "Unknown Drive").Trim();
            var interfaceType = (string?)disk["InterfaceType"] ?? "UNKNOWN";
            var pnpDeviceId = (string?)disk["PNPDeviceID"] ?? string.Empty;

            var indexMatch = System.Text.RegularExpressions.Regex.Match(deviceId, @"PHYSICALDRIVE(\d+)");
            if (!indexMatch.Success)
            {
                diag.Add($"skipped \"{model}\" — DeviceID \"{deviceId}\" didn't match PHYSICALDRIVE pattern.");
                continue;
            }

            var index = int.Parse(indexMatch.Groups[1].Value);

            var isUsb = usbBusIndexes.Contains(index)
                        || interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase)
                        || pnpDeviceId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase)
                        || pnpDeviceId.Contains("USB\\VID_", StringComparison.OrdinalIgnoreCase);

            if (!isUsb)
            {
                diag.Add($"skipped \"{model}\" (PHYSICALDRIVE{index}) — InterfaceType=\"{interfaceType}\", PNPDeviceID=\"{pnpDeviceId}\", not in MSFT_PhysicalDisk USB set — didn't look like USB.");
                continue;
            }
            var capacity = disk["Size"] is not null ? Convert.ToUInt64(disk["Size"]) : 0UL;
            var serial = ((string?)disk["SerialNumber"] ?? "UNKNOWN").Trim();

            var volumeLetters = GetAllVolumeLetters(index);
            var (fileSystemType, totalBytes, freeBytes) = GetVolumeSpaceInfo(volumeLetters.FirstOrDefault());

            diag.Add($"included \"{model}\" (PHYSICALDRIVE{index}) — InterfaceType=\"{interfaceType}\", Serial=\"{serial}\", Size={capacity}, Volumes=[{string.Join(",", volumeLetters)}], FS={fileSystemType ?? "(none)"}.");

            drives.Add(new DriveInfo
            {
                PhysicalDriveIndex = index,
                Model = model,
                SerialNumber = serial,
                ReportedCapacityBytes = capacity,
                InterfaceType = interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : $"USB ({interfaceType})",
                IsRemovable = true,
                VolumeLetter = volumeLetters.FirstOrDefault(),
                VolumeLetters = volumeLetters,
                FileSystemType = fileSystemType,
                TotalVolumeBytes = totalBytes,
                AvailableFreeBytes = freeBytes,
            });
        }

        diagnostics = diag;
        return drives;
    }

    /// <summary>
    /// Physical drive indexes that Windows' Storage Management WMI reports as being on a USB bus
    /// (MSFT_PhysicalDisk.BusType == 7). This sees through UASP/bridge enclosures that Win32_DiskDrive
    /// reports as "SCSI". DeviceId on MSFT_PhysicalDisk is the same physical drive number used in
    /// Win32_DiskDrive's "\\.\PHYSICALDRIVEn" DeviceID.
    /// </summary>
    private static HashSet<int> GetUsbBusPhysicalDriveIndexes(List<string> diag)
    {
        const int usbBusType = 7;
        var indexes = new HashSet<int>();

        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, BusType FROM MSFT_PhysicalDisk"));

            foreach (ManagementObject physicalDisk in searcher.Get())
            {
                var busType = physicalDisk["BusType"] is not null ? Convert.ToInt32(physicalDisk["BusType"]) : -1;
                if (busType != usbBusType) continue;

                var deviceId = (string?)physicalDisk["DeviceId"];
                if (deviceId is not null && int.TryParse(deviceId, out var index))
                    indexes.Add(index);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            diag.Add($"MSFT_PhysicalDisk lookup unavailable ({ex.GetType().Name}: {ex.Message}) — falling back to Win32_DiskDrive InterfaceType/PNPDeviceID heuristics only.");
        }

        return indexes;
    }

    /// <summary>
    /// All volume letters on this physical disk — not just the first. Some drives ship with more
    /// than one partition (e.g. a small bundled-software partition alongside the main data
    /// partition); if only the first is locked before a raw write, the second stays mounted and
    /// Windows' storage stack refuses raw writes to the disk, surfacing as ERROR_NOT_READY.
    /// </summary>
    private static IReadOnlyList<string> GetAllVolumeLetters(int physicalDriveIndex)
    {
        var letters = new List<string>();

        // A drive with no partition (unformatted, RAW, or fresh from the factory) legitimately
        // has no associated volumes — that's a normal outcome here, not an error condition.
        try
        {
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{physicalDriveIndex}'}} " +
                "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using var logicalDiskSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                    "WHERE AssocClass = Win32_LogicalDiskToPartition");

                foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get())
                {
                    if ((string?)logicalDisk["DeviceID"] is { } letter)
                        letters.Add(letter);
                }
            }
        }
        catch (ManagementException)
        {
            return letters;
        }

        return letters;
    }

    private static (string? FileSystemType, ulong TotalBytes, ulong FreeBytes) GetVolumeSpaceInfo(string? volumeLetter)
    {
        if (volumeLetter is null) return (null, 0, 0);

        try
        {
            var info = new System.IO.DriveInfo(volumeLetter);
            if (!info.IsReady) return (null, 0, 0);

            return (info.DriveFormat, (ulong)info.TotalSize, (ulong)info.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (null, 0, 0);
        }
    }
}
