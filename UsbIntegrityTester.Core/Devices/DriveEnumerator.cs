using System.Management;

namespace UsbIntegrityTester.Core.Devices;

/// <summary>Finds removable USB disks currently attached to the system via WMI.</summary>
public static class DriveEnumerator
{
    public static IReadOnlyList<DriveInfo> GetRemovableUsbDrives()
    {
        var drives = new List<DriveInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB'");

        foreach (ManagementObject disk in searcher.Get())
        {
            var deviceId = (string?)disk["DeviceID"] ?? string.Empty;
            var indexMatch = System.Text.RegularExpressions.Regex.Match(deviceId, @"PHYSICALDRIVE(\d+)");
            if (!indexMatch.Success) continue;

            var index = int.Parse(indexMatch.Groups[1].Value);
            var capacity = disk["Size"] is not null ? Convert.ToUInt64(disk["Size"]) : 0UL;
            var serial = ((string?)disk["SerialNumber"] ?? "UNKNOWN").Trim();
            var model = ((string?)disk["Model"] ?? "Unknown Drive").Trim();

            drives.Add(new DriveInfo
            {
                PhysicalDriveIndex = index,
                Model = model,
                SerialNumber = serial,
                ReportedCapacityBytes = capacity,
                InterfaceType = "USB",
                IsRemovable = true,
                VolumeLetter = GetFirstVolumeLetter(index),
            });
        }

        return drives;
    }

    private static string? GetFirstVolumeLetter(int physicalDriveIndex)
    {
        using var partitionSearcher = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{physicalDriveIndex}'}} " +
            "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

        foreach (ManagementObject partition in partitionSearcher.Get())
        {
            using var logicalDiskSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get())
            {
                return (string?)logicalDisk["DeviceID"];
            }
        }

        return null;
    }
}
