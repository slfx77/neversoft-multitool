using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

public class SfdFileEntry : BaseFileEntry
{
    private double _convertProgress;
    private string _durationDisplay = "";
    private bool _isChecked = true;
    private string _resolutionDisplay = "";

    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required AssetSource Source { get; init; }
    public string RelativePath { get; init; } = "";

    protected override string ProcessingVerb => "Converting...";

    /// <summary>Directory portion of the relative path, for the Folder column.</summary>
    public string FolderDisplay =>
        Path.GetDirectoryName(RelativePath)?.Replace('\\', '/') ?? "";

    /// <summary>Whether this file participates in the batch conversion.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    // Settable with change notification: directory scans defer ffprobe to a
    // background pass so large recursive scans don't block the UI thread.
    public string DurationDisplay
    {
        get => _durationDisplay;
        set
        {
            _durationDisplay = value;
            OnPropertyChanged();
        }
    }

    public string ResolutionDisplay
    {
        get => _resolutionDisplay;
        set
        {
            _resolutionDisplay = value;
            OnPropertyChanged();
        }
    }

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
