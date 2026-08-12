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
///         Files whose complete output-name set is unique in the batch keep the flat
///         <c>&lt;output&gt;/&lt;stem&gt;</c> layout they have always had. Only
///         records participating in an output-name collision are relocated, into a
///         subdirectory mirroring their source path, so no existing single-file or
///         well-behaved batch workflow changes.
///     </para>
/// </summary>
public static class MeshOutputPathPlanner
{
    /// <summary>One input file's collision-safe output location.</summary>
    /// <param name="File">The input file (or <c>archive::entry</c> virtual path).</param>
    /// <param name="Subdirectory">
    ///     Relative directory under the output root, or an empty string for the
    ///     flat layout.
    /// </param>
    /// <param name="Stem">The output name, without extension.</param>
    public readonly record struct PlannedOutput(string File, string Subdirectory, string Stem);

    /// <summary>
    ///     Plans one output location per file. Guarantees a bijection: two inputs
    ///     never share a (subdirectory, stem) pair. Ownership is deterministic,
    ///     while the returned plan retains the caller's file order.
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
        return Plan(
            files,
            stemOf,
            static (_, proposedStem) => [proposedStem],
            inputRoot);
    }

    /// <summary>
    ///     Plans records that can emit more than one output stem. The callback is
    ///     evaluated for the preferred stem and again for any ordinal alternative,
    ///     so derived names such as <c>foo_mip1</c> follow a relocated
    ///     <c>foo_2</c> as <c>foo_2_mip1</c>. The returned plan retains the
    ///     caller's file order.
    /// </summary>
    /// <param name="files">The batch, in any order.</param>
    /// <param name="stemOf">The preferred base output stem for each file.</param>
    /// <param name="outputStemsOf">
    ///     Every output stem a file will write for a proposed base stem. The set
    ///     must include that proposed base, be non-empty, and be internally unique
    ///     under ordinal-ignore-case comparison.
    /// </param>
    /// <param name="inputRoot">
    ///     The directory the batch was enumerated from, used to build mirrored
    ///     subdirectories. Null falls back to each file's immediate parent.
    /// </param>
    public static IReadOnlyList<PlannedOutput> Plan(
        IReadOnlyList<string> files,
        Func<string, string> stemOf,
        Func<string, string, IReadOnlyList<string>> outputStemsOf,
        string? inputRoot)
    {
        // Sort before making any ownership decision. Filesystem enumeration order
        // must not decide which record keeps a preferred output name.
        var candidates = files
            .Select((file, index) => new PlanningCandidate(index, file, stemOf(file)))
            .OrderBy(static candidate => candidate.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.File, StringComparer.Ordinal)
            .ToList();

        var flatOutputStems = candidates
            .Select(candidate => GetOutputStems(
                candidate.File,
                candidate.PreferredStem,
                outputStemsOf))
            .ToList();

        var flatAliasCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var outputStems in flatOutputStems)
        {
            foreach (var outputStem in outputStems)
                flatAliasCounts[outputStem] = flatAliasCounts.GetValueOrDefault(outputStem) + 1;
        }

        var naturalPlans = new List<NaturalPlan>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var participatesInFlatAlias = flatOutputStems[index]
                .Any(outputStem => flatAliasCounts[outputStem] > 1);
            var subdirectory = participatesInFlatAlias
                ? MirroredSubdirectory(candidate.File, inputRoot)
                : "";
            naturalPlans.Add(new NaturalPlan(
                candidate.OriginalIndex,
                candidate.File,
                candidate.PreferredStem,
                subdirectory,
                flatOutputStems[index]));
        }

        // Reserve all natural aliases before allocating any ordinal. Otherwise an
        // early `foo_2` backstop can steal a later record's preferred `foo_2`.
        var reservedPreferredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var natural in naturalPlans)
            reservedPreferredKeys.UnionWith(OutputKeys(natural.Subdirectory, natural.OutputStems));

        var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new PlannedOutput[naturalPlans.Count];
        foreach (var natural in naturalPlans)
        {
            var preferredKeys = OutputKeys(natural.Subdirectory, natural.OutputStems);
            if (preferredKeys.All(key => !assignedKeys.Contains(key)))
            {
                assignedKeys.UnionWith(preferredKeys);
                planned[natural.OriginalIndex] = new PlannedOutput(
                    natural.File,
                    natural.Subdirectory,
                    natural.PreferredStem);
                continue;
            }

            // A malformed callback can retain one fixed conflicting alias for every
            // proposed base. Keep allocation fail-closed and bounded instead of
            // spinning forever. Well-formed base-derived aliases need at most one
            // attempt per already reserved/assigned name, plus the batch size.
            var maxOrdinalAttempts = (long)reservedPreferredKeys.Count
                                     + assignedKeys.Count
                                     + naturalPlans.Count
                                     + 1;
            string? allocatedStem = null;
            IReadOnlyList<string>? allocatedKeys = null;
            for (var attempt = 0L; attempt < maxOrdinalAttempts; attempt++)
            {
                var ordinal = attempt + 2;
                var proposedStem = $"{natural.PreferredStem}_{ordinal}";
                var proposedOutputStems = GetOutputStems(
                    natural.File,
                    proposedStem,
                    outputStemsOf);
                var proposedKeys = OutputKeys(natural.Subdirectory, proposedOutputStems);
                if (proposedKeys.Any(assignedKeys.Contains)
                    || proposedKeys.Any(reservedPreferredKeys.Contains))
                {
                    continue;
                }

                allocatedStem = proposedStem;
                allocatedKeys = proposedKeys;
                break;
            }

            if (allocatedStem is null || allocatedKeys is null)
            {
                throw new InvalidOperationException(
                    $"Unable to allocate a unique output stem for '{natural.File}'.");
            }

            assignedKeys.UnionWith(allocatedKeys);
            planned[natural.OriginalIndex] = new PlannedOutput(
                natural.File,
                natural.Subdirectory,
                allocatedStem);
        }

        return planned;
    }

    private static IReadOnlyList<string> GetOutputStems(
        string file,
        string proposedStem,
        Func<string, string, IReadOnlyList<string>> outputStemsOf)
    {
        var outputStems = outputStemsOf(file, proposedStem)
                          ?? throw new ArgumentException(
                              "The output-stem callback returned null.",
                              nameof(outputStemsOf));
        if (outputStems.Count == 0)
        {
            throw new ArgumentException(
                "Every planned file must have at least one output stem.",
                nameof(outputStemsOf));
        }

        var distinct = outputStems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (outputStems.Any(string.IsNullOrWhiteSpace)
            || distinct.Count != outputStems.Count)
        {
            throw new ArgumentException(
                "A file's output stems must be non-empty and unique.",
                nameof(outputStemsOf));
        }

        if (!distinct.Contains(proposedStem))
        {
            throw new ArgumentException(
                "A file's output stems must include its proposed base stem.",
                nameof(outputStemsOf));
        }

        return [.. outputStems];
    }

    private static IReadOnlyList<string> OutputKeys(
        string subdirectory,
        IReadOnlyList<string> outputStems)
    {
        return [.. outputStems.Select(outputStem => Path.Combine(subdirectory, outputStem))];
    }

    private readonly record struct PlanningCandidate(
        int OriginalIndex,
        string File,
        string PreferredStem);

    private readonly record struct NaturalPlan(
        int OriginalIndex,
        string File,
        string PreferredStem,
        string Subdirectory,
        IReadOnlyList<string> OutputStems);

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
