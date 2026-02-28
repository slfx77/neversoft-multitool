namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     A single bone/part entry from a PSH header file.
/// </summary>
public sealed class PshBone
{
    /// <summary>Bone name extracted from the #define after PART_ prefix (lowercased).</summary>
    public required string Name { get; init; }

    /// <summary>Zero-based index from the #define value. Corresponds to object index in PSX file.</summary>
    public required int Index { get; init; }

    /// <summary>Parent bone name from the "//   parent:" comment, or null for root bones.</summary>
    public string? ParentName { get; init; }
}

/// <summary>
///     Parsed PSH (C preprocessor header) file containing bone hierarchy definitions.
///     PSH files define the skeleton structure for character models as #define PART_ entries
///     with parent comments. Found alongside PSX model files with the same stem.
///     Format: <c>#define HAWK2PART_HAWK_PELVIS 0</c> followed by <c>//   parent: Scene Root</c>.
/// </summary>
public sealed class PshFile
{
    private readonly Dictionary<int, string> _namesByIndex;

    private PshFile(IReadOnlyList<PshBone> bones)
    {
        Bones = bones;
        _namesByIndex = [];
        foreach (var bone in bones)
            _namesByIndex.TryAdd(bone.Index, bone.Name);
    }

    public required IReadOnlyList<PshBone> Bones { get; init; }

    /// <summary>
    ///     Returns the bone name for the given object index, or null if not found.
    /// </summary>
    public string? GetBoneName(int objectIndex)
    {
        return _namesByIndex.GetValueOrDefault(objectIndex);
    }

    /// <summary>
    ///     Parses a PSH header file. Returns null if the file has no PART_ entries.
    /// </summary>
    public static PshFile? Parse(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var lines = File.ReadAllLines(filePath);
        var bones = new List<PshBone>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (!line.StartsWith("#define ", StringComparison.Ordinal))
                continue;

            // Find PART_ prefix: #define HAWK2PART_HAWK_PELVIS  0
            var partIdx = line.IndexOf("PART_", StringComparison.Ordinal);
            if (partIdx < 0)
                continue;

            // Extract bone name (everything after PART_ until whitespace)
            var afterPart = line[(partIdx + 5)..];
            var endIdx = afterPart.IndexOfAny([' ', '\t']);
            if (endIdx <= 0)
                continue;

            var boneName = afterPart[..endIdx].ToLowerInvariant();

            // Extract index number (after the name, skip whitespace)
            var indexStr = afterPart[endIdx..].Trim();
            if (!int.TryParse(indexStr, out var index))
                continue;

            // Check next line for parent comment: "//   parent: hawk_pelvis"
            var parentName = i + 1 < lines.Length
                ? TryExtractParentName(lines[i + 1])
                : null;

            bones.Add(new PshBone
            {
                Name = boneName,
                Index = index,
                ParentName = parentName
            });
        }

        return bones.Count > 0
            ? new PshFile(bones) { Bones = bones }
            : null;
    }

    /// <summary>
    ///     Attempts to find and parse a companion PSH file for a PSX model file.
    ///     Looks for a file with the same stem and .psh extension (case-insensitive).
    /// </summary>
    public static PshFile? FindCompanion(string psxFilePath)
    {
        var dir = Path.GetDirectoryName(psxFilePath);
        if (dir == null)
            return null;

        var stem = Path.GetFileNameWithoutExtension(psxFilePath);
        var candidates = Directory.GetFiles(dir, stem + ".psh",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });

        return candidates.Length > 0 ? Parse(candidates[0]) : null;
    }

    private static string? TryExtractParentName(string line)
    {
        const string prefix = "//   parent: ";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var candidate = trimmed[prefix.Length..].Trim();
        return candidate.Equals("Scene Root", StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate.ToLowerInvariant();
    }
}
