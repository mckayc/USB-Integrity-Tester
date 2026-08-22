using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsbIntegrityTester.Core.Devices;
using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.App.Services;

/// <summary>
/// Everything the user configures on the Claimed Stats/USB Connections/Testing tabs, persisted to
/// disk so a new session starts from the previous one's settings instead of the defaults every
/// time. Deliberately excludes drive-specific fields (claimed capacity, drive label) — those are
/// auto-filled from whichever physical drive gets selected on the next launch, and restoring a
/// stale value from a different drive would be actively misleading rather than convenient.
/// </summary>
public sealed record UserSettings
{
    public double ClaimedReadSpeedMegabytesPerSecond { get; init; } = 100;
    public double ClaimedWriteSpeedMegabytesPerSecond { get; init; } = 30;
    public UsbLinkSpeed? ManualLinkSpeedOverride { get; init; }

    public TestMode TestMode { get; init; } = TestMode.File;
    public bool ClearExistingData { get; init; } = true;
    public TestCleanupPolicy CleanupPolicy { get; init; } = TestCleanupPolicy.DeleteAfterTest;

    public bool RunCapacityTest { get; init; } = true;
    public bool RunSpeedTest { get; init; } = true;
    public CapacityScanDepth CapacityScanDepth { get; init; } = CapacityScanDepth.Quick;

    public bool SpeedTestSlot1Enabled { get; init; } = true;
    public SpeedTestCategory SpeedTestSlot1Category { get; init; } = SpeedTestCategory.Seq1MQ8T1;
    public bool SpeedTestSlot2Enabled { get; init; } = true;
    public SpeedTestCategory SpeedTestSlot2Category { get; init; } = SpeedTestCategory.Seq1MQ1T1;
    public bool SpeedTestSlot3Enabled { get; init; } = true;
    public SpeedTestCategory SpeedTestSlot3Category { get; init; } = SpeedTestCategory.Rnd4KQ32T1;
    public bool SpeedTestSlot4Enabled { get; init; } = true;
    public SpeedTestCategory SpeedTestSlot4Category { get; init; } = SpeedTestCategory.Rnd4KQ1T1;

    public bool AutoAdvanceTests { get; init; } = true;
}

/// <summary>Loads/saves <see cref="UserSettings"/> as indented, human-readable JSON (enums as
/// their names, not raw ints) so the file is easy to inspect or hand-edit if something goes wrong.</summary>
public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static UserSettings Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new UserSettings();
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<UserSettings>(json, Options) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing/corrupt/unreadable settings file just means "start from defaults" — never
            // worth blocking startup over.
            return new UserSettings();
        }
    }

    public static void Save(string filePath, UserSettings settings)
    {
        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — losing settings on the way out isn't worth crashing the app over.
        }
    }
}
