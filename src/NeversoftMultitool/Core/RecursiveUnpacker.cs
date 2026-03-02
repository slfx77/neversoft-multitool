using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core;

/// <summary>
///     Recursively extracts all archives found under a root directory, in-place.
///     Archives extract into a sibling subdirectory named after the archive stem.
///     After each pass, newly-extracted directories are scanned for more archives.
///     Repeats until no new archives are found.
/// </summary>
public static class RecursiveUnpacker
{
    private static readonly string[] ArchiveExtensions =
        [".wad", ".pre", ".prx", ".pkr", ".ddx", ".bon"];

    public sealed class ArchiveInfo
    {
        public required string FilePath { get; init; }
        public required string ArchiveType { get; init; }
        public required int Pass { get; init; }
        public bool AlreadyExtracted { get; init; }
        public bool Extracted { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    ///     Scans a directory tree for all archive files, returning them with already-extracted status.
    /// </summary>
    public static List<ArchiveInfo> Scan(string rootDir, int pass = 1)
    {
        var results = new List<ArchiveInfo>();

        foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            if (!IsArchiveFile(file))
                continue;

            results.Add(new ArchiveInfo
            {
                FilePath = file,
                ArchiveType = ClassifyArchive(file),
                Pass = pass,
                AlreadyExtracted = IsAlreadyExtracted(file)
            });
        }

        return results;
    }

    /// <summary>
    ///     Checks if an archive has already been extracted.
    ///     Rule: directory named {stem} exists next to the archive and is non-empty.
    /// </summary>
    public static bool IsAlreadyExtracted(string archivePath)
    {
        var parentDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrEmpty(parentDir))
            return false;

        var stem = Path.GetFileNameWithoutExtension(archivePath);
        var extractDir = Path.Combine(parentDir, stem);

        return Directory.Exists(extractDir) &&
               Directory.EnumerateFileSystemEntries(extractDir).Any();
    }

    /// <summary>
    ///     Extracts a single archive in-place.
    /// </summary>
    public static void ExtractArchive(string archivePath, CancellationToken ct = default)
    {
        var parentDir = Path.GetDirectoryName(archivePath)!;
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();

        // PKR uses internal directory names directly under outputDir (no stem subdir),
        // so wrap it in a stem subdirectory for consistency.
        string outputDir;
        if (ext == ".pkr")
        {
            var stem = Path.GetFileNameWithoutExtension(archivePath);
            outputDir = Path.Combine(parentDir, stem);
            Directory.CreateDirectory(outputDir);
        }
        else
        {
            outputDir = parentDir;
        }

        switch (ext)
        {
            case ".wad":
                WadArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".pre" when CompressedPreArchive.IsCompressedPre(archivePath):
            case ".prx":
                CompressedPreArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".pre":
                PreArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".pkr":
                PkrArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".ddx":
                DdxArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".bon":
                BonArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
        }
    }

    /// <summary>
    ///     Classifies an archive file by extension into a display type string.
    /// </summary>
    public static string ClassifyArchive(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".wad" => "WAD",
            ".pre" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3" : "PRE",
            ".prx" => "PRE3",
            ".pkr" => "PKR",
            ".ddx" => "DDX",
            ".bon" => "BON",
            _ => "?"
        };
    }

    /// <summary>
    ///     Checks if a file path has a supported archive extension.
    /// </summary>
    public static bool IsArchiveFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ArchiveExtensions.Contains(ext);
    }

    /// <summary>
    ///     Full recursive extraction loop. Extracts all archives, rescans, repeats.
    /// </summary>
    public static List<ArchiveInfo> ExtractAll(
        string rootDir,
        Action<ArchiveInfo>? onArchiveStarted = null,
        Action<ArchiveInfo>? onArchiveCompleted = null,
        Action<int, List<ArchiveInfo>>? onPassDiscovered = null,
        CancellationToken ct = default)
    {
        var allArchives = new List<ArchiveInfo>();
        var pass = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pass++;

            var discovered = Scan(rootDir, pass);
            var newArchives = discovered
                .Where(a => !a.AlreadyExtracted)
                .ToList();

            if (newArchives.Count == 0)
                break;

            onPassDiscovered?.Invoke(pass, newArchives);
            allArchives.AddRange(newArchives);

            foreach (var archive in newArchives)
            {
                ct.ThrowIfCancellationRequested();
                onArchiveStarted?.Invoke(archive);

                try
                {
                    ExtractArchive(archive.FilePath, ct);
                    archive.Extracted = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    archive.Error = ex.Message;
                }

                onArchiveCompleted?.Invoke(archive);
            }
        }

        return allArchives;
    }
}
