using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

public sealed class PsxFileEntry : IListEntry, INotifyPropertyChanged
{
    private int _textureCount;
    private int _extractedCount;
    private ExtractionStatus _status = ExtractionStatus.Pending;
    private bool _isExpanded;
    private bool _hasTextures = true;

    public bool IsChildEntry => false;

    public required string FileName { get; init; }

    public int TextureCount
    {
        get => _textureCount;
        set { _textureCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TextureCountDisplay)); }
    }

    public int ExtractedCount
    {
        get => _extractedCount;
        set { _extractedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExtractedCountDisplay)); }
    }

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

    public string ChevronGlyph => !_hasTextures ? "" : _isExpanded ? "\uE70D" : "\uE76C";

    /// <summary>
    /// Cached child texture entries, populated on first expand.
    /// </summary>
    internal List<PsxTextureEntry>? CachedChildren { get; set; }

    public string TextureCountDisplay => _textureCount > 0 ? _textureCount.ToString() : "";
    public string ExtractedCountDisplay => _extractedCount > 0 ? _extractedCount.ToString() : "";

    public string StatusDisplay => _status switch
    {
        ExtractionStatus.Pending => "",
        ExtractionStatus.Processing => "Processing...",
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
