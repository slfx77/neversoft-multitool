using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

public sealed class MeshFileEntry : INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.Pending;
    private int _triangleCount;

    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required string Format { get; init; }
    public required int ObjectCount { get; init; }
    public required int MeshCount { get; init; }

    // Internal: PSX level geometry companion texture library (*_g.psx → *_l.psx)
    internal string? CompanionLibraryPsxPath { get; init; }

    // Internal: DDM placement companions
    internal string? CompanionPsxPath { get; init; }
    internal string? CompanionObjectsDdmPath { get; init; }
    internal bool IsPlacedLevel => CompanionPsxPath != null;

    internal bool IsPsx => Format == "PSX";

    public string FormatDisplay => Format;
    public string ObjectsDisplay => ObjectCount.ToString("N0");
    public string MeshesDisplay => MeshCount.ToString("N0");

    public int TriangleCount
    {
        get => _triangleCount;
        set
        {
            _triangleCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrianglesDisplay));
        }
    }

    public string TrianglesDisplay => _triangleCount > 0 ? _triangleCount.ToString("N0") : "";

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
