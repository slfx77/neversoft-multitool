namespace NeversoftMultitool.Core.Formats.Video;

public sealed class StrProbeResult
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FrameCount { get; init; }
    public required bool HasAudio { get; init; }
    public required long FileSize { get; init; }
    public double FrameRate { get; init; } = 15.0;

    public TimeSpan Duration => FrameCount > 0
        ? TimeSpan.FromSeconds(FrameCount / FrameRate)
        : TimeSpan.Zero;

    public string DurationDisplay
    {
        get
        {
            var d = Duration;
            return d.TotalMinutes >= 60
                ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
                : $"{(int)d.TotalMinutes}:{d.Seconds:D2}";
        }
    }

    public string ResolutionDisplay => $"{Width}x{Height}";
}
