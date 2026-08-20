using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace UsbIntegrityTester.App.Logging;

/// <summary>
/// App-wide log, visible in the Logs tab so failures (like a raw-disk access-denied error)
/// are visible and copyable instead of only surfacing as a one-line status message.
/// </summary>
public static class AppLog
{
    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static void Info(string message) => Add(LogLevel.Info, message);
    public static void Warning(string message) => Add(LogLevel.Warning, message);
    public static void Error(string message) => Add(LogLevel.Error, message);

    private static void Add(LogLevel level, string message)
    {
        var entry = new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message };

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Entries.Add(entry);
        }
        else
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Entries.Add(entry));
        }
    }
}
