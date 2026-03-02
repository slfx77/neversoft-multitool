namespace NeversoftMultitool;

public class UnpackArchiveEntry : BaseFileEntry
{
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public required string ArchiveType { get; init; }
    public int Pass { get; init; }
    public string? ErrorMessage { get; set; }

    protected override string ProcessingVerb => "Extracting...";
}
