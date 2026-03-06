namespace NeversoftMultitool;

public class PsxFileEntry : BaseFileEntry, IListEntry
{
    private int _extractedCount;
    private bool _hasTextures = true;
    private bool _isExpanded;
    private int _textureCount;

    public required string FileName { get; init; }
    internal TextureFileFormat Format { get; init; }

    public int TextureCount
    {
        get => _textureCount;
        set
        {
            _textureCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TextureCountDisplay));
        }
    }

    public int ExtractedCount
    {
        get => _extractedCount;
        set
        {
            _extractedCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExtractedCountDisplay));
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

    public bool HasTextures
    {
        get => _hasTextures;
        set
        {
            _hasTextures = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public string ChevronGlyph => _hasTextures switch
    {
        false => "",
        true => _isExpanded ? "\uE70D" : "\uE76C"
    };

    /// <summary>
    ///     Cached child texture entries, populated on first expand.
    /// </summary>
    internal List<PsxTextureEntry>? CachedChildren { get; set; }

    public string TextureCountDisplay => _textureCount > 0 ? _textureCount.ToString() : "";
    public string ExtractedCountDisplay => _extractedCount > 0 ? _extractedCount.ToString() : "";

    public bool IsChildEntry => false;
}
