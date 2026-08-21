using System.Collections.Concurrent;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Finds the <c>tricks.bin</c> a carved N64 cart carries, so its skater
///     animation slots get their real trick names.
///     <para>
///         The cart ships the same table the disc does, but the carve has no
///         name for it — it is an ordinary payload emitted as
///         <c>misc/&lt;slot&gt;.bin</c>, and the slot ordinal differs per ROM. So
///         unlike <see cref="TrickNameLocator" />, which finds the disc's file
///         by name, this locates the table by PARSING: exactly one carved asset
///         per cart is a credible trick table, which is what
///         <see cref="TricksFile.Parse" />'s own credibility gate establishes.
///     </para>
///     <para>
///         Parsing thousands of assets would be wasteful, so a cheap byte-level
///         pre-filter runs first. Results are cached per cart because a batch
///         convert opens the same carve once per bundle.
///     </para>
/// </summary>
internal static class N64TrickTableLocator
{
    /// <summary>
    ///     Directories the table provably is not in — the three bulk roles the
    ///     carver classifies by signature. Skipping them turns a ~4,000-asset
    ///     sweep into a few hundred, and drops the one known false positive (a
    ///     430 KB <c>group2</c> render bank). Deliberately a SKIP list rather
    ///     than an allow list: an unrecognised role directory is still scanned,
    ///     because the table is emitted as an unclassified payload and the
    ///     directory it lands in is role-derived, not fixed.
    /// </summary>
    private static readonly string[] SkippedRoles = ["group2", "textures", "models"];

    /// <summary>
    ///     Size band a trick table can fall in. The shipped tables are 13 KB
    ///     (prototype) to 34 KB; the bound is loose because its only job is to
    ///     skip the render banks and audio blobs that dominate a carve.
    /// </summary>
    private const int MinimumSize = 4 * 1024;

    private const int MaximumSize = 256 * 1024;

    /// <summary>
    ///     Fewest name records a candidate must show before it is worth
    ///     parsing. Matches the parser's own minimum trick count, so the filter
    ///     can never reject something the parser would have accepted.
    /// </summary>
    private const int MinimumNameRecords = 32;

    private static readonly ConcurrentDictionary<string, TricksFile?> Cache = new(
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, string> EmptyNames = [];

    /// <summary>
    ///     Trick names for a carved bundle's animation slots, or an empty map
    ///     when the cart carries no compatible table. Only slots exactly one
    ///     trick owns are named.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ForBundle(AssetSource bundleSource, int slotCount)
    {
        if (slotCount <= 0)
            return EmptyNames;

        var tricks = Locate(bundleSource);
        return tricks == null
            ? EmptyNames
            // EXACT fit, not "every slot fits". A cart holds shells with as
            // many as 300 clips, and the loose gate would let any of them
            // swallow a 218-slot table's names.
            : TrickAnimationNames.BuildForExactBank(tricks, slotCount);
    }

    private static TricksFile? Locate(AssetSource source)
    {
        try
        {
            if (source is ArchiveAssetSource archive)
            {
                return Cache.GetOrAdd(
                    $"n64::{archive.Backend.ArchivePath}", _ => ScanArchive(archive));
            }

            var root = N64ModelCompanions.TryFindCarveRoot(source);
            // The factory argument is the cache KEY, not the root — closing
            // over `root` rather than taking the parameter is load-bearing.
            return root == null ? null : Cache.GetOrAdd($"n64::{root}", _ => ScanDirectory(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Exactly one carved asset per cart may be a credible table. A second
    ///     match means the identification is not safe, so the whole thing is
    ///     declined rather than picking one — the same fail-closed shape the
    ///     boot-image name tables use.
    /// </summary>
    private static TricksFile? SelectSingleCredible(IEnumerable<byte[]> candidates)
    {
        TricksFile? found = null;
        foreach (var data in candidates)
        {
            if (TryParse(data) is not { } tricks)
                continue;
            if (found != null)
                return null;
            found = tricks;
        }

        return found;
    }

    private static TricksFile? ScanArchive(ArchiveAssetSource archive)
    {
        return SelectSingleCredible(archive.Backend.Entries
            .Where(static entry =>
                IsCandidateRole($"{entry.Directory}/{entry.Name}") && IsCandidateSize(entry.Size))
            .Select(archive.Backend.ReadEntryBytes));
    }

    private static TricksFile? ScanDirectory(string carveRoot)
    {
        return SelectSingleCredible(
            Directory.EnumerateFiles(carveRoot, "*", SearchOption.AllDirectories)
                .Where(path => IsCandidateRole(
                                   Path.GetRelativePath(carveRoot, path).Replace('\\', '/'))
                               && IsCandidateSize(new FileInfo(path).Length))
                .Select(File.ReadAllBytes));
    }

    /// <summary>
    ///     Tests the ROLE — the first path segment — not the immediate parent.
    ///     A bundle sits at <c>models/NNN/</c>, whose immediate parent is the
    ///     slot number, so an immediate-parent test silently fails to skip the
    ///     largest role in the carve.
    /// </summary>
    private static bool IsCandidateRole(string relativePath)
    {
        var slash = relativePath.IndexOf('/');
        var role = slash < 0 ? "" : relativePath[..slash];
        return !SkippedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCandidateSize(long size)
    {
        return size is >= MinimumSize and <= MaximumSize;
    }

    private static TricksFile? TryParse(byte[] data)
    {
        return CouldHoldATrickTable(data) ? TricksFile.Parse(data) : null;
    }

    /// <summary>
    ///     Cheap structural pre-filter: a trick table opens at least
    ///     <see cref="MinimumNameRecords" /> tricks with a name record, and a
    ///     name record is the opcode <c>0x0B</c> followed by printable ASCII.
    ///     Counting those costs one pass and rejects ordinary payloads outright.
    /// </summary>
    private static bool CouldHoldATrickTable(ReadOnlySpan<byte> data)
    {
        var found = 0;
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] != 0x0B || data[i + 1] is < 0x20 or > 0x7E)
                continue;
            if (++found >= MinimumNameRecords)
                return true;
        }

        return false;
    }
}
