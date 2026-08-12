namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Stages an archive-backed video under its original leaf name inside a
///     unique directory. Preserving the leaf keeps filename-derived output and
///     VID1 variant behavior deterministic.
/// </summary>
internal sealed class ArchiveVideoTempFile : IDisposable
{
    private string? _directory;

    private ArchiveVideoTempFile(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    public string Path { get; }

    public static ArchiveVideoTempFile Write(string scope, string entryFileName, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(scope) ||
            scope is "." or ".." ||
            !string.Equals(scope, System.IO.Path.GetFileName(scope), StringComparison.Ordinal))
        {
            throw new ArgumentException("Temporary video scope must be a single directory name", nameof(scope));
        }

        var normalizedEntryName = entryFileName.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(normalizedEntryName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            throw new ArgumentException("Archive video entry must have a file name", nameof(entryFileName));
        if (fileName.Contains(':') ||
            !string.Equals(fileName, fileName.TrimEnd(' ', '.'), StringComparison.Ordinal) ||
            IsWindowsDeviceName(fileName))
        {
            throw new ArgumentException(
                "Archive video entry cannot use a Windows-reserved file name",
                nameof(entryFileName));
        }

        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NeversoftMultitool",
            scope,
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, fileName);

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, data);
            return new ArchiveVideoTempFile(directory, path);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public void Dispose()
    {
        var directory = Interlocked.Exchange(ref _directory, null);
        if (directory != null)
            TryDeleteDirectory(directory);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool IsWindowsDeviceName(string fileName)
    {
        var dotIndex = fileName.IndexOf('.');
        var stem = dotIndex >= 0 ? fileName[..dotIndex] : fileName;
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
}
