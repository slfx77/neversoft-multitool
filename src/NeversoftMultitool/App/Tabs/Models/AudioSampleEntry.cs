namespace NeversoftMultitool;

/// <summary>
///     Child row representing a raw bank sample, a resolved SFX cue, or an XA channel.
/// </summary>
public sealed class AudioSampleEntry : IListEntry
{
    public required AudioFileEntry Parent { get; init; }

    /// <summary>The actual VAB/KAT sample index, or XA channel number.</summary>
    public required int SampleIndex { get; init; }

    /// <summary>The authored stream name when the bank stores one.</summary>
    public string? SampleName { get; init; }

    /// <summary>The authored cue index when this row came from an owned SFX sheet.</summary>
    internal int? CueIndex { get; init; }

    /// <summary>The exact sheet whose cue produced this row.</summary>
    internal AudioCueSheetBinding? CueSheet { get; init; }

    public required string Encoding { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int DataSize { get; init; }
    public double? DurationSeconds { get; init; }

    public string ParentFileName => Parent.FileName;

    public string IndexDisplay
    {
        get
        {
            if (CueIndex is not { } cueIndex || CueSheet == null)
            {
                return string.IsNullOrWhiteSpace(SampleName)
                    ? $"#{SampleIndex:D3}"
                    : $"#{SampleIndex:D5} {SampleName}";
            }

            var cue = $"Cue #{cueIndex:D3} → sample #{SampleIndex:D3}";
            return Parent.CueSheets.Count > 1 ? $"{CueSheet.FileName}: {cue}" : cue;
        }
    }
    public string SizeDisplay => DataSize >= 1024 ? $"{DataSize / 1024:N0} KB" : $"{DataSize} B";
    public string DurationDisplay => DurationSeconds is { } duration && duration > 0
        ? TimeDisplay.Format(TimeSpan.FromSeconds(duration))
        : "";

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
