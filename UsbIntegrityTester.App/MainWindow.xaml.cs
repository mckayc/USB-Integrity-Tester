using System.Windows;
using UsbIntegrityTester.App.Services;
using UsbIntegrityTester.App.ViewModels;
using UsbIntegrityTester.App.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UsbIntegrityTester.App;

public partial class MainWindow : FluentWindow
{
    private readonly ClaimedStatsView _claimedStatsView = new();
    private readonly UsbConnectionsView _usbConnectionsView = new();
    private readonly TestingView _testingView = new();
    private readonly ReportView _reportView = new();
    private readonly HistoryView _historyView = new();
    private readonly LogsView _logsView = new();
    private TaskCompletionSource<bool>? _confirmCompletion;

    public MainWindow()
    {
        InitializeComponent();

        // SystemThemeWatcher.Watch alone leaves the title bar in an inconsistent state on first
        // paint (dark background, black text) until something forces a full theme re-application —
        // toggling the switch fixed it because that calls Apply() directly. Do that once up front.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);

        DialogService.ConfirmHandler = (title, message) =>
        {
            ConfirmOverlayTitle.Text = title;
            ConfirmOverlayMessage.Text = message;
            ConfirmOverlay.Visibility = Visibility.Visible;

            _confirmCompletion = new TaskCompletionSource<bool>();
            return _confirmCompletion.Task;
        };

        Loaded += (_, _) =>
        {
            NavigateTo((NavigationViewItem)RootNavigation.MenuItems[0]);

            // The constructor's Apply() above runs before this window has a native HWND, so the
            // title bar's text-color recalculation doesn't fully take effect (black text on the
            // dark background). Re-applying here, once the handle exists, fixes first paint
            // without requiring the user to toggle the switch manually.
            var theme = DarkModeToggle.IsChecked == true ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
        };
        Closing += (_, _) =>
        {
            var viewModel = DataContext as MainViewModel;
            viewModel?.CleanupOnAppClose();
            viewModel?.SaveSettings();
        };
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e) => NavigateTo((NavigationViewItem)sender);

    private void ConfirmOverlayContinue_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmCompletion?.TrySetResult(true);
    }

    private void ConfirmOverlayCancel_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmCompletion?.TrySetResult(false);
    }

    private void DarkModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var theme = DarkModeToggle.IsChecked == true ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
    }

    private void NavigateTo(NavigationViewItem item)
    {
        UIElement? view = item.Tag switch
        {
            "ClaimedStats" => _claimedStatsView,
            "UsbConnections" => _usbConnectionsView,
            "Testing" => _testingView,
            "Report" => _reportView,
            "History" => _historyView,
            "Logs" => _logsView,
            _ => null,
        };

        if (view is null) return;

        foreach (var menuItem in RootNavigation.MenuItems)
            if (menuItem is NavigationViewItem navItem)
                navItem.IsActive = ReferenceEquals(navItem, item);

        RootNavigation.ReplaceContent(view, DataContext);
    }
}
