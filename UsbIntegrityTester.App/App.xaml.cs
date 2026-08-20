using System.Configuration;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using UsbIntegrityTester.App.Logging;

namespace UsbIntegrityTester.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbIntegrityTester", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // DispatcherUnhandledException only covers exceptions raised while handling a dispatched
        // UI-thread operation. An exception on a background thread (e.g. an unawaited Task, or a
        // thread started directly) or during WPF's internal render pass can terminate the process
        // without ever reaching it — these two catch what that one can't. We can't always stop the
        // crash, but we can get the stack trace onto disk before the process goes down, since the
        // in-memory Logs tab dies with it.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive on an unexpected error — the Logs tab captures the full detail
        // instead of the window silently disappearing, which is what happened before this existed.
        AppLog.Error(e.Exception.ToString());
        TryWriteCrashLog(e.Exception, "DispatcherUnhandledException (recovered)");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nFull details are in the Logs tab.",
            "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // By the time this fires the process is already on its way down — nothing here can save
        // it, so just get the details written before it's gone.
        TryWriteCrashLog(e.ExceptionObject as Exception, "AppDomain.UnhandledException (fatal)");
    }

    private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        TryWriteCrashLog(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private static void TryWriteCrashLog(Exception? exception, string source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch
        {
            // If even this fails there's nothing more we can do — the process is already going down.
        }
    }
}
