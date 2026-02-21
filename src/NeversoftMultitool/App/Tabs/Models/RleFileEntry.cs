using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

public sealed class RleFileEntry : INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.Pending;
    private int _detectedWidth = 512;
    private int? _widthOverride;

    public required string FileName { get; init; }

    public int DetectedWidth
    {
        get => _detectedWidth;
        set
        {
            _detectedWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveWidth));
            OnPropertyChanged(nameof(WidthDisplay));
        }
    }

    public int? WidthOverride
    {
        get => _widthOverride;
        set
        {
            _widthOverride = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveWidth));
            OnPropertyChanged(nameof(WidthDisplay));
        }
    }

    public int EffectiveWidth => _widthOverride ?? _detectedWidth;

    public string WidthDisplay => _widthOverride.HasValue ? $"{_widthOverride}*" : $"{_detectedWidth}";

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
