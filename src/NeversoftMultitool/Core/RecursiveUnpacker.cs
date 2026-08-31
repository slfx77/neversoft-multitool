using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.DiscImage;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Nds;

namespace NeversoftMultitool.Core;

/// <summary>
///     Recursively extracts all archives found under a root directory, in-place.
///     Archives extract into a sibling subdirectory named after the archive stem.
///     After each pass, newly-extracted directories are scanned for more archives.
///     Repeats until no new archives are found.
/// </summary>
public static class RecursiveUnpacker
{
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
        ct.ThrowIfCancellationRequested();

        var parentDir = Path.GetDirectoryName(archivePath)!;
        var ext = GetArchiveExtension(archivePath);

        // PKR and disc images use internal directory names directly under
        // outputDir (no stem subdir), so wrap them in a stem subdirectory
        // for consistency.
        string outputDir;
        if (ext is ".pkr" or ".iso" or ".cue" or ".gdi" or ".img" or ".z64" or ".nds" or ".gob" or ".sdat" or ".gba")
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
                ExtractPreFamily(archivePath, outputDir, ct);
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
            case ".z64" when N64RomArchive.IsN64Rom(archivePath):
                N64RomArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".nds" when NdsRomArchive.IsNdsRom(archivePath):
                NdsRomArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".gob" when GobArchive.IsGobArchive(archivePath):
                GobArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".sdat" when SdatArchive.IsSdat(archivePath):
                SdatArchive.ExtractFiles(archivePath, outputDir, null, ct);
                break;
            case ".gba" when GbaLevelCarver.IsVvLevelRom(archivePath):
                GbaLevelCarver.ExtractFiles(archivePath, outputDir, ct);
                break;
            default:
                throw new InvalidDataException($"Unsupported or invalid archive: {archivePath}");
        }
    }

    private static void ExtractPreFamily(string archivePath, string outputDir, CancellationToken ct)
    {
        // Every genuine Neversoft .prx is a compressed PRE and passes the
        // content check; a Sony PSP firmware .prx falls through to the plain
        // parser's explicit refusal (the scan already skips them as
        // "PRE3 (raw)", so this path only sees them via direct calls).
        if (CompressedPreArchive.IsCompressedPre(archivePath))
            CompressedPreArchive.ExtractFiles(archivePath, outputDir, null, ct);
        else
            PreArchive.ExtractFiles(archivePath, outputDir, null, ct);
    }

    /// <summary>
    ///     Classifies an archive file by extension into a display type string.
    /// </summary>
    public static string ClassifyArchive(string filePath)
    {
        return ArchiveTypeDetector.Classify(filePath);
    }

    /// <summary>
    ///     Checks if a file path has a supported archive extension.
    /// </summary>
    public static bool IsArchiveFile(string filePath)
    {
        return ArchiveTypeDetector.IsArchiveFile(filePath);
    }

    /// <summary>
    ///     Gets the archive-relevant extension, handling double extensions like .pak.ps2.
    /// </summary>
    public static string GetArchiveExtension(string filePath)
    {
        return ArchiveTypeDetector.GetArchiveExtension(filePath);
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
