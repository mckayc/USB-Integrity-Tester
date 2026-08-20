using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using UsbIntegrityTester.App.ViewModels;

namespace UsbIntegrityTester.App.Views;

public partial class TestingView : UserControl
{
    public TestingView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.FakeBlockFlashTrigger)) return;

        // A quick red flash the instant a fake/corrupted block is found — the "gotcha" moment.
        var animation = new DoubleAnimation
        {
            From = 0.6,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        FakeBlockFlashOverlay.BeginAnimation(OpacityProperty, animation);
    }
}
