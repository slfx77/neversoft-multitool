using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Core;

/// <summary>
///     Recursively extracts all archives found under a root directory, in-place.
///     Archives extract into a sibling subdirectory named after the archive stem.
///     After each pass, newly-extracted directories are scanned for more archives.
///     Repeats until no new archives are found.
/// </summary>
public static class RecursiveUnpacker
{
    // .img is gated on a same-stem .ccd sibling inside DiscImageArchive
    // because bare .img files are PS2 IOP modules / GC textures rather than
    // disc images. Bare .bin track files are reached through their .cue.
    private static readonly string[] ArchiveExtensions =
    [
        ".wad", ".pre", ".prx", ".prd", ".prf", ".prg", ".pkr", ".ddx", ".bon", ".pak", ".apk", ".zip", ".cut",
        ".iso", ".cue", ".gdi", ".img"
    ];

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

            var archiveType = ClassifyArchive(file);

            // Skip raw-data files that carry an archive extension but no parseable structure
            if (archiveType.EndsWith("(raw)", StringComparison.Ordinal))
                continue;

            results.Add(new ArchiveInfo
            {
                FilePath = file,
                ArchiveType = archiveType,
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

        var stem = ArchiveNaming.GetExtractionStem(archivePath);
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
        var ext = GetArchiveExtension(archivePath);

        // PKR and disc images use internal directory names directly under
        // outputDir (no stem subdir), so wrap them in a stem subdirectory
        // for consistency.
        string outputDir;
        if (ext is ".pkr" or ".iso" or ".cue" or ".gdi" or ".img")
        {
            var stem = ArchiveNaming.GetExtractionStem(archivePath);
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
            case ".pre" or ".prd" or ".prf" or ".prg" or ".prx":
                ExtractPreFamily(archivePath, outputDir, ext, ct);
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
            case ".pak" when PakArchive.IsPakArchive(archivePath):
            case ".apk" when PakArchive.IsPakArchive(archivePath):
                PakArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".zip" when QZipArchive.IsZip(archivePath):
                QZipArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".cut" when CutArchive.IsCut(archivePath):
                CutArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".iso" or ".cue" or ".gdi" or ".img" when DiscImageArchive.IsDiscImage(archivePath):
                DiscImageArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
        }
    }

    private static void ExtractPreFamily(string archivePath, string outputDir, string ext, CancellationToken ct)
    {
        if (ext == ".prx" || CompressedPreArchive.IsCompressedPre(archivePath))
            CompressedPreArchive.ExtractFiles(archivePath, outputDir, null, ct);
        else
            PreArchive.ExtractFiles(archivePath, outputDir, null, ct);
    }

    /// <summary>
    ///     Classifies an archive file by extension into a display type string.
    /// </summary>
    public static string ClassifyArchive(string filePath)
    {
        var ext = GetArchiveExtension(filePath);
        return ext switch
        {
            ".wad" => "WAD",
            ".pre" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3" : "PRE",
            ".prx" => "PRE3",
            ".prd" or ".prg" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3 (German)" : "PRE (German)",
            ".prf" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3 (French)" : "PRE (French)",
            ".pkr" => "PKR",
            ".ddx" => "DDX",
            ".bon" => "BON",
            ".pak" => PakArchive.IsPakArchive(filePath) ? "PAK" : "PAK (raw)",
            ".apk" => PakArchive.IsPakArchive(filePath) ? "PAK (GC)" : "PAK (raw)",
            ".zip" => QZipArchive.IsZip(filePath) ? "ZIP" : "ZIP (raw)",
            ".cut" => CutArchive.IsCut(filePath) ? "CUT" : "CUT (raw)",
            ".iso" or ".cue" or ".gdi" or ".img" =>
                DiscImageArchive.IsDiscImage(filePath) ? "DISC" : "DISC (raw)",
            _ => "?"
        };
    }

    /// <summary>
    ///     Checks if a file path has a supported archive extension.
    /// </summary>
    public static bool IsArchiveFile(string filePath)
    {
        var ext = GetArchiveExtension(filePath);
        return ArchiveExtensions.Contains(ext);
    }

    /// <summary>
    ///     Gets the archive-relevant extension, handling double extensions like .pak.ps2.
    /// </summary>
    public static string GetArchiveExtension(string filePath)
    {
        var name = Path.GetFileName(filePath).ToLowerInvariant();

        // Check for double extensions (e.g. .pak.ps2, .pak.xen)
        foreach (var archiveExt in ArchiveExtensions)
        {
            var pattern = archiveExt + ".";
            var idx = name.IndexOf(pattern, StringComparison.Ordinal);
            if (idx >= 0 && idx > 0)
                return archiveExt;
        }

        return Path.GetExtension(filePath).ToLowerInvariant();
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
        // Each pass calls Scan() fresh, so an archive that failed to extract (no {stem}/ created)
        // would be rediscovered as "not extracted" forever. Track attempted paths to break the loop.
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pass = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pass++;

            var discovered = Scan(rootDir, pass);
            var newArchives = discovered
                .Where(a => !a.AlreadyExtracted && !attempted.Contains(a.FilePath))
                .ToList();

            if (newArchives.Count == 0)
                break;

            onPassDiscovered?.Invoke(pass, newArchives);
            allArchives.AddRange(newArchives);

            foreach (var archive in newArchives)
            {
                ct.ThrowIfCancellationRequested();
                onArchiveStarted?.Invoke(archive);
                attempted.Add(archive.FilePath);

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

    public sealed class ArchiveInfo
    {
        public required string FilePath { get; init; }
        public required string ArchiveType { get; init; }
        public required int Pass { get; init; }
        public bool AlreadyExtracted { get; init; }
        public bool Extracted { get; set; }
        public string? Error { get; set; }
    }
}
