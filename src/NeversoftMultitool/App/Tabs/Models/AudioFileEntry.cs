using Microsoft.UI.Xaml;
using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

public class AudioFileEntry : BaseFileEntry, IListEntry
{
    private bool _isExpanded;
    private int _sampleCount;

    public required string FileName { get; init; }
    public required string AudioFormat { get; init; }
    public required AssetSource Source { get; init; }
    public string RelativePath { get; init; } = "";

    protected override string ProcessingVerb => "Converting...";

    /// <summary>
    ///     Whether this format supports expand/collapse (VAB, KAT, SFX have
    ///     multiple samples). XA files are only expandable once a background
    ///     probe has found more than one interleaved channel — single-channel
    ///     XA stays a flat, directly-previewable row.
    /// </summary>
    public bool IsExpandable =>
        AudioFormat is "VAB" or "KAT" or "SFX"
        || (AudioFormat == "XA" && CachedChildren is { Count: > 0 });

    /// <summary>
    ///     Re-evaluates the expander UI after <see cref="CachedChildren" /> is
    ///     populated off-thread (the XA channel probe). UI thread only.
    /// </summary>
    internal void NotifyExpandabilityChanged()
    {
        OnPropertyChanged(nameof(ExpanderVisibility));
        OnPropertyChanged(nameof(ChevronGlyph));
    }

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
    ///     Single-sample formats hide the expander button entirely instead of
    ///     rendering a dead blank glyph in the chevron column.
    /// </summary>
    public Visibility ExpanderVisibility => IsExpandable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Directory portion of the relative path, for the Folder column.</summary>
    public string FolderDisplay =>
        Path.GetDirectoryName(RelativePath.Replace("::", "/"))?.Replace('\\', '/') ?? "";

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
