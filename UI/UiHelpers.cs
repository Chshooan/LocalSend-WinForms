using System.Globalization;

namespace LocalSendWinForms.UI;

/// <summary>Small presentation helpers shared by the UI forms.</summary>
internal static class UiHelpers
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formats a byte count using binary units (e.g. "1.4 MB").</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {Units[0]}"
            : string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", value, Units[unit]);
    }

    /// <summary>Formats a per-second byte rate (e.g. "12.4 MB/s").</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        var size = FormatSize((long)bytesPerSecond);
        return size + "/s";
    }
}
