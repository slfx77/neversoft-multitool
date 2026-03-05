using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool;

public class QbFileEntry : BaseFileEntry, IListEntry
{
    private bool _isExpanded;
    private int _nodeCount;
    private string _versionDisplay = "QB";

    public required string FileName { get; init; }
    public required string FilePath { get; init; }

    protected override string ProcessingVerb => "Exporting...";

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

    public string ChevronGlyph => _nodeCount switch
    {
        0 => "",
        _ => _isExpanded ? "\uE70D" : "\uE76C"
    };

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

    internal QbFile? CachedParsedFile { get; set; }
    internal List<QbItemEntry>? CachedChildren { get; set; }

    public bool IsChildEntry => false;
}
