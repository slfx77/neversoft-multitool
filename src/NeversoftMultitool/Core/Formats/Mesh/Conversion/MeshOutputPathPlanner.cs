using System.Text;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Assigns an output location per input file so a batch conversion cannot lose
///     work to name collisions.
///     <para>
///         Game trees reuse asset names heavily — 1,678 bare <c>.col</c> files
///         across Sample/Builds share only 287 stems (1,228 of them are literally
///         named <c>mission.col</c>), and 4,974 <c>.skin.xbx</c> share 729. Writing
///         every one to <c>&lt;output&gt;/&lt;stem&gt;.glb</c> silently overwrites
///         all but the last.
///     </para>
///     <para>
///         Files whose stem is unique in the batch keep the flat
///         <c>&lt;output&gt;/&lt;stem&gt;</c> layout they have always had. Only
///         colliding stems are relocated, into a subdirectory mirroring their
///         source path, so no existing single-file or well-behaved batch workflow
///         changes.
///     </para>
/// </summary>
public static class MeshOutputPathPlanner
{
    /// <param name="File">The input file (or <c>archive::entry</c> virtual path).</param>
    /// <param name="Subdirectory">
    ///     Relative directory under the output root, or an empty string for the
    ///     flat layout.
    /// </param>
    /// <param name="Stem">The output name, without extension.</param>
    public readonly record struct PlannedOutput(string File, string Subdirectory, string Stem);

    /// <summary>
    ///     Plans one output location per file. Guarantees a bijection: two inputs
    ///     never share a (subdirectory, stem) pair.
    /// </summary>
    /// <param name="files">The batch, in any order.</param>
    /// <param name="stemOf">The caller's stem rule (usually MeshTypeDetector.GetStem).</param>
    /// <param name="inputRoot">
    ///     The directory the batch was enumerated from, used to build the mirrored
    ///     subdirectory. Null falls back to the file's own parent directory name.
    /// </param>
    public static IReadOnlyList<PlannedOutput> Plan(
        IReadOnlyList<string> files,
        Func<string, string> stemOf,
        string? inputRoot)
    {
        var planned = new List<PlannedOutput>(files.Count);
        var byStem = files
            .GroupBy(stemOf, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in byStem)
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                planned.Add(new PlannedOutput(members[0], "", group.Key));
                continue;
            }

            // Deterministic ordering so the same batch always plans the same way,
            // whatever order the filesystem enumerated it in.
            members.Sort(StringComparer.OrdinalIgnoreCase);

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in members)
            {
                var subdirectory = MirroredSubdirectory(file, inputRoot);
                var stem = group.Key;

                // A mirrored path is usually unique on its own; the ordinal suffix
                // is the backstop for the rest (same stem, same directory, e.g.
                // two entries of one archive).
                var key = Path.Combine(subdirectory, stem);
                if (!taken.Add(key))
                {
                    var ordinal = 2;
                    while (!taken.Add(Path.Combine(subdirectory, $"{group.Key}_{ordinal}")))
                        ordinal++;
                    stem = $"{group.Key}_{ordinal}";
                }

                planned.Add(new PlannedOutput(file, subdirectory, stem));
            }
        }

        return planned;
    }

    /// <summary>
    ///     The source directory of <paramref name="file" /> relative to
    ///     <paramref name="inputRoot" />, sanitized into a safe relative path.
    ///     Archive virtual paths (<c>archive::entry</c>) contribute both halves.
    /// </summary>
    private static string MirroredSubdirectory(string file, string? inputRoot)
    {
        var path = file.Replace("::", "/").Replace('\\', '/');
        var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";

        if (!string.IsNullOrEmpty(inputRoot))
        {
            var root = inputRoot.Replace('\\', '/').TrimEnd('/');
            if (directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                directory = directory[root.Length..].TrimStart('/');
        }
        else
        {
            // No root to be relative to — the immediate parent still separates the
            // common "one asset per level directory" case.
            directory = Path.GetFileName(directory);
        }

        return Sanitize(directory);
    }

    /// <summary>
    ///     Strips drive letters, rooted prefixes, "..", and characters the
    ///     filesystem rejects, so the result can always be combined under an
    ///     output root.
    /// </summary>
    private static string Sanitize(string relativeDirectory)
    {
        if (string.IsNullOrEmpty(relativeDirectory))
            return "";

        var invalid = Path.GetInvalidFileNameChars();
        var parts = new List<string>();
        foreach (var segment in relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                continue;

            var builder = new StringBuilder(segment.Length);
            foreach (var c in segment)
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            var cleaned = builder.ToString().Trim();
            if (cleaned.Length > 0)
                parts.Add(cleaned);
        }

        return parts.Count == 0 ? "" : Path.Combine([.. parts]);
    }
}
