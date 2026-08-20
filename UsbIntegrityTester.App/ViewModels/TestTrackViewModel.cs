using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.App.ViewModels;

/// <summary>
/// Live state for one test type's card on the Run Test tab (Capacity / Large File / Small File /
/// Huge File). Each test type gets its own progress and card, and within it, its two directions
/// (Write/Read, or Write/Verify for the capacity test) are tracked as separate <see cref="TestChannelViewModel"/>
/// instances so they get their own numbers and trend lines instead of one shared line that jumps
/// between unrelated measurements.
/// </summary>
public sealed partial class TestTrackViewModel : ObservableObject
{
    public string Title { get; }

    /// <summary>The "Write" channel for every track.</summary>
    public TestChannelViewModel Primary { get; }

    /// <summary>The "Read" channel for speed tracks, or "Verify" for the capacity track.</summary>
    public TestChannelViewModel Secondary { get; }

    private readonly Brush _neutralBrush;
    private readonly Brush _neutralFillBrush;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private string _statusText = "Not selected";
    [ObservableProperty] private double _progressFraction;

    /// <summary>Non-empty once the capacity test's verify pass finds a bad block — stays visible on the card, unlike the one-off screen flash.</summary>
    [ObservableProperty] private string _failedBlocksText = string.Empty;

    /// <summary>The phase this track last flushed — lets the caller detect a sub-phase change (e.g. write -> read) and reset that channel's smoothing so numbers don't carry over between them.</summary>
    public TestPhase? LastPhase;

    /// <summary>Latest raw sample waiting for the next throttled UI flush.</summary>
    public TestProgress? PendingProgress;

    public TestTrackViewModel(string title, string primaryLabel, string secondaryLabel, Brush neutralBrush, Brush neutralFillBrush)
    {
        Title = title;
        Primary = new TestChannelViewModel(primaryLabel);
        Secondary = new TestChannelViewModel(secondaryLabel);
        _neutralBrush = neutralBrush;
        _neutralFillBrush = neutralFillBrush;
    }

    public void ResetForRun()
    {
        Primary.ResetForRun(_neutralBrush, _neutralFillBrush);
        Secondary.ResetForRun(_neutralBrush, _neutralFillBrush);
        PendingProgress = null;
        LastPhase = null;
        ProgressFraction = 0;
        IsRunning = false;
        IsComplete = false;
        FailedBlocksText = string.Empty;
        StatusText = IsSelected ? "Waiting…" : "Not selected";
    }

    // Reflects a live toggle on the Setup tab (e.g. unchecking "Small file") back into the card's
    // status text, but only while idle — don't relabel a card that's mid-run or just finished.
    partial void OnIsSelectedChanged(bool value)
    {
        if (IsRunning || IsComplete) return;
        StatusText = value ? "Waiting…" : "Not selected";
    }

    public void MarkComplete()
    {
        IsRunning = false;
        IsComplete = IsSelected;
        StatusText = IsSelected ? "Complete" : "Not selected";
    }
}
