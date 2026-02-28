namespace NeversoftMultitool;

/// <summary>
///     Child row representing a single audio sample within a VAB or KAT soundbank.
/// </summary>
public sealed class AudioSampleEntry : IListEntry
{
    public required string ParentFileName { get; init; }
    public required int SampleIndex { get; init; }
    public required string Encoding { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int DataSize { get; init; }

    public string IndexDisplay => $"#{SampleIndex:D3}";
    public string SizeDisplay => DataSize >= 1024 ? $"{DataSize / 1024:N0} KB" : $"{DataSize} B";

    public string InfoDisplay
    {
        get
        {
            if (SampleRate <= 0) return Encoding;
            var channelDesc = Channels > 1 ? "stereo" : "mono";
            return $"{Encoding}, {SampleRate} Hz, {channelDesc}";
        }
    }

    public bool IsChildEntry => true;
}
