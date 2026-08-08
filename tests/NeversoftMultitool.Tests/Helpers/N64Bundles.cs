using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests;

/// <summary>
///     Finds a carved N64 model bundle by its SLOT rather than its file name.
///     <para>
///         A bundle's file name carries the slot plus, where content identified
///         it, the original PS1 file name (<c>models/008/008_c_kart.psx.n64</c>)
///         — and an unresolved bundle carries only the slot. The slot is the
///         stable identity: it is the game's own asset-id space and does not
///         move when the name dictionary is regenerated. Tests address bundles
///         through here so they never encode the naming scheme, which is exactly
///         what made the 2026-08-07 rename touch eight test files.
///     </para>
/// </summary>
internal static class N64Bundles
{
    /// <summary>
    ///     The <c>.psx.n64</c> shell in <c>models/&lt;slot&gt;</c>. Throws with the
    ///     slot named when it is absent, which reads far better than a null-deref
    ///     on <c>FindByPath</c>.
    /// </summary>
    internal static ArchiveEntry FindBundle(ArchiveAssetBackend backend, string slot)
    {
        var directory = $"models/{slot}";
        foreach (var entry in backend.Entries)
        {
            if (entry.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)
                && entry.Name.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"no carved bundle shell in {directory}");
    }

    /// <summary>An <see cref="ArchiveAssetSource" /> over that shell.</summary>
    internal static ArchiveAssetSource OpenBundle(ArchiveAssetBackend backend, string slot)
    {
        return new ArchiveAssetSource(backend, FindBundle(backend, slot));
    }
}
