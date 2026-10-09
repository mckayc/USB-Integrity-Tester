using System.Diagnostics;
using System.Text;

namespace UsbIntegrityTester.Core.Devices;

/// <summary>File systems the app can format a drive as before (File Mode) or after (Raw Mode) a test.</summary>
public enum DriveFileSystem
{
    ExFat,
    Ntfs,
    Fat32,
}

/// <summary>
/// Wipes a USB disk's partition table and gives it one fresh, full-size partition with a new file
/// system. Starting from the partition table rather than reformatting an existing volume means it
/// also works on a drive with no volume at all — e.g. right after a Raw Mode test has overwritten
/// every sector — and on drives that shipped with extra partitions. Uses Windows' own Storage
/// cmdlets (the same ones Disk Management sits on top of) rather than hand-rolling it.
/// Requires Administrator.
/// </summary>
public static class DriveFormatter
{
    /// <summary>Windows' built-in formatter refuses FAT32 on anything larger than 32 GB, by design.</summary>
    public const ulong MaxFat32Bytes = 32UL * 1024 * 1024 * 1024;

    /// <summary>MBR can't address past 2 TiB; below that it's preferred for compatibility with TVs, cars, cameras and other non-PC devices.</summary>
    private const ulong MaxMbrBytes = 2UL * 1024 * 1024 * 1024 * 1024;

    private const string VolumeLabel = "USBTEST";

    public static string DisplayName(this DriveFileSystem fileSystem) => fileSystem switch
    {
        DriveFileSystem.ExFat => "exFAT",
        DriveFileSystem.Ntfs => "NTFS",
        DriveFileSystem.Fat32 => "FAT32",
        _ => fileSystem.ToString(),
    };

    public static bool Supports(this DriveFileSystem fileSystem, ulong driveSizeBytes) =>
        fileSystem != DriveFileSystem.Fat32 || driveSizeBytes <= MaxFat32Bytes;

    /// <summary>Erases <paramref name="drive"/> entirely and formats it as <paramref name="fileSystem"/>.</summary>
    /// <returns>The new volume's letter, e.g. "G:".</returns>
    public static Task<string> RepartitionAndFormatAsync(DriveInfo drive, DriveFileSystem fileSystem, Action<string>? log = null) => Task.Run(async () =>
    {
        if (!fileSystem.Supports(drive.ReportedCapacityBytes))
            throw new InvalidOperationException($"Windows can only format drives up to 32 GB as FAT32 — this one is {drive.ReportedCapacityBytes / 1_000_000_000.0:N0} GB.");

        // Disk numbers get reused: if this drive was unplugged since it was enumerated, the same
        // number could now belong to a different one. Re-check right before doing anything
        // irreversible that it's still the same physical drive (by serial number) at that number.
        var current = DriveEnumerator.GetRemovableUsbDrives()
            .FirstOrDefault(d => d.PhysicalDriveIndex == drive.PhysicalDriveIndex);
        if (current is null || current.SerialNumber != drive.SerialNumber)
            throw new InvalidOperationException("The drive changed since it was selected (unplugged or swapped?) — refresh the drive list and try again. Nothing was formatted.");

        log?.Invoke($"Re-partitioning disk {drive.PhysicalDriveIndex} and formatting it as {fileSystem.DisplayName()}…");
        var output = await RunPowerShellAsync(BuildScript(drive.PhysicalDriveIndex, drive.ReportedCapacityBytes, fileSystem));

        var letter = output.Trim();
        if (letter.Length != 1 || !char.IsLetter(letter[0]))
            throw new IOException($"Formatting finished but Windows didn't report a drive letter for the new volume (got \"{letter}\"). Check Disk Management.");

        var volume = letter.ToUpperInvariant() + ":";

        // The letter is assigned a moment before the volume is actually ready to use.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (new System.IO.DriveInfo(volume).IsReady) break;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
            }

            await Task.Delay(250);
        }

        log?.Invoke($"Formatted as {fileSystem.DisplayName()} — new volume is {volume}.");
        return volume;
    });

    internal static string BuildScript(int diskNumber, ulong driveSizeBytes, DriveFileSystem fileSystem)
    {
        var partitionStyle = driveSizeBytes > MaxMbrBytes ? "GPT" : "MBR";
        var fileSystemName = fileSystem switch
        {
            DriveFileSystem.ExFat => "exFAT",
            DriveFileSystem.Ntfs => "NTFS",
            DriveFileSystem.Fat32 => "FAT32",
            _ => throw new ArgumentOutOfRangeException(nameof(fileSystem)),
        };

        // The MBR partition type byte is a hint other devices use to decide what's on a partition —
        // FAT32 gets its own, everything else the generic "installable file system" type.
        var mbrType = fileSystem == DriveFileSystem.Fat32 ? "FAT32" : "IFS";
        var newPartitionArgs = partitionStyle == "MBR" ? $"-MbrType {mbrType}" : string.Empty;

        // Safety checks live in the script too, not just in the C# caller: they run against the
        // disk at the exact moment it's about to be wiped, by Windows' own view of it.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $disk = Get-Disk -Number {{diskNumber}}
            if ($disk.BusType -ne 'USB') { throw "Disk {{diskNumber}} is not a USB disk (bus type: $($disk.BusType)) - refusing to format it." }
            if ($disk.IsBoot -or $disk.IsSystem) { throw "Disk {{diskNumber}} is a boot/system disk - refusing to format it." }
            if ($disk.IsReadOnly) { throw "Disk {{diskNumber}} is read-only (write-protect switch, or the drive has locked itself) - can't format it." }
            if ($disk.IsOffline) { Set-Disk -Number {{diskNumber}} -IsOffline $false }
            if ($disk.PartitionStyle -ne 'RAW') { Clear-Disk -Number {{diskNumber}} -RemoveData -RemoveOEM -Confirm:$false }
            Initialize-Disk -Number {{diskNumber}} -PartitionStyle {{partitionStyle}}
            $partition = New-Partition -DiskNumber {{diskNumber}} -UseMaximumSize {{newPartitionArgs}}
            $null = Format-Volume -Partition $partition -FileSystem {{fileSystemName}} -NewFileSystemLabel '{{VolumeLabel}}' -Confirm:$false -Force
            $partition = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber $partition.PartitionNumber
            if (-not $partition.DriveLetter -or $partition.DriveLetter -eq [char]0) {
                Add-PartitionAccessPath -DiskNumber {{diskNumber}} -PartitionNumber $partition.PartitionNumber -AssignDriveLetter
                $partition = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber $partition.PartitionNumber
            }
            Write-Output $partition.DriveLetter
            """;
    }

    private static async Task<string> RunPowerShellAsync(string script)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        // Encoded so the script never needs command-line quoting.
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Couldn't start PowerShell to format the drive.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !l.StartsWith("At line", StringComparison.Ordinal) && !l.StartsWith("+", StringComparison.Ordinal))
                ?? $"PowerShell exited with code {process.ExitCode}";
            throw new IOException($"Formatting failed: {reason}");
        }

        return stdout;
    }
}
