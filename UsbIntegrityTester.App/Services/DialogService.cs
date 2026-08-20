namespace UsbIntegrityTester.App.Services;

/// <summary>
/// Lets the ViewModel ask for a yes/no confirmation without knowing about WPF UI elements —
/// MainWindow registers the handler once at startup, wired to its in-app ContentDialog, so
/// confirmations render inside the app's own themed chrome instead of a jarring OS message box.
/// </summary>
public static class DialogService
{
    public static Func<string, string, Task<bool>>? ConfirmHandler { get; set; }

    public static Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmHandler?.Invoke(title, message) ?? Task.FromResult(false);
}
