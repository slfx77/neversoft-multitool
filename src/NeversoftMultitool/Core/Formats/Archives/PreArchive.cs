namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
/// Stub for PRE archive extraction.
/// PRE format support will be implemented in a follow-up plan.
/// </summary>
public static class PreArchive
{
    public static List<ArchiveEntry> GetFileList(string prePath)
    {
        throw new NotSupportedException("PRE archive extraction is not yet implemented.");
    }

    public static void ExtractFiles(string prePath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        throw new NotSupportedException("PRE archive extraction is not yet implemented.");
    }
}
