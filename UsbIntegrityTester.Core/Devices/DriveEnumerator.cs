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

            var volumeLetters = GetAllVolumeLetters(index);
            var (fileSystemType, totalBytes, freeBytes) = GetVolumeSpaceInfo(volumeLetters.FirstOrDefault());

            drives.Add(new DriveInfo
            {
                PhysicalDriveIndex = index,
                Model = model,
                SerialNumber = serial,
                ReportedCapacityBytes = capacity,
                InterfaceType = "USB",
                IsRemovable = true,
                VolumeLetter = volumeLetters.FirstOrDefault(),
                VolumeLetters = volumeLetters,
                FileSystemType = fileSystemType,
                TotalVolumeBytes = totalBytes,
                AvailableFreeBytes = freeBytes,
            });
        }

        return drives;
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
