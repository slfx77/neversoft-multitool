using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

public sealed class AudioFileEntry : IListEntry, INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.Pending;
    private int _sampleCount;
    private bool _isExpanded;

    public bool IsChildEntry => false;

    public required string FileName { get; init; }
    public required string AudioFormat { get; init; }

    /// <summary>
    /// Whether this format supports expand/collapse (VAB, KAT have multiple samples).
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

    public string ChevronGlyph => !IsExpandable ? "" : _isExpanded ? "\uE70D" : "\uE76C";

    /// <summary>
    /// Cached child sample entries, populated on first expand.
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

    public ExtractionStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public string StatusDisplay => _status switch
    {
        ExtractionStatus.Pending => "",
        ExtractionStatus.Processing => "Converting...",
        ExtractionStatus.Done => "OK",
        ExtractionStatus.Error => "ERROR",
        ExtractionStatus.Skipped => "SKIPPED",
        _ => ""
    };

    public SolidColorBrush StatusColor => _status switch
    {
        ExtractionStatus.Processing => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00)),
        ExtractionStatus.Done => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0xA6, 0x00)),
        ExtractionStatus.Error => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x00, 0x00)),
        ExtractionStatus.Skipped => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xA5, 0x00)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88))
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
