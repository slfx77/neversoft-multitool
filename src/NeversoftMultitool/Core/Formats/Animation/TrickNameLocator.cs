using System.Collections.Concurrent;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Finds the <c>tricks.bin</c> that names a PSX animation bank's slots.
///     <para>
///         The pairing is positional, not by name: a build ships one trick table
///         beside its skater bank (<c>CD/tricks.bin</c> next to
///         <c>CD/sk2anim.psx</c>), and nothing inside either file references the
///         other. What keeps that safe is
///         <see cref="TrickAnimationNames.BuildForBank" />, which declines the
///         whole map unless every slot a trick references exists in the bank —
///         so a table paired with an unrelated bank names nothing rather than
///         mislabelling it.
///     </para>
/// </summary>
public static class TrickNameLocator
{
    private const string TricksFileName = "tricks.bin";

    /// <summary>How far up from the bank to look for the table.</summary>
    private const int MaxAncestorDepth = 2;

    private static readonly ConcurrentDictionary<string, TricksFile?> Cache = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Slot names for a bank, or an empty map when no compatible trick table
    ///     sits beside it. Only slots exactly one trick owns are named.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ForBank(AssetSource bankSource, int slotCount)
    {
        if (slotCount <= 0) return EmptyNames;

        var tricks = Locate(bankSource);
        return tricks == null
            ? EmptyNames
            : TrickAnimationNames.BuildForBank(tricks, slotCount);
    }

    /// <summary>
    ///     Names a slot, falling back to the synthetic <c>anim_N</c> label. This
    ///     is the shape every caller wants, so they do not each re-implement the
    ///     fallback.
    /// </summary>
    public static Func<int, string> NameFactory(
        IReadOnlyDictionary<int, string> slotNames, string? prefix = null)
    {
        return index =>
        {
            var name = slotNames.TryGetValue(index, out var trick) ? trick : $"anim_{index}";
            return string.IsNullOrEmpty(prefix) ? name : $"{prefix}{name}";
        };
    }

    private static readonly Dictionary<int, string> EmptyNames = [];

    private static TricksFile? Locate(AssetSource bankSource)
    {
        if (bankSource.FileSystemPath is { } fsPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(fsPath));
            for (var depth = 0; depth <= MaxAncestorDepth && !string.IsNullOrEmpty(dir);
                 depth++, dir = Path.GetDirectoryName(dir))
            {
                var candidate = Path.Combine(dir, TricksFileName);
                var found = Cache.GetOrAdd(candidate, static path =>
                {
                    try
                    {
                        return File.Exists(path)
                            ? TricksFile.Parse(File.ReadAllBytes(path))
                            : null;
                    }
                    catch
                    {
                        return null;
                    }
                });
                if (found != null) return found;
            }

            return null;
        }

        if (bankSource is not ArchiveAssetSource archiveSource) return null;

        var key = $"{archiveSource.Backend.GetHashCode()}::{TricksFileName}";
        return Cache.GetOrAdd(key, _ =>
        {
            try
            {
                var entry = archiveSource.Backend.Entries.FirstOrDefault(e =>
                    Path.GetFileName(e.Name)
                        .Equals(TricksFileName, StringComparison.OrdinalIgnoreCase));
                return entry == null
                    ? null
                    : TricksFile.Parse(new ArchiveAssetSource(archiveSource.Backend, entry).ReadBytes());
            }
            catch
            {
                return null;
            }
        });
    }
}
