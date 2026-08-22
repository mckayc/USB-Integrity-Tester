using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbIntegrityTester.App.Logging;
using UsbIntegrityTester.App.Services;
using UsbIntegrityTester.Core.Devices;
using UsbIntegrityTester.Core.History;
using UsbIntegrityTester.Core.Reporting;
using UsbIntegrityTester.Core.Testing;
using UsbIntegrityTester.Core.Verdict;
using DriveInfo = UsbIntegrityTester.Core.Devices.DriveInfo;

namespace UsbIntegrityTester.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbIntegrityTester");

    private readonly DriveHistoryStore _historyStore;
    private CancellationTokenSource? _testCancellation;

    // Raw progress arrives many times per second during a test — far faster than a human can read.
    // A timer flushes the latest sample to the UI on a fixed cadence instead, and an exponential
    // moving average smooths the displayed number so it doesn't jitter between flushes.
    private readonly DispatcherTimer _liveUpdateTimer;
    private Stopwatch? _capacityStopwatch;
    private double? _capacityEtaSmoothedSeconds;
    private Stopwatch? _overallTestStopwatch;

    public TestTrackViewModel CapacityTrack { get; } =
        new("Capacity Test", "Write", "Verify", ThroughputPalette.CapacityNeutral, ThroughputPalette.CapacityNeutralFill, usesCapacityLayout: true);
    public TestTrackViewModel Slot1Track { get; } =
        new(SpeedTestCategoryOption.All[0].DisplayName, "Write", "Read", ThroughputPalette.Slot1Neutral, ThroughputPalette.Slot1NeutralFill);
    public TestTrackViewModel Slot2Track { get; } =
        new(SpeedTestCategoryOption.All[0].DisplayName, "Write", "Read", ThroughputPalette.Slot2Neutral, ThroughputPalette.Slot2NeutralFill);
    public TestTrackViewModel Slot3Track { get; } =
        new(SpeedTestCategoryOption.All[0].DisplayName, "Write", "Read", ThroughputPalette.Slot3Neutral, ThroughputPalette.Slot3NeutralFill);
    public TestTrackViewModel Slot4Track { get; } =
        new(SpeedTestCategoryOption.All[0].DisplayName, "Write", "Read", ThroughputPalette.Slot4Neutral, ThroughputPalette.Slot4NeutralFill);

    /// <summary>All five tracks in a fixed order — Capacity, then the 4 speed test slots — always.
    /// Cards used to reorder to put whichever test was running at the top, but that meant the whole
    /// layout reshuffled every time a test finished, which was disorienting; the accent border and
    /// pulsing dot on the active card already say "this one's running" without moving anything.</summary>
    public IReadOnlyList<TestTrackViewModel> AllTracks => new[] { CapacityTrack, Slot1Track, Slot2Track, Slot3Track, Slot4Track };

    private readonly string _settingsFilePath;

    public MainViewModel()
    {
        Directory.CreateDirectory(AppDataDir);
        _historyStore = new DriveHistoryStore(Path.Combine(AppDataDir, "history.db"));
        _settingsFilePath = Path.Combine(AppDataDir, "settings.json");

        ApplyUserSettings(UserSettingsStore.Load(_settingsFilePath));

        _liveUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _liveUpdateTimer.Tick += (_, _) => FlushLiveProgress();

        // Field initializers set RunCapacityTest etc. directly without going through the property
        // setter, so the OnXChanged partials above never fire for these defaults — sync once here.
        CapacityTrack.IsSelected = RunCapacityTest;
        SyncSpeedTrackSelection();

        RefreshDrives();
        RefreshHistory();
    }

    private void ApplyUserSettings(UserSettings s)
    {
        ClaimedReadSpeedMegabytesPerSecond = s.ClaimedReadSpeedMegabytesPerSecond;
        ClaimedWriteSpeedMegabytesPerSecond = s.ClaimedWriteSpeedMegabytesPerSecond;
        ManualLinkSpeedOverride = s.ManualLinkSpeedOverride;

        TestMode = s.TestMode;
        ClearExistingData = s.ClearExistingData;
        CleanupPolicy = s.CleanupPolicy;

        RunCapacityTest = s.RunCapacityTest;
        RunSpeedTest = s.RunSpeedTest;
        CapacityScanDepth = s.CapacityScanDepth;

        SpeedTestSlot1Enabled = s.SpeedTestSlot1Enabled;
        SpeedTestSlot1Category = s.SpeedTestSlot1Category;
        SpeedTestSlot2Enabled = s.SpeedTestSlot2Enabled;
        SpeedTestSlot2Category = s.SpeedTestSlot2Category;
        SpeedTestSlot3Enabled = s.SpeedTestSlot3Enabled;
        SpeedTestSlot3Category = s.SpeedTestSlot3Category;
        SpeedTestSlot4Enabled = s.SpeedTestSlot4Enabled;
        SpeedTestSlot4Category = s.SpeedTestSlot4Category;

        AutoAdvanceTests = s.AutoAdvanceTests;
    }

    /// <summary>Called on clean app shutdown (see MainWindow's Closing handler) — persists whatever
    /// the user currently has configured so the next launch picks up where this session left off.</summary>
    public void SaveSettings()
    {
        var settings = new UserSettings
        {
            ClaimedReadSpeedMegabytesPerSecond = ClaimedReadSpeedMegabytesPerSecond,
            ClaimedWriteSpeedMegabytesPerSecond = ClaimedWriteSpeedMegabytesPerSecond,
            ManualLinkSpeedOverride = ManualLinkSpeedOverride,

            TestMode = TestMode,
            ClearExistingData = ClearExistingData,
            CleanupPolicy = CleanupPolicy,

            RunCapacityTest = RunCapacityTest,
            RunSpeedTest = RunSpeedTest,
            CapacityScanDepth = CapacityScanDepth,

            SpeedTestSlot1Enabled = SpeedTestSlot1Enabled,
            SpeedTestSlot1Category = SpeedTestSlot1Category,
            SpeedTestSlot2Enabled = SpeedTestSlot2Enabled,
            SpeedTestSlot2Category = SpeedTestSlot2Category,
            SpeedTestSlot3Enabled = SpeedTestSlot3Enabled,
            SpeedTestSlot3Category = SpeedTestSlot3Category,
            SpeedTestSlot4Enabled = SpeedTestSlot4Enabled,
            SpeedTestSlot4Category = SpeedTestSlot4Category,

            AutoAdvanceTests = AutoAdvanceTests,
        };

        UserSettingsStore.Save(_settingsFilePath, settings);
    }

    // --- Claimed Stats ---
    public ObservableCollection<DriveInfo> AvailableDrives { get; } = new();

    [ObservableProperty] private DriveInfo? _selectedDrive;
    [ObservableProperty] private string _driveLabel = string.Empty;
    [ObservableProperty] private double _claimedCapacityGb = 64;
    [ObservableProperty] private double _claimedReadSpeedMegabytesPerSecond = 100;
    [ObservableProperty] private double _claimedWriteSpeedMegabytesPerSecond = 30;

    partial void OnSelectedDriveChanged(DriveInfo? value)
    {
        if (value is null) return;

        // Suggest the drive's model as the tracking label, but never clobber something the user typed.
        if (string.IsNullOrWhiteSpace(DriveLabel))
            DriveLabel = value.Model;

        // Default the claimed capacity to what Windows already reports — the user can still edit
        // it freely. This isn't proof of anything (a counterfeit drive fakes this too), just a
        // fast starting point; it becomes the value being checked for real when the test runs.
        ClaimedCapacityGb = Math.Round(value.ReportedCapacityBytes / 1_000_000_000.0, 2);
    }

    [RelayCommand]
    private void UseOsReportedCapacity()
    {
        if (SelectedDrive is null) return;
        ClaimedCapacityGb = Math.Round(SelectedDrive.ReportedCapacityBytes / 1_000_000_000.0, 2);
    }

    // --- USB Connections ---
    [ObservableProperty] private string _negotiatedLinkSpeedText = "Select a drive and click Inspect Port.";
    [ObservableProperty] private UsbLinkSpeed? _autoDetectedLinkSpeed;
    [ObservableProperty] private UsbLinkSpeed? _manualLinkSpeedOverride;

    /// <summary>Every speed the dropdown offers, for manually correcting a bad auto-detection.</summary>
    public IReadOnlyList<UsbLinkSpeed> AvailableLinkSpeeds { get; } = Enum.GetValues<UsbLinkSpeed>()
        .Where(s => s != UsbLinkSpeed.Unknown).ToList();

    /// <summary>What actually gets used for the report — the manual override if the user set one, otherwise auto-detection.</summary>
    public UsbLinkSpeed? EffectiveLinkSpeed => ManualLinkSpeedOverride ?? AutoDetectedLinkSpeed;

    partial void OnManualLinkSpeedOverrideChanged(UsbLinkSpeed? value) => OnPropertyChanged(nameof(EffectiveLinkSpeed));

    [ObservableProperty] private string _deviceIdText = "—";
    [ObservableProperty] private string _transportText = "—";
    [ObservableProperty] private string _hubHopsText = "—";

    public string SystemInfoText =>
        $"{Core.Devices.SystemInfo.OperatingSystem} ({Core.Devices.SystemInfo.Architecture}) — {Core.Devices.SystemInfo.MachineName}";

    public string ElevationText => Core.Devices.SystemInfo.IsElevated
        ? "Running as Administrator — raw disk access is available."
        : "NOT running as Administrator — port inspection and destructive tests will fail. Restart the app elevated.";

    public bool IsElevated => Core.Devices.SystemInfo.IsElevated;

    // --- Testing ---
    [ObservableProperty] private TestMode _testMode = TestMode.File;
    [ObservableProperty] private bool _clearExistingData = true;
    [ObservableProperty] private TestCleanupPolicy _cleanupPolicy = TestCleanupPolicy.DeleteAfterTest;

    /// <summary>Tracks the volume most recently used for a File Mode test, so app-close cleanup knows where to look.</summary>
    private string? _lastFileModeVolumeLetter;

    [RelayCommand]
    private async Task CleanUpTestFilesNowAsync()
    {
        if (SelectedDrive?.VolumeLetter is not { } letter) return;
        // Off the UI thread — a prior test can leave many thousands of files behind (a Full
        // capacity scan especially), and deleting them synchronously here would freeze the window
        // for however long that takes with no way to tell it's still working.
        await Task.Run(() => FileModeEngine.TryDeleteTestFolder(letter));
        AppLog.Info($"Cleaned up test files on {letter}.");
    }

    /// <summary>Called from MainWindow's Closing event — honors "delete on app close" for the last File Mode test.</summary>
    public void CleanupOnAppClose()
    {
        if (CleanupPolicy != TestCleanupPolicy.DeleteOnAppClose) return;
        if (_lastFileModeVolumeLetter is not { } letter) return;
        FileModeEngine.TryDeleteTestFolder(letter);
    }

    [ObservableProperty] private bool _runCapacityTest = true;
    [ObservableProperty] private bool _runSpeedTest = true;
    [ObservableProperty] private CapacityScanDepth _capacityScanDepth = CapacityScanDepth.Quick;

    /// <summary>Every category a speed test slot's dropdown can pick, for binding via SelectedValue/SelectedValuePath.</summary>
    public IReadOnlyList<SpeedTestCategoryOption> SpeedTestCategoryOptions => SpeedTestCategoryOption.All;

    [ObservableProperty] private bool _speedTestSlot1Enabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestSlot1Info))]
    private SpeedTestCategory _speedTestSlot1Category = SpeedTestCategory.Seq1MQ8T1;
    [ObservableProperty] private bool _speedTestSlot2Enabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestSlot2Info))]
    private SpeedTestCategory _speedTestSlot2Category = SpeedTestCategory.Seq1MQ1T1;
    [ObservableProperty] private bool _speedTestSlot3Enabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestSlot3Info))]
    private SpeedTestCategory _speedTestSlot3Category = SpeedTestCategory.Rnd4KQ32T1;
    [ObservableProperty] private bool _speedTestSlot4Enabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestSlot4Info))]
    private SpeedTestCategory _speedTestSlot4Category = SpeedTestCategory.Rnd4KQ1T1;

    /// <summary>The selected category's full description/size-range, for the Setup tab's helper text under each dropdown.</summary>
    public SpeedTestCategoryOption SpeedTestSlot1Info => SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot1Category);
    public SpeedTestCategoryOption SpeedTestSlot2Info => SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot2Category);
    public SpeedTestCategoryOption SpeedTestSlot3Info => SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot3Category);
    public SpeedTestCategoryOption SpeedTestSlot4Info => SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot4Category);

    // Keep each Run Test card's "selected/not selected" state and title live as the user edits the
    // Setup tab, so a card doesn't misleadingly say "Not selected" for a test that's actually
    // checked, or keep showing a category the user just swapped away from.
    partial void OnRunCapacityTestChanged(bool value) => CapacityTrack.IsSelected = value;
    partial void OnRunSpeedTestChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot1EnabledChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot2EnabledChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot3EnabledChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot4EnabledChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot1CategoryChanged(SpeedTestCategory value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot2CategoryChanged(SpeedTestCategory value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot3CategoryChanged(SpeedTestCategory value) => SyncSpeedTrackSelection();
    partial void OnSpeedTestSlot4CategoryChanged(SpeedTestCategory value) => SyncSpeedTrackSelection();

    private void SyncSpeedTrackSelection()
    {
        Slot1Track.IsSelected = RunSpeedTest && SpeedTestSlot1Enabled;
        Slot1Track.Title = SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot1Category).DisplayName;
        Slot2Track.IsSelected = RunSpeedTest && SpeedTestSlot2Enabled;
        Slot2Track.Title = SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot2Category).DisplayName;
        Slot3Track.IsSelected = RunSpeedTest && SpeedTestSlot3Enabled;
        Slot3Track.Title = SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot3Category).DisplayName;
        Slot4Track.IsSelected = RunSpeedTest && SpeedTestSlot4Enabled;
        Slot4Track.Title = SpeedTestCategoryOption.All.First(o => o.Category == SpeedTestSlot4Category).DisplayName;
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private double _progressFraction;

    public double ProgressPercent => ProgressFraction * 100;
    [ObservableProperty] private string _progressText = "Idle.";
    [ObservableProperty] private bool _isTestRunning;
    [ObservableProperty] private string _currentPhaseLabel = "Idle";

    /// <summary>On: tests run straight through, like before. Off: the run pauses after each test
    /// finishes until "Run Next Test" is clicked — meant for recording, where you want to narrate
    /// a result before the next test's numbers start moving.</summary>
    [ObservableProperty] private bool _autoAdvanceTests = true;

    /// <summary>True while a run is paused between tests waiting for "Run Next Test".</summary>
    [ObservableProperty] private bool _isWaitingForNextTest;

    private TaskCompletionSource? _nextTestGate;

    [RelayCommand]
    private void RunNextTest() => _nextTestGate?.TrySetResult();

    /// <summary>Passed into the engines as the pause point between top-level tests. A no-op when
    /// auto-advance is on; otherwise blocks until RunNextTest is clicked or the run is cancelled.</summary>
    private async Task WaitForNextTestIfNeededAsync(CancellationToken cancellationToken)
    {
        if (AutoAdvanceTests) return;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nextTestGate = gate;
        IsWaitingForNextTest = true;
        try
        {
            using var registration = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            await gate.Task;
        }
        finally
        {
            IsWaitingForNextTest = false;
            if (ReferenceEquals(_nextTestGate, gate)) _nextTestGate = null;
        }
    }

    /// <summary>Bumped once per newly-detected failed block during verification — the Testing view watches this to trigger a flash + sound the instant a fake/corrupted block is found.</summary>
    [ObservableProperty] private int _fakeBlockFlashTrigger;

    private int _lastReportedBlocksFailed;

    // --- Report ---
    [ObservableProperty] private ReportModel? _lastReport;
    [ObservableProperty] private string _verdictText = string.Empty;
    [ObservableProperty] private string _verdictExplanationText = string.Empty;
    [ObservableProperty] private ReportCapacitySummary _capacitySummary = ReportCapacitySummary.Empty;
    [ObservableProperty] private ObservableCollection<SpeedTestSlotReportRow> _speedTestRows = new();
    [ObservableProperty] private ObservableCollection<ReportStat> _extraStats = new();

    public ObservableCollection<LogEntry> Logs => AppLog.Entries;

    // --- History ---
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = new();

    [RelayCommand]
    private void RefreshHistory()
    {
        try
        {
            HistoryEntries.Clear();
            foreach (var entry in _historyStore.GetAllHistory())
                HistoryEntries.Add(entry);
        }
        catch (Exception ex)
        {
            AppLog.Error($"RefreshHistory failed: {ex}");
        }
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        try
        {
            AvailableDrives.Clear();
            var foundDrives = DriveEnumerator.GetRemovableUsbDrives(out var diagnostics);
            foreach (var drive in foundDrives)
                AvailableDrives.Add(drive);

            foreach (var line in diagnostics)
                AppLog.Info($"Drive enum: {line}");

            SelectedDrive = AvailableDrives.FirstOrDefault();
            AppLog.Info($"Refreshed drives — found {AvailableDrives.Count}.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"RefreshDrives failed: {ex}");
        }
    }

    [RelayCommand]
    private void InspectPort()
    {
        if (SelectedDrive is null)
        {
            NegotiatedLinkSpeedText = "No drive selected.";
            return;
        }

        try
        {
            AppLog.Info($"Inspecting port for drive {SelectedDrive.PhysicalDriveIndex}…");
            var speed = UsbPortInspector.GetNegotiatedLinkSpeed(
                SelectedDrive.PhysicalDriveIndex, msg => AppLog.Info($"  {msg}"));

            AutoDetectedLinkSpeed = speed;
            NegotiatedLinkSpeedText = speed is null
                ? "Could not determine negotiated link speed automatically — see the Logs tab for why, or set it manually below."
                : $"{speed.Value.DisplayName()} — spec ceiling {speed.Value.MaxTheoreticalMegabytesPerSecond():N0} MB/s";

            var details = UsbPortInspector.GetDeviceDetails(SelectedDrive.PhysicalDriveIndex);
            DeviceIdText = details?.VendorId is not null
                ? $"VID {details.VendorId} / PID {details.ProductId}"
                : "Unknown";
            TransportText = details?.TransportDescription ?? "Unknown";
            HubHopsText = details is null
                ? "Unknown"
                : details.HubHopsFromRoot == 0
                    ? "Plugged directly into a root port"
                    : $"{details.HubHopsFromRoot} hub hop(s) from the root port";
        }
        catch (Exception ex)
        {
            AutoDetectedLinkSpeed = null;
            NegotiatedLinkSpeedText = "Port inspection failed — see Logs tab, or set the speed manually below.";
            AppLog.Error($"InspectPort failed for drive {SelectedDrive.PhysicalDriveIndex}: {ex}");
        }
    }

    [RelayCommand]
    private async Task StartTestAsync()
    {
        if (SelectedDrive is null)
        {
            ProgressText = "No drive selected.";
            return;
        }

        if (TestMode == TestMode.File && SelectedDrive.VolumeLetter is null)
        {
            ProgressText = "File Mode needs a formatted, mounted volume — this drive has none. Try Raw Mode, or format the drive first.";
            return;
        }

        var driveDescription = $"{SelectedDrive.Model}\nSerial: {SelectedDrive.SerialNumber}\n" +
            $"Volume: {SelectedDrive.VolumeLetter ?? "(none)"}\nReported size: {SelectedDrive.ReportedCapacityBytes / 1_000_000_000.0:N1} GB";

        string message;
        if (TestMode == TestMode.Raw)
        {
            message = $"This will ERASE ALL DATA on:\n\n{driveDescription}\n\n" +
                "Raw Mode writes directly to the drive's sectors and dismounts the volume for the duration — nothing will be visible in Explorer until the test finishes.\n\nContinue?";
        }
        else if (ClearExistingData)
        {
            message = $"This will DELETE ALL EXISTING FILES on:\n\n{driveDescription}\n\n" +
                "File Mode writes visible test files to the drive, which stays mounted so you can watch them appear in Explorer.\n\nContinue?";
        }
        else
        {
            var freeGb = SelectedDrive.AvailableFreeBytes / 1_000_000_000.0;
            var totalGb = SelectedDrive.TotalVolumeBytes / 1_000_000_000.0;
            message = $"Testing without clearing existing data on:\n\n{driveDescription}\n\n" +
                $"⚠ This only tests the {freeGb:N1} GB currently free (out of {totalGb:N1} GB total) — it CANNOT detect fake capacity beyond what's free, and cannot verify space already used by your existing files. Your existing files will not be touched.\n\nContinue?";
        }

        var confirmed = await DialogService.ConfirmAsync(
            TestMode == TestMode.Raw || ClearExistingData ? "Confirm destructive test" : "Confirm test (existing data preserved)",
            message);

        if (!confirmed) return;

        IsTestRunning = true;
        _testCancellation = new CancellationTokenSource();
        AppLog.Info($"Starting {TestMode} Mode test on {SelectedDrive.Model} (Serial {SelectedDrive.SerialNumber}), capacity scan depth {CapacityScanDepth}.");

        try
        {
            await RunTestAsync(SelectedDrive, _testCancellation.Token);
            AppLog.Info("Test completed.");
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled.";
            AppLog.Warning("Test cancelled by user.");
        }
        catch (Win32IOException ex) when (ex.ErrorCode == 5)
        {
            ProgressText = "Access denied — restart the app as Administrator.";
            AppLog.Error($"Test failed (access denied — raw disk access requires Administrator): {ex}");
        }
        catch (Exception ex)
        {
            ProgressText = $"Test failed: {ex.Message}";
            AppLog.Error($"Test failed: {ex}");
        }
        finally
        {
            _liveUpdateTimer.Stop();
            FlushLiveProgress();
            foreach (var track in AllTracks) track.MarkComplete();
            IsTestRunning = false;
            CurrentPhaseLabel = "Idle";
        }
    }

    /// <summary>Which card a given test phase belongs to.</summary>
    private TestTrackViewModel? TrackForPhase(TestPhase phase) => phase switch
    {
        TestPhase.WritingCapacityPattern or TestPhase.VerifyingCapacityPattern => CapacityTrack,
        TestPhase.MeasuringSpeedTestSlot1WriteSpeed or TestPhase.MeasuringSpeedTestSlot1ReadSpeed => Slot1Track,
        TestPhase.MeasuringSpeedTestSlot2WriteSpeed or TestPhase.MeasuringSpeedTestSlot2ReadSpeed => Slot2Track,
        TestPhase.MeasuringSpeedTestSlot3WriteSpeed or TestPhase.MeasuringSpeedTestSlot3ReadSpeed => Slot3Track,
        TestPhase.MeasuringSpeedTestSlot4WriteSpeed or TestPhase.MeasuringSpeedTestSlot4ReadSpeed => Slot4Track,
        _ => null,
    };

    private static string SubPhaseStatusText(TestPhase phase) => phase switch
    {
        TestPhase.WritingCapacityPattern => "Writing test pattern…",
        TestPhase.VerifyingCapacityPattern => "Verifying blocks…",
        TestPhase.MeasuringSpeedTestSlot1WriteSpeed or TestPhase.MeasuringSpeedTestSlot2WriteSpeed
            or TestPhase.MeasuringSpeedTestSlot3WriteSpeed or TestPhase.MeasuringSpeedTestSlot4WriteSpeed => "Writing…",
        TestPhase.MeasuringSpeedTestSlot1ReadSpeed or TestPhase.MeasuringSpeedTestSlot2ReadSpeed
            or TestPhase.MeasuringSpeedTestSlot3ReadSpeed or TestPhase.MeasuringSpeedTestSlot4ReadSpeed => "Reading…",
        _ => string.Empty,
    };

    /// <summary>Every tracked phase is either the "Write" half (WritingCapacityPattern or a *Write* speed phase) or the "Read"/"Verify" half.</summary>
    private static bool IsPrimaryChannelPhase(TestPhase phase) => phase is TestPhase.WritingCapacityPattern
        or TestPhase.MeasuringSpeedTestSlot1WriteSpeed or TestPhase.MeasuringSpeedTestSlot2WriteSpeed
        or TestPhase.MeasuringSpeedTestSlot3WriteSpeed or TestPhase.MeasuringSpeedTestSlot4WriteSpeed;

    private void FlushLiveProgress()
    {
        foreach (var track in AllTracks)
        {
            if (track.PendingProgress is not { } p) continue;
            track.PendingProgress = null;
            FlushTrack(track, p);
        }

        UpdateOverallProgress();
    }

    private void FlushTrack(TestTrackViewModel track, TestProgress p)
    {
        track.IsRunning = true;
        track.StatusText = SubPhaseStatusText(p.Phase);

        var isPrimaryChannel = IsPrimaryChannelPhase(p.Phase);
        var channel = isPrimaryChannel ? track.Primary : track.Secondary;

        // A sub-phase change (write -> verify, or write -> read) starts a fresh measurement on
        // that channel — carrying over the previous sub-phase's smoothing/peak/average would
        // misrepresent the new one's numbers. The *other* channel's numbers are left alone, so
        // e.g. the write results stay visible once reading starts instead of being wiped.
        if (track.LastPhase != p.Phase)
        {
            track.LastPhase = p.Phase;
            channel.ResetSmoothing();

            // Write and verify usually run at noticeably different speeds (verify is a pure read;
            // write includes filesystem/controller overhead write doesn't), so the ETA's own clock
            // and smoothing reset here too — otherwise the estimate right after this handoff is
            // still dragged around by the *other* pass's rate until enough of the new pass elapses
            // to outweigh it.
            if (track == CapacityTrack)
            {
                _capacityStopwatch = null;
                _capacityEtaSmoothedSeconds = null;
            }
        }

        if (track == CapacityTrack)
        {
            // Computed below from GB written/verified — the capacity engine's own FractionComplete
            // spans both passes combined, not this channel alone.
        }
        else
        {
            // Large/Small/Huge tracks run two equal-weighted sub-phases (write then read); each
            // one's own FractionComplete is already scoped to just that sub-phase.
            channel.ProgressFraction = p.FractionComplete;
            track.ProgressFraction = ((isPrimaryChannel ? 0 : 1) + p.FractionComplete) / 2;
        }

        if (p.CurrentThroughputMegabytesPerSecond is { } mbps)
        {
            var displayMbps = channel.Smooth(mbps);

            channel.CurrentThroughputText = $"{displayMbps:N1} MB/s";
            channel.PeakThroughputText = $"Peak {channel.PeakMbps:N1} MB/s";
            channel.AverageThroughputText = $"Avg {channel.AverageMbps:N1} MB/s";

            channel.LivePoints.Add(displayMbps);
            if (channel.LivePoints.Count > 150) channel.LivePoints.RemoveAt(0);

            // Whether this channel is meeting its claim is shown as a small badge, not by
            // recoloring the line itself — the line's color is that channel's fixed identity
            // (write vs. read), so two very different numbers never render as the same color just
            // because both happen to fall short of their claims. Uses the smoothed value so the
            // badge doesn't flicker on every momentary dip/spike. The Capacity track has no speed
            // claim to compare against.
            if (track != CapacityTrack)
            {
                var claimForPhase = isPrimaryChannel ? ClaimedWriteSpeedMegabytesPerSecond : ClaimedReadSpeedMegabytesPerSecond;
                if (claimForPhase <= 0)
                {
                    channel.ClaimStatusGlyph = string.Empty;
                }
                else
                {
                    var isMeetingClaim = displayMbps >= claimForPhase;
                    channel.ClaimStatusGlyph = isMeetingClaim ? "✓" : "▼"; // ✓ or ▼
                    channel.ClaimStatusBrush = isMeetingClaim ? ThroughputPalette.Good : ThroughputPalette.Bad;
                }
            }
        }

        if (track == CapacityTrack && p.Phase is TestPhase.WritingCapacityPattern or TestPhase.VerifyingCapacityPattern)
        {
            _capacityStopwatch ??= Stopwatch.StartNew();

            var isWriting = p.Phase == TestPhase.WritingCapacityPattern;
            var totalPhaseBytes = p.TotalBytes / 2;
            var phaseBytesDone = isWriting
                ? p.BytesProcessed
                : (p.BytesProcessed > totalPhaseBytes ? p.BytesProcessed - totalPhaseBytes : 0);

            var phaseFraction = totalPhaseBytes == 0 ? 0 : (double)phaseBytesDone / totalPhaseBytes;
            channel.ProgressFraction = phaseFraction;
            // The capacity engine's own FractionComplete already spans both passes, so it doubles
            // nicely as this track's overall (write + verify) progress.
            track.ProgressFraction = p.FractionComplete;

            var etaText = string.Empty;
            if (phaseFraction > 0.02) // wait for a little real signal — a ratio this early is mostly noise
            {
                // Uses only THIS pass's own elapsed time and fraction — not the combined
                // write+verify fraction — so a fast write pass doesn't drag out verify's estimate
                // or vice versa. Smoothed with an EMA since a raw elapsed/fraction ratio swings
                // wildly block-to-block, especially in the first second of a freshly-reset phase.
                var rawRemainingSeconds = _capacityStopwatch.Elapsed.TotalSeconds * (1 - phaseFraction) / phaseFraction;
                _capacityEtaSmoothedSeconds = _capacityEtaSmoothedSeconds is { } prevEta
                    ? prevEta + (rawRemainingSeconds - prevEta) * 0.25
                    : rawRemainingSeconds;
                etaText = FormatEta(_capacityEtaSmoothedSeconds.Value);
            }

            channel.DetailText = $"{phaseBytesDone / 1_000_000_000.0:N2} GB of {totalPhaseBytes / 1_000_000_000.0:N2} GB {(isWriting ? "written" : "verified")}"
                + (string.IsNullOrEmpty(etaText) ? "" : $" — {etaText}");

            if (p.BlocksFailedSoFar > 0)
                track.FailedBlocksText = $"⚠ {p.BlocksFailedSoFar} block(s) failed verification";
        }

        ProgressText = $"{CurrentPhaseLabel} — {channel.CurrentThroughputText}";
    }

    private void UpdateOverallProgress()
    {
        var selected = AllTracks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ProgressFraction = 0;
            return;
        }

        var completed = selected.Count(t => t.IsComplete);
        var runningFraction = selected.FirstOrDefault(t => t.IsRunning)?.ProgressFraction ?? 0;
        ProgressFraction = (completed + runningFraction) / selected.Count;
    }

    private static string FormatEta(double remainingSeconds)
    {
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) || remainingSeconds < 0)
            return string.Empty;

        var span = TimeSpan.FromSeconds(remainingSeconds);
        if (span.TotalHours >= 1) return $"~{(int)span.TotalHours}h {span.Minutes}m remaining";
        if (span.TotalMinutes >= 1) return $"~{(int)span.TotalMinutes}m {span.Seconds}s remaining";
        return $"~{Math.Max(1, span.Seconds)}s remaining";
    }

    private string PhaseLabel(TestPhase phase) => phase switch
    {
        TestPhase.WritingCapacityPattern => "Writing unique data to every tested block, to prove each address is real storage",
        TestPhase.VerifyingCapacityPattern => "Reading each block back to confirm it holds its own data, not a copy",
        TestPhase.MeasuringSpeedTestSlot1WriteSpeed or TestPhase.MeasuringSpeedTestSlot2WriteSpeed
            or TestPhase.MeasuringSpeedTestSlot3WriteSpeed or TestPhase.MeasuringSpeedTestSlot4WriteSpeed
            => $"Writing — {TrackForPhase(phase)?.Title ?? "speed test"}",
        TestPhase.MeasuringSpeedTestSlot1ReadSpeed or TestPhase.MeasuringSpeedTestSlot2ReadSpeed
            or TestPhase.MeasuringSpeedTestSlot3ReadSpeed or TestPhase.MeasuringSpeedTestSlot4ReadSpeed
            => $"Reading — {TrackForPhase(phase)?.Title ?? "speed test"}",
        TestPhase.FlushingSpeedTestCache => "Writing throwaway data so the read tests above don't come back from the drive's own cache",
        TestPhase.Complete => "Complete",
        _ => phase.ToString(),
    };

    [RelayCommand]
    private void CancelTest() => _testCancellation?.Cancel();

    [ObservableProperty] private string _copyLogsButtonText = "\U0001F4CB Copy All";

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var text = string.Join(Environment.NewLine, Logs.Select(l => l.ToString()));
        if (string.IsNullOrEmpty(text)) text = "(no log entries)";

        var succeeded = TrySetClipboardText(text);
        CopyLogsButtonText = succeeded ? "✅ Copied!" : "⚠ Copy failed — try again";
        await Task.Delay(1500);
        CopyLogsButtonText = "\U0001F4CB Copy All";
    }

    [ObservableProperty] private string _copyReportButtonText = "\U0001F4CB Copy as Text";

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        var text = BuildReportText();
        var succeeded = TrySetClipboardText(text);
        CopyReportButtonText = succeeded ? "✅ Copied!" : "⚠ Copy failed — try again";
        await Task.Delay(1500);
        CopyReportButtonText = "\U0001F4CB Copy as Text";
    }

    [RelayCommand]
    private void SaveReportText()
    {
        if (LastReport is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text file (*.txt)|*.txt",
            FileName = $"usb-integrity-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildReportText());
            AppLog.Info($"Report saved to \"{dialog.FileName}\".");
        }
        catch (Exception ex)
        {
            AppLog.Error($"SaveReportText failed: {ex}");
        }
    }

    /// <summary>Plain-text rendering of the Report page — same figures shown on screen, formatted so
    /// it reads cleanly pasted into an email, ticket, or chat message.</summary>
    private string BuildReportText()
    {
        if (LastReport is not { } report) return "(no test run yet)";

        var sb = new StringBuilder();
        sb.AppendLine("USB Integrity Tester — Report");
        sb.AppendLine($"Drive: {report.DriveLabel}");
        sb.AppendLine($"Tested: {report.TimestampUtc:g} UTC");
        sb.AppendLine();

        sb.AppendLine($"Verdict: {VerdictText}");
        sb.AppendLine(VerdictExplanationText);
        sb.AppendLine();

        sb.AppendLine("Capacity");
        sb.AppendLine($"  {CapacitySummary.HeadlineText}");
        if (!string.IsNullOrEmpty(CapacitySummary.DetailText))
            sb.AppendLine($"  {CapacitySummary.DetailText}");
        if (!string.IsNullOrEmpty(CapacitySummary.ScanThoroughnessText))
            sb.AppendLine($"  {CapacitySummary.ScanThoroughnessText}");
        sb.AppendLine();

        sb.AppendLine("Speed Tests");
        foreach (var row in SpeedTestRows)
        {
            sb.AppendLine($"  {row.CategoryName} — {row.WorkloadDescription} ({row.SizeRangeText})");
            sb.AppendLine($"    Write: {row.WriteAvgText} ({row.WritePeakText}{(string.IsNullOrEmpty(row.WriteClaimText) ? "" : $", {row.WriteClaimText}")})");
            sb.AppendLine($"    Read:  {row.ReadAvgText} ({row.ReadPeakText}{(string.IsNullOrEmpty(row.ReadClaimText) ? "" : $", {row.ReadClaimText}")})");
            sb.AppendLine($"    {row.PracticalTimeText}");
        }
        sb.AppendLine();

        sb.AppendLine("Details");
        sb.AppendLine($"  Serial Number: {report.SerialNumber}");
        sb.AppendLine($"  Claimed Capacity: {report.ClaimedCapacityBytes / 1_000_000_000.0:N1} GB");
        foreach (var stat in ExtraStats)
            sb.AppendLine($"  {stat.Label}: {stat.Value}");

        return sb.ToString();
    }

    private static bool TrySetClipboardText(string text)
    {
        // Clipboard.SetText intermittently throws COMException when another process briefly
        // owns clipboard focus — a short retry loop clears this up almost every time.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException ex)
            {
                AppLog.Warning($"Clipboard busy, retrying ({attempt + 1}/10): {ex.Message}");
                Thread.Sleep(75);
            }
        }

        return false;
    }

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    private async Task RunTestAsync(DriveInfo drive, CancellationToken cancellationToken)
    {
        var settings = new TestSettings
        {
            TestMode = TestMode,
            ClearExistingData = ClearExistingData,
            CleanupPolicy = CleanupPolicy,
            RunCapacityTest = RunCapacityTest,
            RunSpeedTest = RunSpeedTest,
            SpeedTestSlot1 = new SpeedTestSlotSettings(SpeedTestSlot1Enabled, SpeedTestSlot1Category),
            SpeedTestSlot2 = new SpeedTestSlotSettings(SpeedTestSlot2Enabled, SpeedTestSlot2Category),
            SpeedTestSlot3 = new SpeedTestSlotSettings(SpeedTestSlot3Enabled, SpeedTestSlot3Category),
            SpeedTestSlot4 = new SpeedTestSlotSettings(SpeedTestSlot4Enabled, SpeedTestSlot4Category),
            CapacityScanDepth = CapacityScanDepth,
        };

        CapacityTrack.IsSelected = RunCapacityTest;
        SyncSpeedTrackSelection();
        foreach (var track in AllTracks) track.ResetForRun();

        CurrentPhaseLabel = "Starting…";
        _lastReportedBlocksFailed = 0;
        _capacityStopwatch = null;
        _capacityEtaSmoothedSeconds = null;
        _overallTestStopwatch = Stopwatch.StartNew();
        _nextTestGate = null;
        IsWaitingForNextTest = false;

        var claimedCapacityBytes = (ulong)(ClaimedCapacityGb * 1_000_000_000);
        var progress = new Progress<TestProgress>(p =>
        {
            // Keep the phase label responsive — it changes in discrete steps, not per-chunk, so
            // there's no jitter to smooth out here.
            CurrentPhaseLabel = PhaseLabel(p.Phase);

            // Fake/corrupted block detection is a rare, important discrete event — surface it the
            // instant it happens rather than waiting for the next throttled UI flush.
            if (p.Phase == TestPhase.VerifyingCapacityPattern && p.BlocksFailedSoFar > _lastReportedBlocksFailed)
            {
                AppLog.Warning($"Fake or corrupted block detected! ({p.BlocksFailedSoFar} block(s) failed so far)");
                SystemSounds.Exclamation.Play();
                FakeBlockFlashTrigger++;
            }
            _lastReportedBlocksFailed = p.BlocksFailedSoFar;

            // Everything else (throughput number, chart, capacity GB/ETA text) arrives many times
            // per second — too fast to read — so just stash the latest sample per track; the timer flushes it.
            if (TrackForPhase(p.Phase) is { } track) track.PendingProgress = p;
        });

        _liveUpdateTimer.Start();
        TestResult result;
        if (settings.TestMode == TestMode.File)
        {
            var volumeLetter = drive.VolumeLetter!; // StartTestAsync already guarded against null here
            AppLog.Info(settings.ClearExistingData
                ? $"File Mode: clearing existing data on {volumeLetter} before writing test files."
                : $"File Mode: preserving existing data on {volumeLetter}, using free space only.");

            _lastFileModeVolumeLetter = volumeLetter;
            var fileEngine = new FileModeEngine();
            result = await fileEngine.RunAsync(volumeLetter, claimedCapacityBytes, settings, progress, cancellationToken, WaitForNextTestIfNeededAsync);
        }
        else
        {
            AppLog.Info(drive.VolumeLetters.Count == 0
                ? "No volumes found on this disk — proceeding without locking (unpartitioned/RAW drive)."
                : $"Locking {drive.VolumeLetters.Count} volume(s) before raw access: {string.Join(", ", drive.VolumeLetters)}");

            var lockers = new List<VolumeLocker>();
            try
            {
                foreach (var letter in drive.VolumeLetters)
                {
                    lockers.Add(VolumeLocker.LockAndDismount(letter));
                    AppLog.Info($"Locked and dismounted {letter}.");
                }

                using var accessor = RawDiskAccessor.Open(drive.DevicePath, writable: true);
                var engine = new TestEngine();
                result = await engine.RunAsync(accessor, claimedCapacityBytes, settings, progress, cancellationToken, WaitForNextTestIfNeededAsync);
            }
            finally
            {
                foreach (var locker in lockers) locker.Dispose();
            }
        }

        var verdict = FraudDetector.Evaluate(
            claimedCapacityBytes, ClaimedReadSpeedMegabytesPerSecond, ClaimedWriteSpeedMegabytesPerSecond, result);

        UsbLinkSpeed? negotiatedLinkSpeed;
        if (EffectiveLinkSpeed is { } overrideSpeed)
        {
            negotiatedLinkSpeed = overrideSpeed;
        }
        else
        {
            AppLog.Info($"Auto-detecting negotiated link speed for drive {drive.PhysicalDriveIndex}…");
            negotiatedLinkSpeed = UsbPortInspector.GetNegotiatedLinkSpeed(
                drive.PhysicalDriveIndex, msg => AppLog.Info($"  {msg}"));
        }

        var report = new ReportModel
        {
            TimestampUtc = DateTime.UtcNow,
            DriveLabel = string.IsNullOrWhiteSpace(DriveLabel) ? drive.Model : DriveLabel,
            SerialNumber = drive.SerialNumber,
            ClaimedCapacityBytes = claimedCapacityBytes,
            ClaimedReadSpeedMegabytesPerSecond = ClaimedReadSpeedMegabytesPerSecond,
            ClaimedWriteSpeedMegabytesPerSecond = ClaimedWriteSpeedMegabytesPerSecond,
            NegotiatedLinkSpeed = negotiatedLinkSpeed,
            TestResult = result,
            Verdict = verdict,
            TestDurationSeconds = _overallTestStopwatch?.Elapsed.TotalSeconds ?? 0,
            CapacityScanDepth = CapacityScanDepth,
        };

        LastReport = report;
        VerdictText = VerdictHeadline(verdict.Verdict);
        VerdictExplanationText = verdict.Explanation;
        BuildReportVisuals(report);
        _historyStore.Save(report);
        RefreshHistory();
    }

    private static string VerdictHeadline(DriveVerdict verdict) => verdict switch
    {
        DriveVerdict.Ok => "✅ Matches Claims",
        DriveVerdict.Underperforming => "⚠️ Underperforming",
        DriveVerdict.Counterfeit => "❌ Likely Counterfeit",
        DriveVerdict.Failed => "Test Failed",
        _ => verdict.ToString(),
    };

    /// <summary>How much data the "practical time to move this" figures assume someone is copying — a fixed reference amount so every category's time is directly comparable.</summary>
    private const double PracticalTransferGb = 5;

    private void BuildReportVisuals(ReportModel report)
    {
        BuildCapacitySummary(report);
        BuildSpeedTestRows(report);
        BuildExtraStats(report);
    }

    private void BuildCapacitySummary(ReportModel report)
    {
        var claimedBytes = report.ClaimedCapacityBytes;
        var capacity = report.TestResult.Capacity;
        var verifiedBytes = capacity?.VerifiedGoodBytes ?? 0;
        var missingBytes = claimedBytes > verifiedBytes ? claimedBytes - verifiedBytes : 0;

        CapacitySummary = capacity is null
            ? ReportCapacitySummary.Empty
            : new ReportCapacitySummary
            {
                ClaimedGb = claimedBytes / 1_000_000_000.0,
                VerifiedGb = verifiedBytes / 1_000_000_000.0,
                MissingGb = missingBytes / 1_000_000_000.0,
                VerifiedFraction = claimedBytes == 0 ? 0 : (double)verifiedBytes / claimedBytes,
                BlocksTested = capacity.BlocksTested,
                BlocksFailed = capacity.BlocksFailed,
                HasData = true,
                ScanThoroughnessText = report.CapacityScanDepth switch
                {
                    CapacityScanDepth.Quick => $"Quick scan — {capacity.BlocksTested:N0} sampled blocks checked. Fast, but a smaller sample means it's less likely to catch fraud confined to a narrow region of the drive; run a Standard or Full scan for more confidence.",
                    CapacityScanDepth.Standard => $"Standard scan — {capacity.BlocksTested:N0} blocks checked (~5% of the claimed capacity, evenly spread). A reasonable middle ground between speed and thoroughness.",
                    CapacityScanDepth.Full => $"Full scan — every one of the {capacity.BlocksTested:N0} blocks across the claimed capacity was checked. The most thorough result this app can produce.",
                    _ => string.Empty,
                },
            };
    }

    private void BuildSpeedTestRows(ReportModel report)
    {
        var rows = new List<SpeedTestSlotReportRow>();

        void AddRow(SpeedTestSlotResult? slot)
        {
            if (slot is null) return;
            var info = SpeedTestCategoryCatalog.Get(slot.Category);
            var sizeRange = SpeedTestCategoryOption.All.First(o => o.Category == slot.Category).SizeRangeText;

            var writeSeconds = slot.Write.AverageMegabytesPerSecond > 0 ? PracticalTransferGb * 1000 / slot.Write.AverageMegabytesPerSecond : 0;
            var readSeconds = slot.Read.AverageMegabytesPerSecond > 0 ? PracticalTransferGb * 1000 / slot.Read.AverageMegabytesPerSecond : 0;

            var practicalText = writeSeconds > 0 && readSeconds > 0
                ? $"Moving {PracticalTransferGb:N0} GB worth of {info.WorkloadDescription.ToLowerInvariant()} ({sizeRange} each) would take about "
                    + $"{FormatPracticalDuration(writeSeconds)} to copy TO this drive, and about {FormatPracticalDuration(readSeconds)} to copy FROM it back to a computer."
                : "Not enough data to estimate a practical transfer time for this category.";

            rows.Add(new SpeedTestSlotReportRow
            {
                CategoryName = info.DisplayName,
                WorkloadDescription = info.WorkloadDescription,
                SizeRangeText = sizeRange,
                WriteAvgMbps = slot.Write.AverageMegabytesPerSecond,
                WritePeakMbps = slot.Write.PeakBurstMegabytesPerSecond,
                ClaimedWriteMbps = report.ClaimedWriteSpeedMegabytesPerSecond,
                ReadAvgMbps = slot.Read.AverageMegabytesPerSecond,
                ReadPeakMbps = slot.Read.PeakBurstMegabytesPerSecond,
                ClaimedReadMbps = report.ClaimedReadSpeedMegabytesPerSecond,
                PracticalTimeText = practicalText,
            });
        }

        AddRow(report.TestResult.Slot1);
        AddRow(report.TestResult.Slot2);
        AddRow(report.TestResult.Slot3);
        AddRow(report.TestResult.Slot4);

        SpeedTestRows = new ObservableCollection<SpeedTestSlotReportRow>(rows);
    }

    private static string FormatPracticalDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "an unknown amount of time";
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1) return $"{span.TotalHours:N1} hours";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:N1} minutes";
        return $"{Math.Max(1, span.Seconds)} seconds";
    }

    private void BuildExtraStats(ReportModel report)
    {
        var stats = new List<ReportStat>
        {
            new("Test Duration", FormatDuration(report.TestDurationSeconds)),
            new("Blocks Tested", report.TestResult.Capacity?.BlocksTested.ToString("N0") ?? "—"),
            new("Blocks Failed", report.TestResult.Capacity?.BlocksFailed.ToString("N0") ?? "—"),
        };

        if (report.NegotiatedLinkSpeed is { } linkSpeed)
        {
            var ceiling = linkSpeed.MaxTheoreticalMegabytesPerSecond();
            var bestMeasured = new[] { report.TestResult.Slot1, report.TestResult.Slot2, report.TestResult.Slot3, report.TestResult.Slot4 }
                .Where(s => s is not null)
                .SelectMany(s => new[] { s!.Read.AverageMegabytesPerSecond, s.Write.AverageMegabytesPerSecond })
                .DefaultIfEmpty(0).Max();

            // Measured throughput can't legitimately exceed the link's real-world ceiling by much —
            // some margin for overhead/rounding is normal, but well past 100% means the detected
            // link speed itself is wrong (walked to the wrong hub/port), not that the drive is
            // somehow faster than physics allows. Flag that instead of printing a nonsense percentage.
            const double plausibilityTolerance = 1.1;
            var isImplausible = ceiling > 0 && bestMeasured > ceiling * plausibilityTolerance;

            stats.Add(new ReportStat("Negotiated Link", isImplausible
                ? $"{linkSpeed.DisplayName()} ({ceiling:N0} MB/s ceiling) ⚠ likely misdetected"
                : $"{linkSpeed.DisplayName()} ({ceiling:N0} MB/s ceiling)"));

            if (isImplausible)
            {
                stats.Add(new ReportStat("Link Utilization", "⚠ Measured speed exceeds this link's ceiling — detected link speed is unreliable, not the drive"));
                AppLog.Warning($"Negotiated link speed ({linkSpeed.DisplayName()}, {ceiling:N0} MB/s ceiling) is implausible: measured {bestMeasured:N0} MB/s exceeds it. Link-speed detection likely resolved the wrong hub/port — use Inspect Port on the Testing tab to see the device-tree walk.");
            }
            else if (bestMeasured > 0 && ceiling > 0)
            {
                stats.Add(new ReportStat("Link Utilization", $"{bestMeasured / ceiling:P0} of link ceiling"));
            }
        }

        ExtraStats = new ObservableCollection<ReportStat>(stats);
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (totalSeconds <= 0) return "—";
        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }
}
