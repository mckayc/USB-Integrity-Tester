namespace UsbIntegrityTester.Core.Devices;

/// <summary>A physical, removable USB disk as seen by Windows.</summary>
public sealed record DriveInfo
{
    public required int PhysicalDriveIndex { get; init; }
    public required string Model { get; init; }
    public required string SerialNumber { get; init; }
    public required ulong ReportedCapacityBytes { get; init; }
    public required string InterfaceType { get; init; }
    public required bool IsRemovable { get; init; }
    public string? VolumeLetter { get; init; }
    public IReadOnlyList<string> VolumeLetters { get; init; } = Array.Empty<string>();

    /// <summary>e.g. "NTFS", "exFAT", "FAT32" — null if there's no mounted/formatted volume to read this from.</summary>
    public string? FileSystemType { get; init; }

    public ulong TotalVolumeBytes { get; init; }
    public ulong AvailableFreeBytes { get; init; }

    public string DevicePath => $@"\\.\PhysicalDrive{PhysicalDriveIndex}";
}
