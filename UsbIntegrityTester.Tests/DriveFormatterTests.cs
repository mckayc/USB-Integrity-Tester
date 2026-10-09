using UsbIntegrityTester.Core.Devices;

namespace UsbIntegrityTester.Tests;

/// <summary>Formatting itself needs Administrator and a real drive, so these check the script that does it.</summary>
public class DriveFormatterTests
{
    private const ulong OneTerabyte = 1_000_000_000_000;

    [Fact]
    public void Script_RefusesNonUsbAndSystemDisks_BeforeTouchingAnything()
    {
        var script = DriveFormatter.BuildScript(4, OneTerabyte, DriveFileSystem.ExFat);

        var clearIndex = script.IndexOf("Clear-Disk", StringComparison.Ordinal);
        Assert.True(script.IndexOf("BusType -ne 'USB'", StringComparison.Ordinal) is >= 0 and var bus && bus < clearIndex);
        Assert.True(script.IndexOf("IsBoot -or $disk.IsSystem", StringComparison.Ordinal) is >= 0 and var sys && sys < clearIndex);
        Assert.Contains("Get-Disk -Number 4", script);
    }

    [Theory]
    [InlineData(DriveFileSystem.ExFat, "-FileSystem exFAT", "-MbrType IFS")]
    [InlineData(DriveFileSystem.Ntfs, "-FileSystem NTFS", "-MbrType IFS")]
    [InlineData(DriveFileSystem.Fat32, "-FileSystem FAT32", "-MbrType FAT32")]
    public void Script_FormatsAsRequested_WithMatchingMbrType(DriveFileSystem fileSystem, string expectedFormat, string expectedMbrType)
    {
        var script = DriveFormatter.BuildScript(2, 16_000_000_000, fileSystem);

        Assert.Contains(expectedFormat, script);
        Assert.Contains("-PartitionStyle MBR", script);
        Assert.Contains(expectedMbrType, script);
    }

    [Fact]
    public void Script_UsesGpt_OnlyAboveMbrsTwoTerabyteLimit()
    {
        Assert.Contains("-PartitionStyle MBR", DriveFormatter.BuildScript(2, 2 * OneTerabyte, DriveFileSystem.ExFat));
        Assert.Contains("-PartitionStyle GPT", DriveFormatter.BuildScript(2, 4 * OneTerabyte, DriveFileSystem.ExFat));
    }

    [Fact]
    public void Fat32_IsOnlyOffered_UpTo32Gigabytes()
    {
        Assert.True(DriveFileSystem.Fat32.Supports(32_000_000_000));
        Assert.False(DriveFileSystem.Fat32.Supports(64_000_000_000));
        Assert.True(DriveFileSystem.ExFat.Supports(4 * OneTerabyte));
    }
}
