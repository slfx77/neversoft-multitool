using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Reads a cart's model-set manifests: the ARM9 and overlay tables that declare
///     which geometry pieces belong to each set, paired with the container grouping
///     they have to reproduce.
///
///     The manifests live in code, so they are only reachable when the caller has the
///     CART open. A bare extracted <c>.gob</c> carries no code and gets nothing —
///     which is why everything the manifests provide (authored piece names, the
///     "declares no texture bank" signal, the Downhill Jam / Proving Ground animation
///     binding) is additive on top of a pipeline that already works without them.
/// </summary>
public static class NdsCartManifests
{
    private const string SystemDirectory = "_system";

    /// <summary>
    ///     Reads every manifest in <paramref name="cart" />, keyed by model-set id.
    ///     Returns an empty map for anything that is not a DS cart, or a cart whose
    ///     code holds no table matching the container.
    /// </summary>
    /// <param name="cart">The opened <c>.nds</c>.</param>
    /// <param name="container">The cart's opened GOB — the grouping the tables must reproduce.</param>
    public static IReadOnlyDictionary<uint, NdsModelSetManifest> Read(
        IArchiveFileSystem cart, IArchiveFileSystem container)
    {
        var geometry = GroupGeometry(container);
        if (geometry.Count == 0)
            return new Dictionary<uint, NdsModelSetManifest>();

        var regions = new List<(string, uint, byte[])>();
        foreach (var entry in cart.Entries)
        {
            if (!string.Equals(entry.Directory, SystemDirectory, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.Name.StartsWith("arm9", StringComparison.OrdinalIgnoreCase)
                && !entry.Name.StartsWith("overlay9", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                // The load address is left to be derived from the tables' own name
                // pointers, so no ROM header parsing is needed here.
                regions.Add((entry.Name, 0u, cart.ReadEntry(entry)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
            {
                // A region that will not read simply contributes no manifests.
            }
        }

        return NdsModelSetManifest.Locate(regions, geometry)
            .ToDictionary(m => m.IdA);
    }

    /// <summary>idA to the idBs of the geometry files that share it.</summary>
    private static Dictionary<uint, IReadOnlyCollection<uint>> GroupGeometry(
        IArchiveFileSystem container)
    {
        var sets = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in container.Entries)
        {
            if (!NdsModelSet.TryParseGeometryName(GobNames.TryResolve(entry.Crc), out var idA, out var idB))
                continue;
            if (!sets.TryGetValue(idA, out var list))
                sets[idA] = list = [];
            list.Add(idB);
        }

        return sets.ToDictionary(s => s.Key, s => (IReadOnlyCollection<uint>)s.Value);
    }
}
