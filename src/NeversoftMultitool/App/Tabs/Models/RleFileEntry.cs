namespace NeversoftMultitool;

public class RleFileEntry : BaseFileEntry
{
    private int _detectedWidth = 512;
    private int? _widthOverride;

    public required string FileName { get; init; }

    protected override string ProcessingVerb => "Converting...";

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
}
