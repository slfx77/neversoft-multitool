using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Opt-in deep companion search for assets whose texture dictionary ships
///     in a DIFFERENT subtree than the mesh. THPS4/THUG level geometry sits at
///     <c>&lt;root&gt;/Levels/&lt;Stem&gt;/&lt;stem&gt;.geom.ps2</c> while its
///     textures live only inside the extracted scene PRE at
///     <c>&lt;root&gt;/pre/&lt;Stem&gt;Scn/Levels/&lt;Stem&gt;/&lt;Stem&gt;.tex.ps2</c>.
///     Called only AFTER the exact same-directory/same-archive match fails, so
///     behavior is unchanged wherever exact matching already works.
/// </summary>
internal static class BuildTreeCompanionLocator
{
    private static readonly string[] SceneArchiveExtensions = [".pre", ".prx", ".prd", ".prf", ".prg"];

    public static byte[]? TryReadTextureCompanion(
        AssetSource source, string stem, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrEmpty(stem))
            return null;

        return source switch
        {
            { FileSystemPath: { } path } => TryReadFromBuildTree(path, stem, extensions),
            ArchiveAssetSource archive => TryReadFromArchive(archive, stem, extensions),
            _ => null
        };
    }

    /// <summary>
    ///     Walk up to four ancestors looking for a build root (a directory with a
    ///     <c>pre</c> child), then probe <c>pre/{stem}Scn/…</c> for the dictionary.
    /// </summary>
    private static byte[]? TryReadFromBuildTree(string assetPath, string stem, IReadOnlyList<string> extensions)
    {
        var dir = Path.GetDirectoryName(assetPath);
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++, dir = Path.GetDirectoryName(dir))
        {
            string? preDir;
            try
            {
                preDir = Directory.EnumerateDirectories(dir)
                    .FirstOrDefault(d => Path.GetFileName(d).Equals("pre", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (preDir == null)
                continue;

            foreach (var scnDir in Directory.EnumerateDirectories(preDir)
                         .Where(d => Path.GetFileName(d).Equals(stem + "Scn", StringComparison.OrdinalIgnoreCase)))
            {
                var hit = ProbeSceneDir(scnDir, stem, extensions);
                if (hit != null)
                    return File.ReadAllBytes(hit);
            }
        }

        return null;
    }

    private static string? ProbeSceneDir(string scnDir, string stem, IReadOnlyList<string> extensions)
    {
        // The canonical location first, then a bounded search of the scene tree
        // only (never all of pre/).
        foreach (var ext in extensions)
        {
            var direct = Path.Combine(scnDir, "Levels", stem, stem + ext);
            if (File.Exists(direct))
                return direct;
        }

        foreach (var ext in extensions)
        {
            var match = Directory
                .EnumerateFiles(scnDir, stem + ext, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match != null)
                return match;
        }

        return null;
    }

    /// <summary>
    ///     Archive-backed assets: disambiguate same-name entries by directory,
    ///     then look for a sibling nested scene archive ({stem}Scn.pre) in this
    ///     archive or any ancestor archive.
    /// </summary>
    private static byte[]? TryReadFromArchive(
        ArchiveAssetSource archive, string stem, IReadOnlyList<string> extensions)
    {
        var fs = archive.Backend.FileSystem;

        foreach (var ext in extensions)
        {
            var preferred = fs.FindAllByName(stem + ext)
                .FirstOrDefault(e =>
                    e.Directory.Contains($"Levels/{stem}", StringComparison.OrdinalIgnoreCase) ||
                    e.Directory.Contains(stem + "Scn", StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                return TryRead(fs, preferred);
        }

        for (var level = fs; level != null; level = level.Parent)
        {
            var sceneArchive = FindSceneArchiveEntry(level, stem);
            if (sceneArchive == null)
                continue;

            using var nested = level.TryOpenNested(sceneArchive);
            if (nested == null)
                continue;

            foreach (var ext in extensions)
            {
                var entry = nested.FindByName(stem + ext);
                if (entry != null)
                    return TryRead(nested, entry);
            }
        }

        return null;
    }

    private static ArchiveEntry? FindSceneArchiveEntry(IArchiveFileSystem fs, string stem)
    {
        foreach (var suffix in SceneArchiveExtensions)
        {
            var entry = fs.FindByName($"{stem}Scn{suffix}");
            if (entry != null)
                return entry;
        }

        return null;
    }

    private static byte[]? TryRead(IArchiveFileSystem fs, ArchiveEntry entry)
    {
        try
        {
            return fs.ReadEntry(entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }
    }
}
