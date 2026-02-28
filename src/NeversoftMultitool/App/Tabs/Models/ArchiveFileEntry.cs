namespace NeversoftMultitool;

public class ArchiveFileEntry : BaseFileEntry
{
    public required string FileName { get; init; }
    public long Size { get; init; }

    protected override string ProcessingVerb => "Extracting...";

    public string SizeDisplay
    {
        get
        {
            if (Size < 1024) return $"{Size} B";
            if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
            return $"{Size / (1024.0 * 1024.0):F1} MB";
        }
    }
}
