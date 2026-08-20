using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UsbIntegrityTester.App.ViewModels;

/// <summary>
/// One direction of a test track — the "Write" half or the "Read"/"Verify" half — with its own
/// progress, throughput number, and trend line. Splitting these out means a workload's write and
/// read results never overwrite or blend into each other on screen: once write finishes its
/// numbers stay put while read starts fresh alongside it.
/// </summary>
public sealed partial class TestChannelViewModel : ObservableObject
{
    public string Label { get; }

    private Brush _neutralBrush = Brushes.Gray;
    private Brush _neutralFillBrush = Brushes.Transparent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainderFraction))]
    private double _progressFraction;

    /// <summary>1 - ProgressFraction — the "not yet done" portion, for drawing the unfilled part of a progress bar as a second Star-sized Grid column.</summary>
    public double RemainderFraction => Math.Max(0, 1 - ProgressFraction);

    [ObservableProperty] private string _currentThroughputText = "—";
    [ObservableProperty] private string _peakThroughputText = string.Empty;
    [ObservableProperty] private string _averageThroughputText = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private Brush _throughputBrush = Brushes.Gray;
    [ObservableProperty] private Brush _throughputFillBrush = Brushes.Transparent;

    public ObservableCollection<double> LivePoints { get; } = new();

    private double? _smoothedMbps;
    private double _peakMbps;
    private double _sumMbps;
    private int _sampleCount;

    public TestChannelViewModel(string label) => Label = label;

    public void ResetForRun(Brush neutralBrush, Brush neutralFillBrush)
    {
        _neutralBrush = neutralBrush;
        _neutralFillBrush = neutralFillBrush;
        LivePoints.Clear();
        ResetSmoothing();
        ProgressFraction = 0;
        CurrentThroughputText = "—";
        PeakThroughputText = string.Empty;
        AverageThroughputText = string.Empty;
        DetailText = string.Empty;
        ThroughputBrush = neutralBrush;
        ThroughputFillBrush = neutralFillBrush;
    }

    public void ResetSmoothing()
    {
        _smoothedMbps = null;
        _peakMbps = 0;
        _sumMbps = 0;
        _sampleCount = 0;
    }

    /// <summary>Applies an exponential moving average and records it toward peak/average, returning the value to display.</summary>
    public double Smooth(double raw)
    {
        _smoothedMbps = _smoothedMbps is { } prev ? prev + (raw - prev) * 0.35 : raw;
        _peakMbps = Math.Max(_peakMbps, raw);
        _sumMbps += raw;
        _sampleCount++;
        return _smoothedMbps.Value;
    }

    public double PeakMbps => _peakMbps;
    public double AverageMbps => _sampleCount > 0 ? _sumMbps / _sampleCount : 0;
}
