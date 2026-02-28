using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI;
using Microsoft.UI.Xaml.Media;

namespace NeversoftMultitool;

/// <summary>
///     Base class for file entry models providing shared Status, StatusDisplay, and StatusColor properties.
/// </summary>
public abstract class BaseFileEntry : INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.Pending;

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

    protected virtual string ProcessingVerb => "Processing...";

    public string StatusDisplay => _status switch
    {
        ExtractionStatus.Pending => "",
        ExtractionStatus.Processing => ProcessingVerb,
        ExtractionStatus.Done => "OK",
        ExtractionStatus.Error => "ERROR",
        ExtractionStatus.Skipped => "SKIPPED",
        _ => ""
    };

    public SolidColorBrush StatusColor => _status switch
    {
        ExtractionStatus.Processing => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00)),
        ExtractionStatus.Done => new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xA6, 0x00)),
        ExtractionStatus.Error => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x00, 0x00)),
        ExtractionStatus.Skipped => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xA5, 0x00)),
        _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88))
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
