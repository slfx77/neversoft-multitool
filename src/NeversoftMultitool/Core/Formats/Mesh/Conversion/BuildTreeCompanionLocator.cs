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

    private enum BuildTreeProbeStatus
    {
        Missing,
        Found,
        Conflict
    }

    private readonly record struct BuildTreeProbeResult(
        BuildTreeProbeStatus Status,
        byte[]? Bytes = null)
    {
        public static BuildTreeProbeResult Missing => new(BuildTreeProbeStatus.Missing);
        public static BuildTreeProbeResult Conflict => new(BuildTreeProbeStatus.Conflict);
        public static BuildTreeProbeResult Found(byte[] bytes) => new(BuildTreeProbeStatus.Found, bytes);
    }

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
            var result = ProbeBuildRoot(dir, stem, extensions);
            switch (result.Status)
            {
                case BuildTreeProbeStatus.Found:
                    return result.Bytes;
                case BuildTreeProbeStatus.Conflict:
                    return null;
            }
        }

        return null;
    }

    private static BuildTreeProbeResult ProbeBuildRoot(
        string buildRoot,
        string stem,
        IReadOnlyList<string> extensions)
    {
        string[] preDirs;
        try
        {
            preDirs = Directory.EnumerateDirectories(buildRoot)
                .Where(path => Path.GetFileName(path).Equals("pre", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuildTreeProbeResult.Conflict;
        }

        if (preDirs.Length == 0)
            return BuildTreeProbeResult.Missing;

        // Enumerate every case-insensitive match before choosing. On a
        // case-sensitive filesystem both "pre" and "PRE" (or two differently
        // cased StemScn directories) can coexist; filesystem enumeration order
        // must not decide which texture dictionary owns the mesh.
        var sceneDirs = preDirs
            .SelectMany(Directory.EnumerateDirectories)
            .Where(path => Path.GetFileName(path).Equals(stem + "Scn", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sceneDirs.Length == 0)
            return BuildTreeProbeResult.Missing;

        // The canonical location wins globally across all matching scene
        // directories. Preserve the caller's extension order within the tier.
        foreach (var ext in extensions)
        {
            var candidates = sceneDirs
                .Select(sceneDir => Path.Combine(sceneDir, "Levels", stem, stem + ext))
                .Where(File.Exists)
                .ToArray();
            if (candidates.Length > 0)
                return ReadEquivalentCandidates(candidates);
        }

        // Only when no canonical file exists may a dictionary elsewhere in the
        // bounded scene tree be considered. Resolve the whole tier before
        // returning so filesystem enumeration order cannot pick an owner.
        foreach (var ext in extensions)
        {
            var candidates = sceneDirs
                .SelectMany(sceneDir => Directory.EnumerateFiles(
                    sceneDir,
                    stem + ext,
                    SearchOption.AllDirectories))
                .ToArray();
            if (candidates.Length > 0)
                return ReadEquivalentCandidates(candidates);
        }

        return BuildTreeProbeResult.Missing;
    }

    private static BuildTreeProbeResult ReadEquivalentCandidates(IReadOnlyList<string> candidates)
    {
        // Enumeration and reads intentionally remain exception-transparent. A
        // partial scan cannot safely claim that one candidate is unique.
        var selected = File.ReadAllBytes(candidates[0]);
        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = File.ReadAllBytes(candidates[index]);
            if (!selected.AsSpan().SequenceEqual(candidate))
                return BuildTreeProbeResult.Conflict;
        }

        return BuildTreeProbeResult.Found(selected);
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
