namespace NeversoftMultitool.Core.Formats.Vid1;

public sealed class Vid1VideoProbeResult
{
    public required TimeSpan Duration { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FrameCount { get; init; }
    public required double FrameRate { get; init; }
    public required Vid1VideoVariant Variant { get; init; }
    public required long FileSize { get; init; }
    public bool HasAudio { get; init; }
    public int AudioSampleRate { get; init; }
    public int AudioChannels { get; init; }

    public string DurationDisplay =>
        Duration.TotalMinutes >= 60
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
            : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";

    public string ResolutionDisplay => $"{Width}x{Height}";

    public string VariantDisplay =>
        Variant switch
        {
            Vid1VideoVariant.ThawLongForm => "THAW GameCube Long-Form",
            Vid1VideoVariant.ThawAtvi => "THAW GameCube ATVI Variant",
            _ => "Unknown VID1 Variant"
        };
}
