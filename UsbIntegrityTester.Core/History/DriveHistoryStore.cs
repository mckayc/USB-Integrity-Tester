using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsbIntegrityTester.Core.Reporting;
using UsbIntegrityTester.Core.Verdict;

namespace UsbIntegrityTester.Core.History;

public sealed record HistoryEntry
{
    public required long Id { get; init; }
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

        // Added after the initial release — existing databases won't have this column yet, so add
        // it if missing rather than requiring users to delete their history to pick up the update.
        using var checkColumn = connection.CreateCommand();
        checkColumn.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TestRuns') WHERE name = 'ReportJson';";
        var hasReportJson = (long)checkColumn.ExecuteScalar()! > 0;
        if (!hasReportJson)
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE TestRuns ADD COLUMN ReportJson TEXT;";
            addColumn.ExecuteNonQuery();
        }
    }

    public void Save(ReportModel report)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TestRuns
                (TimestampUtc, SerialNumber, DriveLabel, Verdict, ClaimedCapacityBytes, VerifiedGoodBytes, AverageWriteMegabytesPerSecond, AverageReadMegabytesPerSecond, ReportJson)
            VALUES
                ($timestamp, $serial, $label, $verdict, $claimedCapacity, $verifiedGoodBytes, $avgWrite, $avgRead, $reportJson);
            """;

        command.Parameters.AddWithValue("$timestamp", report.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$serial", report.SerialNumber);
        command.Parameters.AddWithValue("$label", report.DriveLabel);
        command.Parameters.AddWithValue("$verdict", report.Verdict.Verdict.ToString());
        command.Parameters.AddWithValue("$claimedCapacity", (long)report.ClaimedCapacityBytes);
        command.Parameters.AddWithValue("$verifiedGoodBytes", (object?)(long?)report.TestResult.Capacity?.VerifiedGoodBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$avgWrite", (object?)report.TestResult.Slot1?.Write.AverageMegabytesPerSecond ?? DBNull.Value);
        command.Parameters.AddWithValue("$avgRead", (object?)report.TestResult.Slot1?.Read.AverageMegabytesPerSecond ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportJson", JsonSerializer.Serialize(report));

        command.ExecuteNonQuery();
    }

    /// <summary>Fetches the full report saved alongside a history row, for re-viewing it on the Report page. Returns null for rows saved before report persistence was added.</summary>
    public ReportModel? GetReport(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ReportJson FROM TestRuns WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var json = command.ExecuteScalar() as string;
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ReportModel>(json);
    }

    public IReadOnlyList<HistoryEntry> GetAllHistory()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TimestampUtc, SerialNumber, DriveLabel, Verdict, ClaimedCapacityBytes, VerifiedGoodBytes, AverageWriteMegabytesPerSecond, AverageReadMegabytesPerSecond
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
            SELECT Id, TimestampUtc, SerialNumber, DriveLabel, Verdict, ClaimedCapacityBytes, VerifiedGoodBytes, AverageWriteMegabytesPerSecond, AverageReadMegabytesPerSecond
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
                Id = reader.GetInt64(0),
                TimestampUtc = DateTime.Parse(reader.GetString(1)),
                SerialNumber = reader.GetString(2),
                DriveLabel = reader.GetString(3),
                Verdict = Enum.Parse<DriveVerdict>(reader.GetString(4)),
                ClaimedCapacityBytes = (ulong)reader.GetInt64(5),
                VerifiedGoodBytes = reader.IsDBNull(6) ? null : (ulong)reader.GetInt64(6),
                AverageWriteMegabytesPerSecond = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                AverageReadMegabytesPerSecond = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            });
        }

        return results;
    }
}
