namespace NeversoftMultitool.Core.Formats.DiscImage;

public sealed record DiscFileEntry(
    string Directory,
    string Name,
    long ExtentLba,
    long Size,
    bool IsDirectory)
{
    public string FullPath => Directory.Length == 0 ? Name : $"{Directory}/{Name}";
}
