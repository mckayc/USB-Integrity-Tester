using System.Windows;
using System.Windows.Controls;
using UsbIntegrityTester.App.ViewModels;

namespace UsbIntegrityTester.App.Views;

/// <summary>Picks the Capacity card's GB-progress layout or the speed cards' dual-sparkline layout for whichever track is currently shown full-width at the top of the Run Test tab.</summary>
public sealed class TestTrackHeroTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CapacityTemplate { get; set; }
    public DataTemplate? SpeedTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is TestTrackViewModel { UsesCapacityLayout: true } ? CapacityTemplate : SpeedTemplate;
}
