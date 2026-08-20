using System.Globalization;
using System.Text;

namespace UsbIntegrityTester.Core.Reporting;

/// <summary>
/// CSV/text export of a report. Image export (PNG for the video-friendly results card) belongs
/// in the App project, rendered straight from the on-screen chart/visual — no need to duplicate
/// chart drawing logic here in Core.
/// </summary>
public static class ReportExporter
{
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
        AppendRow(sb, "Average Write Speed, Large File (MB/s)", report.TestResult.Write?.AverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Average Read Speed, Large File (MB/s)", report.TestResult.Read?.AverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Average Write Speed, Small File (MB/s)", report.TestResult.SmallFileWrite?.AverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Average Read Speed, Small File (MB/s)", report.TestResult.SmallFileRead?.AverageMegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture) ?? "");
        AppendRow(sb, "Verdict", report.Verdict.Verdict.ToString());
        AppendRow(sb, "Explanation", report.Verdict.Explanation);

        File.WriteAllText(filePath, sb.ToString());
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
