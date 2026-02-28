namespace NeversoftMultitool;

public class SfdFileEntry : BaseFileEntry
{
    private double _convertProgress;

    public required string FileName { get; init; }
    public required string FilePath { get; init; }

    protected override string ProcessingVerb => "Converting...";

    public string DurationDisplay { get; init; } = "";
    public string ResolutionDisplay { get; init; } = "";
    public string SizeDisplay { get; init; } = "";

    public double ConvertProgress
    {
        get => _convertProgress;
        set
        {
            _convertProgress = value;
            OnPropertyChanged();
        }
    }
}
