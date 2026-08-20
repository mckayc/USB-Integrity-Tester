using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.App.ViewModels;

/// <summary>One row in a speed test slot's category dropdown — the category plus display text built from its <see cref="SpeedTestCategoryInfo"/>, so the ComboBox can bind directly instead of needing a converter.</summary>
public sealed record SpeedTestCategoryOption(SpeedTestCategory Category, string DisplayName, string WorkloadDescription, string SizeRangeText)
{
    public static readonly IReadOnlyList<SpeedTestCategoryOption> All = SpeedTestCategoryCatalog.All
        .Select(c =>
        {
            var info = SpeedTestCategoryCatalog.Get(c);
            var sizeRange = info.IsFixedSize
                ? FormatSize(info.MinFileSizeBytes)
                : $"{FormatSize(info.MinFileSizeBytes)}–{FormatSize(info.MaxFileSizeBytes)}";
            return new SpeedTestCategoryOption(c, info.DisplayName, info.WorkloadDescription, sizeRange);
        })
        .ToList();

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        _ => $"{bytes / 1_000.0:0.#} KB",
    };
}
