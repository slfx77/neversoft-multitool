namespace NeversoftMultitool.Core;

/// <summary>
///     Finds companion files (textures, skeletons) relative to a source file.
///     Handles both organized layouts (sibling TEX/SKE/ directories) and real game
///     disc layouts (per-entity bundles, WAD-root Textures/Skeletons/ directories).
/// </summary>
public static class CompanionSearch
{
    private const int MaxAncestorDepth = 5;

    /// <summary>
    ///     Finds a companion file by stem matching with ancestor directory walk.
    ///     Search order: same directory → sibling known dirs → ancestor walk.
    /// </summary>
    /// <param name="startDir">Directory of the source file.</param>
    /// <param name="stem">File stem to match (e.g., "Anl_Chicken").</param>
    /// <param name="extensions">Extensions to try, in priority order (e.g., [".tex.ps2", ".tex"]).</param>
    /// <param name="knownDirNames">
    ///     Directory names to check at each ancestor level
    ///     (e.g., ["TEX", "Textures"] or ["SKE", "Skeletons"]).
    /// </param>
    /// <returns>Full path of the first matching file, or null.</returns>
    public static string? FindCompanion(string startDir, string stem,
        ReadOnlySpan<string> extensions, ReadOnlySpan<string> knownDirNames)
    {
        // 1. Same directory
        var match = TryMatchInDir(startDir, stem, extensions);
        if (match != null) return match;

        // 2. Sibling known directories at parent level (e.g., ../TEX/, ../SKE/)
        var parent = Path.GetDirectoryName(startDir);
        if (parent != null)
        {
            foreach (var dirName in knownDirNames)
            {
                var sibling = FindDirectory(parent, dirName);
                if (sibling == null) continue;

                match = TryMatchInDir(sibling, stem, extensions);
                if (match != null) return match;
            }
        }

        // 3. Ancestor walk — climb up from parent, check known subdirectories at each level
        //    This handles the real game disc layout where Skeletons/ is at WAD root
        //    and models are nested under Models/Animals/Anl_Chicken/
        var ancestor = parent != null ? Path.GetDirectoryName(parent) : null;
        for (var depth = 0; depth < MaxAncestorDepth && ancestor != null; depth++)
        {
            foreach (var dirName in knownDirNames)
            {
                var candidateDir = FindDirectory(ancestor, dirName);
                if (candidateDir == null) continue;

                match = TryMatchInDir(candidateDir, stem, extensions);
                if (match != null) return match;
            }

            ancestor = Path.GetDirectoryName(ancestor);
        }

        return null;
    }

    /// <summary>
    ///     Recursively finds all files matching any of the given extensions under a root directory.
    /// </summary>
    public static List<string> FindAllByExtension(string rootDir, ReadOnlySpan<string> extensions)
    {
        if (!Directory.Exists(rootDir))
            return [];

        var extList = new string[extensions.Length];
        extensions.CopyTo(extList);

        return Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return extList.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    /// <summary>
    ///     Computes a search root directory for batch scanning. Finds the common
    ///     directory prefix of all file paths, then widens it by walking up to
    ///     a game/WAD root (detected by landmark directories like Models/, Textures/,
    ///     Levels/). Falls back to walking up 3 levels from the common root.
    /// </summary>
    public static string? GetCommonRoot(IEnumerable<string> filePaths)
    {
        string? commonRoot = null;

        foreach (var filePath in filePaths)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir == null) continue;

            if (commonRoot == null)
            {
                commonRoot = dir;
                continue;
            }

            // Walk back until we find a shared directory ancestor.
            while (commonRoot != null &&
                   !IsSameOrDescendant(commonRoot, dir))
            {
                commonRoot = Path.GetDirectoryName(commonRoot);
            }
        }

        if (commonRoot == null) return null;

        // Widen: walk up from common root, looking for a directory that contains
        // game-layout landmark subdirectories. This handles the case where input
        // files are deep inside Models/Animals/Anl_Chicken/ or Levels/Alc/ but
        // textures are in a sibling tree (Textures/, pre/).
        var candidate = commonRoot;
        for (var depth = 0; depth < 3; depth++)
        {
            if (IsGameRoot(candidate))
                return candidate;

            var parent = Path.GetDirectoryName(candidate);
            if (parent == null || parent == candidate) break;
            candidate = parent;
        }

        // Fallback: use the widened candidate (3 levels up from common root)
        return candidate;
    }

    private static bool IsSameOrDescendant(string ancestor, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return IsSameOrDescendant(ancestor, path, comparison);
    }

    internal static bool IsSameOrDescendant(string ancestor, string path, StringComparison comparison)
    {
        if (!path.StartsWith(ancestor, comparison))
            return false;

        if (path.Length == ancestor.Length || Path.EndsInDirectorySeparator(ancestor))
            return true;

        var boundary = path[ancestor.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    ///     Checks if a directory looks like a game/WAD root by containing
    ///     known landmark subdirectories.
    /// </summary>
    private static bool IsGameRoot(string dir)
    {
        string[] landmarks =
        [
            "Models", "Textures", "Levels", "Skeletons", "pre", "Pre",
            "TEX", "SKE", "SKIN", "MDL", "GEOM"
        ];

        var matchCount = 0;
        foreach (var landmark in landmarks)
        {
            if (Directory.Exists(Path.Combine(dir, landmark)))
                matchCount++;
            if (matchCount >= 2) return true;
        }

        return false;
    }

    private static string? TryMatchInDir(string dir, string stem, ReadOnlySpan<string> extensions)
    {
        if (!Directory.Exists(dir)) return null;

        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        if (OperatingSystem.IsWindows())
            return null;

        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var ext in extensions)
        {
            var requestedName = stem + ext;
            string? match = null;
            foreach (var file in files)
            {
                if (!Path.GetFileName(file).Equals(
                        requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match != null)
                    return null;
                match = file;
            }

            if (match != null)
                return match;
        }

        return null;
    }

    private static string? FindDirectory(string parent, string name)
    {
        var exact = Path.Combine(parent, name);
        if (Directory.Exists(exact))
            return exact;
        if (OperatingSystem.IsWindows())
            return null;
        if (!Directory.Exists(parent))
            return null;

        string? match = null;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                if (!Path.GetFileName(directory).Equals(
                        name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match != null)
                    return null;
                match = directory;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return match;
    }
}
