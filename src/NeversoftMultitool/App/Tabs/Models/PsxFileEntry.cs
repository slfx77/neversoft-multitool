using Microsoft.UI.Xaml;
using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

public class PsxFileEntry : BaseFileEntry, IListEntry
{
    private int _extractedCount;
    private bool _hasTextures = true;
    private bool _isExpanded;
    private int _textureCount;

    public required string FileName { get; init; }
    public required AssetSource Source { get; init; }
    public string RelativePath { get; init; } = "";
    internal TextureFileFormat Format { get; init; }

    public int TextureCount
    {
        get => _textureCount;
        set
        {
            _textureCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TextureCountDisplay));
            OnPropertyChanged(nameof(ChevronGlyph));
            OnPropertyChanged(nameof(ExpanderVisibility));
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
            OnPropertyChanged(nameof(ExpanderVisibility));
        }
    }

    /// <summary>
    ///     Single-texture files (every .img variant, some one-entry dictionaries)
    ///     render as flat rows \u2014 selecting the row previews the texture directly,
    ///     so a chevron would only add a dead click target. Count 0 means "not
    ///     yet enumerated" and keeps the chevron until the count arrives.
    /// </summary>
    public bool IsExpandable => _hasTextures && _textureCount != 1;

    public string ChevronGlyph => IsExpandable
        ? _isExpanded ? "\uE70D" : "\uE76C"
        : "";

    /// <summary>
    ///     Files with no textures (or exactly one) hide the expander button
    ///     entirely instead of rendering a dead blank glyph in the chevron column.
    /// </summary>
    public Visibility ExpanderVisibility => IsExpandable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Directory portion of the relative path, for the Folder column.</summary>
    public string FolderDisplay =>
        Path.GetDirectoryName(RelativePath.Replace("::", "/"))?.Replace('\\', '/') ?? "";

    /// <summary>
    ///     Cached child texture entries, populated on first expand.
    /// </summary>
    internal List<PsxTextureEntry>? CachedChildren { get; set; }

    public string TextureCountDisplay => _textureCount > 0 ? _textureCount.ToString() : "";
    public string ExtractedCountDisplay => _extractedCount > 0 ? _extractedCount.ToString() : "";

    public bool IsChildEntry => false;
}
