namespace NeversoftMultitool;

/// <summary>
///     Shared playback/duration formatting: "mm:ss" with zero-padded minutes
///     below one hour ("00:08"), "h:mm:ss" at or above it.
/// </summary>
internal static class TimeDisplay
{
    public static string Format(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
