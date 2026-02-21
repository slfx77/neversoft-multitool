namespace NeversoftMultitool.Core.Formats.Video;

public sealed class SfdProbeResult
{
    public required TimeSpan Duration { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public double FrameRate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int AudioSampleRate { get; init; }
    public int AudioChannels { get; init; }
    public long FileSize { get; init; }

    public string DurationDisplay =>
        Duration.TotalMinutes >= 60
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
            : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";

    public string ResolutionDisplay => $"{Width}x{Height}";
}
