using Microsoft.Data.Sqlite;
using UsbIntegrityTester.Core.Reporting;
using UsbIntegrityTester.Core.Verdict;

namespace UsbIntegrityTester.Core.History;

public sealed record HistoryEntry
{
    public required DateTime TimestampUtc { get; init; }
    public required string SerialNumber { get; init; }
    public required string DriveLabel { get; init; }
    public required DriveVerdict Verdict { get; init; }
    public required ulong ClaimedCapacityBytes { get; init; }
    public required ulong? VerifiedGoodBytes { get; init; }
    public required double? AverageWriteMegabytesPerSecond { get; init; }
    public required double? AverageReadMegabytesPerSecond { get; init; }
}

/// <summary>Local SQLite log of past test runs, keyed by drive serial number, so repeat tests on the same physical drive show a trend.</summary>
public sealed class DriveHistoryStore
{
    private readonly string _connectionString;

    public DriveHistoryStore(string databaseFilePath)
    {
        _connectionString = $"Data Source={databaseFilePath}";
        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TestRuns (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                SerialNumber TEXT NOT NULL,
                DriveLabel TEXT NOT NULL,
                Verdict TEXT NOT NULL,
                ClaimedCapacityBytes INTEGER NOT NULL,
                VerifiedGoodBytes INTEGER,
                AverageWriteMegabytesPerSecond REAL,
                AverageReadMegabytesPerSecond REAL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Save(ReportModel report)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TestRuns
                (TimestampUtc, SerialNumber, DriveLabel, Verdict, ClaimedCapacityBytes, VerifiedGoodBytes, AverageWriteMegabytesPerSecond, AverageReadMegabytesPerSecond)
            VALUES
                ($timestamp, $serial, $label, $verdict, $claimedCapacity, $verifiedGoodBytes, $avgWrite, $avgRead);
            """;

        command.Parameters.AddWithValue("$timestamp", report.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$serial", report.SerialNumber);
        command.Parameters.AddWithValue("$label", report.DriveLabel);
        command.Parameters.AddWithValue("$verdict", report.Verdict.Verdict.ToString());
        command.Parameters.AddWithValue("$claimedCapacity", (long)report.ClaimedCapacityBytes);
        command.Parameters.AddWithValue("$verifiedGoodBytes", (object?)(long?)report.TestResult.Capacity?.VerifiedGoodBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$avgWrite", (object?)report.TestResult.Write?.AverageMegabytesPerSecond ?? DBNull.Value);
        command.Parameters.AddWithValue("$avgRead", (object?)report.TestResult.Read?.AverageMegabytesPerSecond ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> GetAllHistory()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TimestampUtc, SerialNumber, DriveLabel, Verdict, ClaimedCapacityBytes, VerifiedGoodBytes, AverageWriteMegabytesPerSecond, AverageReadMegabytesPerSecond
            FROM TestRuns
            ORDER BY TimestampUtc DESC;
            """;

        return ReadEntries(command);
    }

    public IReadOnlyList<HistoryEntry> GetHistoryForSerial(string serialNumber)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TimestampUtc, SerialNumber, DriveLabel, Verdict, ClaimedCapacityBytes, VerifiedGoodBytes, AverageWriteMegabytesPerSecond, AverageReadMegabytesPerSecond
            FROM TestRuns
            WHERE SerialNumber = $serial
            ORDER BY TimestampUtc DESC;
            """;
        command.Parameters.AddWithValue("$serial", serialNumber);

        return ReadEntries(command);
    }

    private static List<HistoryEntry> ReadEntries(SqliteCommand command)
    {
        var results = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HistoryEntry
            {
                TimestampUtc = DateTime.Parse(reader.GetString(0)),
                SerialNumber = reader.GetString(1),
                DriveLabel = reader.GetString(2),
                Verdict = Enum.Parse<DriveVerdict>(reader.GetString(3)),
                ClaimedCapacityBytes = (ulong)reader.GetInt64(4),
                VerifiedGoodBytes = reader.IsDBNull(5) ? null : (ulong)reader.GetInt64(5),
                AverageWriteMegabytesPerSecond = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                AverageReadMegabytesPerSecond = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            });
        }

        return results;
    }
}
