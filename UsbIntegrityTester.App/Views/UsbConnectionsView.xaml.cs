using System.Windows;
using System.Windows.Controls;
using UsbIntegrityTester.App.ViewModels;

namespace UsbIntegrityTester.App.Views;

public partial class UsbConnectionsView : UserControl
{
    public UsbConnectionsView()
    {
        InitializeComponent();
    }

    private void ClearOverride_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ManualLinkSpeedOverride = null;
    }
}
