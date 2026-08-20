using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
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
    public TestTrackViewModel LargeFileTrack { get; } =
        new("Large File (1 MB blocks)", "Write", "Read", ThroughputPalette.LargeFileNeutral, ThroughputPalette.LargeFileNeutralFill);
    public TestTrackViewModel SmallFileTrack { get; } =
        new("Small File (4 KB blocks)", "Write", "Read", ThroughputPalette.SmallFileNeutral, ThroughputPalette.SmallFileNeutralFill);
    public TestTrackViewModel HugeFileTrack { get; } =
        new("Huge File (100 MB blocks)", "Write", "Read", ThroughputPalette.HugeFileNeutral, ThroughputPalette.HugeFileNeutralFill);

    /// <summary>All four tracks in a fixed order — Capacity, Large, Small, Huge — always. Cards
    /// used to reorder to put whichever test was running at the top, but that meant the whole
    /// layout reshuffled every time a test finished, which was disorienting; the accent border and
    /// pulsing dot on the active card already say "this one's running" without moving anything.</summary>
    public IReadOnlyList<TestTrackViewModel> AllTracks => new[] { CapacityTrack, LargeFileTrack, SmallFileTrack, HugeFileTrack };

    public MainViewModel()
    {
        Directory.CreateDirectory(AppDataDir);
        _historyStore = new DriveHistoryStore(Path.Combine(AppDataDir, "history.db"));

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
    private void CleanUpTestFilesNow()
    {
        if (SelectedDrive?.VolumeLetter is not { } letter) return;
        FileModeEngine.TryDeleteTestFolder(letter);
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
    [ObservableProperty] private bool _runLargeFileSpeedTest = true;
    [ObservableProperty] private bool _runSmallFileSpeedTest = true;
    [ObservableProperty] private bool _runHugeFileSpeedTest;
    [ObservableProperty] private CapacityScanDepth _capacityScanDepth = CapacityScanDepth.Quick;

    // Keep each Run Test card's "selected/not selected" state live as the user toggles the Setup
    // tab, so a card doesn't misleadingly say "Not selected" for a test that's actually checked.
    partial void OnRunCapacityTestChanged(bool value) => CapacityTrack.IsSelected = value;
    partial void OnRunSpeedTestChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnRunLargeFileSpeedTestChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnRunSmallFileSpeedTestChanged(bool value) => SyncSpeedTrackSelection();
    partial void OnRunHugeFileSpeedTestChanged(bool value) => SyncSpeedTrackSelection();

    private void SyncSpeedTrackSelection()
    {
        LargeFileTrack.IsSelected = RunSpeedTest && RunLargeFileSpeedTest;
        SmallFileTrack.IsSelected = RunSpeedTest && RunSmallFileSpeedTest;
        HugeFileTrack.IsSelected = RunSpeedTest && RunHugeFileSpeedTest;
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
    [ObservableProperty] private ObservableCollection<SpeedComparisonRow> _speedComparisonRows = new();
    [ObservableProperty] private ObservableCollection<ReportTrendCard> _trendCards = new();
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
            foreach (var drive in DriveEnumerator.GetRemovableUsbDrives())
                AvailableDrives.Add(drive);

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
        TestPhase.MeasuringLargeFileWriteSpeed or TestPhase.MeasuringLargeFileReadSpeed => LargeFileTrack,
        TestPhase.MeasuringSmallFileWriteSpeed or TestPhase.MeasuringSmallFileReadSpeed => SmallFileTrack,
        TestPhase.MeasuringHugeFileWriteSpeed or TestPhase.MeasuringHugeFileReadSpeed => HugeFileTrack,
        _ => null,
    };

    private static string SubPhaseStatusText(TestPhase phase) => phase switch
    {
        TestPhase.WritingCapacityPattern => "Writing test pattern…",
        TestPhase.VerifyingCapacityPattern => "Verifying blocks…",
        TestPhase.MeasuringLargeFileWriteSpeed or TestPhase.MeasuringSmallFileWriteSpeed or TestPhase.MeasuringHugeFileWriteSpeed => "Writing…",
        TestPhase.MeasuringLargeFileReadSpeed or TestPhase.MeasuringSmallFileReadSpeed or TestPhase.MeasuringHugeFileReadSpeed => "Reading…",
        _ => string.Empty,
    };

    /// <summary>Every tracked phase is either the "Write" half (WritingCapacityPattern or a *Write* speed phase) or the "Read"/"Verify" half.</summary>
    private static bool IsPrimaryChannelPhase(TestPhase phase) => phase is TestPhase.WritingCapacityPattern
        or TestPhase.MeasuringLargeFileWriteSpeed or TestPhase.MeasuringSmallFileWriteSpeed or TestPhase.MeasuringHugeFileWriteSpeed;

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

    private static string PhaseLabel(TestPhase phase) => phase switch
    {
        TestPhase.WritingCapacityPattern => "Writing unique data to every tested block, to prove each address is real storage",
        TestPhase.VerifyingCapacityPattern => "Reading each block back to confirm it holds its own data, not a copy",
        TestPhase.MeasuringLargeFileWriteSpeed => "Writing large 1 MB blocks — simulates copying one big file",
        TestPhase.MeasuringLargeFileReadSpeed => "Reading large 1 MB blocks — simulates copying one big file",
        TestPhase.MeasuringSmallFileWriteSpeed => "Writing small 4 KB blocks — simulates copying many small files",
        TestPhase.MeasuringSmallFileReadSpeed => "Reading small 4 KB blocks — simulates copying many small files",
        TestPhase.MeasuringHugeFileWriteSpeed => "Writing huge 100 MB blocks — simulates copying one very large file",
        TestPhase.MeasuringHugeFileReadSpeed => "Reading huge 100 MB blocks — simulates copying one very large file",
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
            RunLargeFileSpeedTest = RunLargeFileSpeedTest,
            RunSmallFileSpeedTest = RunSmallFileSpeedTest,
            RunHugeFileSpeedTest = RunHugeFileSpeedTest,
            CapacityScanDepth = CapacityScanDepth,
        };

        CapacityTrack.IsSelected = RunCapacityTest;
        LargeFileTrack.IsSelected = RunSpeedTest && RunLargeFileSpeedTest;
        SmallFileTrack.IsSelected = RunSpeedTest && RunSmallFileSpeedTest;
        HugeFileTrack.IsSelected = RunSpeedTest && RunHugeFileSpeedTest;
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

        var report = new ReportModel
        {
            TimestampUtc = DateTime.UtcNow,
            DriveLabel = string.IsNullOrWhiteSpace(DriveLabel) ? drive.Model : DriveLabel,
            SerialNumber = drive.SerialNumber,
            ClaimedCapacityBytes = claimedCapacityBytes,
            ClaimedReadSpeedMegabytesPerSecond = ClaimedReadSpeedMegabytesPerSecond,
            ClaimedWriteSpeedMegabytesPerSecond = ClaimedWriteSpeedMegabytesPerSecond,
            NegotiatedLinkSpeed = EffectiveLinkSpeed ?? UsbPortInspector.GetNegotiatedLinkSpeed(drive.PhysicalDriveIndex),
            TestResult = result,
            Verdict = verdict,
            TestDurationSeconds = _overallTestStopwatch?.Elapsed.TotalSeconds ?? 0,
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

    private void BuildReportVisuals(ReportModel report)
    {
        BuildCapacitySummary(report);
        BuildSpeedComparisonRows(report);
        BuildTrendCards(report);
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
            };
    }

    private void BuildSpeedComparisonRows(ReportModel report)
    {
        var entries = new List<(string Label, double Measured, double? Claimed, double? Peak)>();

        if (report.TestResult.Write is { } write)
            entries.Add(("Large File Write", write.AverageMegabytesPerSecond, report.ClaimedWriteSpeedMegabytesPerSecond, write.PeakBurstMegabytesPerSecond));
        if (report.TestResult.Read is { } read)
            entries.Add(("Large File Read", read.AverageMegabytesPerSecond, report.ClaimedReadSpeedMegabytesPerSecond, read.PeakBurstMegabytesPerSecond));
        if (report.TestResult.SmallFileWrite is { } smallWrite)
            entries.Add(("Small File Write", smallWrite.AverageMegabytesPerSecond, report.ClaimedWriteSpeedMegabytesPerSecond, smallWrite.PeakBurstMegabytesPerSecond));
        if (report.TestResult.SmallFileRead is { } smallRead)
            entries.Add(("Small File Read", smallRead.AverageMegabytesPerSecond, report.ClaimedReadSpeedMegabytesPerSecond, smallRead.PeakBurstMegabytesPerSecond));
        if (report.TestResult.HugeFileWrite is { } hugeWrite)
            entries.Add(("Huge File Write", hugeWrite.AverageMegabytesPerSecond, report.ClaimedWriteSpeedMegabytesPerSecond, hugeWrite.PeakBurstMegabytesPerSecond));
        if (report.TestResult.HugeFileRead is { } hugeRead)
            entries.Add(("Huge File Read", hugeRead.AverageMegabytesPerSecond, report.ClaimedReadSpeedMegabytesPerSecond, hugeRead.PeakBurstMegabytesPerSecond));
        // The capacity test writes/reads back every scanned block, so it doubles as an independent
        // measurement — often over a much larger swath of the drive than the dedicated speed test.
        if (report.TestResult.Capacity is { WriteMegabytesPerSecond: > 0 } capacityForWrite)
            entries.Add(("Capacity Scan Write", capacityForWrite.WriteMegabytesPerSecond, report.ClaimedWriteSpeedMegabytesPerSecond, null));
        if (report.TestResult.Capacity is { ReadMegabytesPerSecond: > 0 } capacityForRead)
            entries.Add(("Capacity Scan Read", capacityForRead.ReadMegabytesPerSecond, report.ClaimedReadSpeedMegabytesPerSecond, null));

        // All bars share one scale (the largest measurement or claim in the whole set) so their
        // relative lengths are directly comparable at a glance.
        var axisMax = entries.Count == 0 ? 1 : Math.Max(
            entries.Max(e => e.Measured),
            entries.Max(e => e.Claimed ?? 0));
        if (axisMax <= 0) axisMax = 1;

        SpeedComparisonRows = new ObservableCollection<SpeedComparisonRow>(entries.Select(e => new SpeedComparisonRow
        {
            Label = e.Label,
            MeasuredMbps = e.Measured,
            ClaimedMbps = e.Claimed,
            PeakMbps = e.Peak,
            BarFraction = Math.Clamp(e.Measured / axisMax, 0, 1),
        }));
    }

    private void BuildTrendCards(ReportModel report)
    {
        var cards = new List<ReportTrendCard>();

        void AddCard(string title, SpeedTestResult? result, Brush stroke, Brush fill)
        {
            if (result?.SampledMegabytesPerSecond is not { Count: > 0 } samples) return;
            cards.Add(new ReportTrendCard
            {
                Title = title,
                Points = samples,
                AverageMbps = result.AverageMegabytesPerSecond,
                PeakMbps = result.PeakBurstMegabytesPerSecond,
                Stroke = stroke,
                Fill = fill,
            });
        }

        // Each card shows one workload's burst-to-sustained curve — reveals an SLC-cache cliff
        // (fast at first, then drops) without crowding every workload onto one shared chart.
        AddCard("Large File Write", report.TestResult.Write, ThroughputPalette.LargeFileNeutral, ThroughputPalette.LargeFileNeutralFill);
        AddCard("Large File Read", report.TestResult.Read, ThroughputPalette.LargeFileNeutral, ThroughputPalette.LargeFileNeutralFill);
        AddCard("Small File Write", report.TestResult.SmallFileWrite, ThroughputPalette.SmallFileNeutral, ThroughputPalette.SmallFileNeutralFill);
        AddCard("Small File Read", report.TestResult.SmallFileRead, ThroughputPalette.SmallFileNeutral, ThroughputPalette.SmallFileNeutralFill);
        AddCard("Huge File Write", report.TestResult.HugeFileWrite, ThroughputPalette.HugeFileNeutral, ThroughputPalette.HugeFileNeutralFill);
        AddCard("Huge File Read", report.TestResult.HugeFileRead, ThroughputPalette.HugeFileNeutral, ThroughputPalette.HugeFileNeutralFill);

        TrendCards = new ObservableCollection<ReportTrendCard>(cards);
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
            var bestMeasured = new[]
            {
                report.TestResult.Read?.AverageMegabytesPerSecond,
                report.TestResult.Write?.AverageMegabytesPerSecond,
            }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();

            stats.Add(new ReportStat("Negotiated Link", $"{linkSpeed.DisplayName()} ({ceiling:N0} MB/s ceiling)"));
            if (bestMeasured > 0 && ceiling > 0)
                stats.Add(new ReportStat("Link Utilization", $"{bestMeasured / ceiling:P0} of link ceiling"));
        }

        if (report.TestResult.Write is { } write)
            stats.Add(new ReportStat("Large File Write Peak", $"{write.PeakBurstMegabytesPerSecond:N1} MB/s"));
        if (report.TestResult.Read is { } read)
            stats.Add(new ReportStat("Large File Read Peak", $"{read.PeakBurstMegabytesPerSecond:N1} MB/s"));

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
