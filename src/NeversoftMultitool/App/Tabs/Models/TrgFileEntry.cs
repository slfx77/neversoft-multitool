using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

public sealed class TrgFileEntry : IListEntry, INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.Pending;
    private int _nodeCount;
    private bool _isExpanded;
    private string _versionDisplay = "";

    public bool IsChildEntry => false;

    public required string FileName { get; init; }
    public required string FilePath { get; init; }

    public string VersionDisplay
    {
        get => _versionDisplay;
        set
        {
            _versionDisplay = value;
            OnPropertyChanged();
        }
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

    public string ChevronGlyph => _nodeCount == 0 ? "" : _isExpanded ? "\uE70D" : "\uE76C";

    public int NodeCount
    {
        get => _nodeCount;
        set
        {
            _nodeCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NodeCountDisplay));
            OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public string NodeCountDisplay => _nodeCount > 0 ? _nodeCount.ToString() : "";

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
        ExtractionStatus.Processing => "Exporting...",
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

    internal Core.Formats.Trg.TrgFile? CachedParsedFile { get; set; }
    internal List<TrgNodeEntry>? CachedChildren { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
