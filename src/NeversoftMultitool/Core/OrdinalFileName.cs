namespace NeversoftMultitool.Core;

internal static class OrdinalFileName
{
    public static bool HasSuffix(string fileName, string suffix)
    {
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasAnySuffix(string fileName, IReadOnlyList<string> suffixes)
    {
        return suffixes.Any(suffix => HasSuffix(fileName, suffix));
    }

    public static bool HasExtension(string path, string extension)
    {
        return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
    }

    public static string StripCompoundSuffix(string fileName, IReadOnlyList<string> suffixes)
    {
        var suffix = suffixes.FirstOrDefault(candidate => HasSuffix(fileName, candidate));
        return suffix == null
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName[..^suffix.Length];
    }
}
