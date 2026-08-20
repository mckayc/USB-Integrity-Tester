namespace UsbIntegrityTester.App.Logging;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record LogEntry
{
    public required DateTime Timestamp { get; init; }
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }

    public override string ToString() => $"[{Timestamp:HH:mm:ss}] {Level}: {Message}";
}
