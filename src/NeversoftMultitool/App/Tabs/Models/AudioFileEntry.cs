namespace NeversoftMultitool;

public class AudioFileEntry : BaseFileEntry, IListEntry
{
    private bool _isExpanded;
    private int _sampleCount;

    public required string FileName { get; init; }
    public required string AudioFormat { get; init; }

    protected override string ProcessingVerb => "Converting...";

    /// <summary>
    ///     Whether this format supports expand/collapse (VAB, KAT have multiple samples).
    /// </summary>
    public bool IsExpandable => AudioFormat is "VAB" or "KAT";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public string ChevronGlyph => IsExpandable switch
    {
        false => "",
        true => _isExpanded ? "\uE70D" : "\uE76C"
    };

    /// <summary>
    ///     Cached child sample entries, populated on first expand.
    /// </summary>
    internal List<AudioSampleEntry>? CachedChildren { get; set; }

    public int SampleCount
    {
        get => _sampleCount;
        set
        {
            _sampleCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SampleCountDisplay));
        }
    }

    public string SampleCountDisplay => _sampleCount > 0 ? _sampleCount.ToString() : "";

    public bool IsChildEntry => false;
}
