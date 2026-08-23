using System.Globalization;
using System.Text;
using UsbIntegrityTester.Core.History;
using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.Core.Reporting;

/// <summary>
/// CSV/text export of a report. Image export (PNG for the video-friendly results card) belongs
/// in the App project, rendered straight from the on-screen chart/visual — no need to duplicate
/// chart drawing logic here in Core.
/// </summary>
public static class ReportExporter
{
    /// <summary>One row per test run — the same figures shown in the History grid, for a spreadsheet-friendly export of the whole log rather than just one report.</summary>
    public static void WriteHistoryCsv(IReadOnlyList<HistoryEntry> entries, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date (UTC),Drive,Serial,Verdict,Claimed (GB),Verified (GB),Write MB/s,Read MB/s");

        foreach (var entry in entries)
        {
            sb.Append(EscapeCsv(entry.TimestampUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(EscapeCsv(entry.DriveLabel)).Append(',');
            sb.Append(EscapeCsv(entry.SerialNumber)).Append(',');
            sb.Append(EscapeCsv(entry.Verdict.ToString())).Append(',');
            sb.Append(EscapeCsv((entry.ClaimedCapacityBytes / 1_000_000_000.0).ToString("F1", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(EscapeCsv(entry.VerifiedGoodBytes is { } v ? (v / 1_000_000_000.0).ToString("F1", CultureInfo.InvariantCulture) : "")).Append(',');
            sb.Append(EscapeCsv(entry.AverageWriteMegabytesPerSecond?.ToString("F1", CultureInfo.InvariantCulture) ?? "")).Append(',');
            sb.AppendLine(EscapeCsv(entry.AverageReadMegabytesPerSecond?.ToString("F1", CultureInfo.InvariantCulture) ?? ""));
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    public static void WriteCsv(ReportModel report, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Field,Value");
        AppendRow(sb, "Timestamp (UTC)", report.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        AppendRow(sb, "Drive Label", report.DriveLabel);
        AppendRow(sb, "Serial Number", report.SerialNumber);
        AppendRow(sb, "Claimed Capacity (bytes)", report.ClaimedCapacityBytes.ToString(CultureInfo.InvariantCulture));
        AppendRow(sb, "Claimed Read Speed (MB/s)", report.ClaimedReadSpeedMegabytesPerSecond?.ToString("F1", CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Claimed Write Speed (MB/s)", report.ClaimedWriteSpeedMegabytesPerSecond?.ToString("F1", CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Negotiated Link Speed", report.NegotiatedLinkSpeed?.ToString() ?? "Unknown");
        AppendRow(sb, "Verified Good Capacity (bytes)", report.TestResult.Capacity?.VerifiedGoodBytes.ToString(CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Blocks Failed", report.TestResult.Capacity?.BlocksFailed.ToString(CultureInfo.InvariantCulture) ?? "");
        AppendSlot(sb, report.TestResult.Slot1);
        AppendSlot(sb, report.TestResult.Slot2);
        AppendSlot(sb, report.TestResult.Slot3);
        AppendSlot(sb, report.TestResult.Slot4);

        if (report.TestResult.RealFileCopy is { } copy)
        {
            AppendRow(sb, "Real-World Copy Files", copy.FilesCopied.ToString(CultureInfo.InvariantCulture));
            AppendRow(sb, "Real-World Copy Total Bytes", copy.TotalBytes.ToString(CultureInfo.InvariantCulture));
            AppendRow(sb, "Real-World Copy Write Speed (MB/s)", copy.WriteAverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture));
            AppendRow(sb, "Real-World Copy Read Speed (MB/s)", copy.ReadAverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture));
            AppendRow(sb, "Real-World Copy Files Verified Identical", copy.FilesVerifiedIdentical.ToString(CultureInfo.InvariantCulture));
            AppendRow(sb, "Real-World Copy Issues", copy.Issues.Count.ToString(CultureInfo.InvariantCulture));
        }

        AppendRow(sb, "Verdict", report.Verdict.Verdict.ToString());
        AppendRow(sb, "Explanation", report.Verdict.Explanation);

        File.WriteAllText(filePath, sb.ToString());
    }

    private static void AppendSlot(StringBuilder sb, SpeedTestSlotResult? slot)
    {
        if (slot is null) return;
        var name = SpeedTestCategoryCatalog.Get(slot.Category).DisplayName;
        AppendRow(sb, $"Average Write Speed, {name} (MB/s)", slot.Write.AverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture));
        AppendRow(sb, $"Average Read Speed, {name} (MB/s)", slot.Read.AverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture));
    }

    private static void AppendRow(StringBuilder sb, string field, string value)
    {
        sb.Append(EscapeCsv(field)).Append(',').Append(EscapeCsv(value)).AppendLine();
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
