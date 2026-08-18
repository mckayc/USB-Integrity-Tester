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

    public string DevicePath => $@"\\.\PhysicalDrive{PhysicalDriveIndex}";
}
