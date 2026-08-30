using System.Buffers.Binary;
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

        return NdsModelSetManifest.Locate(CodeRegions(cart), geometry)
            .ToDictionary(m => m.IdA);
    }

    /// <summary>
    ///     Recovers each model set's authored name from the same code images, by
    ///     re-hashing the strings they hold — see <see cref="NdsSetNames" />. Empty
    ///     for anything that is not a cart.
    /// </summary>
    /// <param name="cart">The opened <c>.nds</c>.</param>
    /// <param name="container">The cart's opened GOB — the ids to name.</param>
    public static IReadOnlyDictionary<uint, string> ReadSetNames(
        IArchiveFileSystem cart, IArchiveFileSystem container)
    {
        var geometry = GroupGeometry(container);
        return geometry.Count == 0
            ? new Dictionary<uint, string>()
            : NdsSetNames.Harvest(CodeRegions(cart), geometry.Keys);
    }

    /// <summary>
    ///     Binds a model piece to the single clip it owns, for the carts that spell
    ///     animation with one opaque id per model rather than an indexed library.
    ///
    ///     Those ids are recoverable no other way: they hash onto no authored name
    ///     (measured over every printable run in ARM9 and the overlays — 0 of 322 and
    ///     0 of 467, while the identical sweep names every geometry set and piece) and
    ///     they coincide with no geometry id. But the code that asks for the file has
    ///     to hold the id, and it holds it inside the record that also carries the
    ///     geometry pair — three and two words ahead of it.
    ///
    ///     Fail-closed on both halves. An id is used only if it occurs EXACTLY once
    ///     across every code image, and only if the pair ahead of it is one the
    ///     container really holds. Measured: 322 of 322 Downhill Jam ids and 467 of
    ///     467 Proving Ground ids satisfy both, forming a bijection — one clip per
    ///     piece, nothing left over on either side — while values drawn at random
    ///     from the same word pool and pushed through the identical rule bind 0.
    /// </summary>
    /// <param name="cart">The opened <c>.nds</c>.</param>
    /// <param name="container">The cart's opened GOB.</param>
    public static IReadOnlyDictionary<(uint IdA, uint IdB), uint> ReadAnimationBindings(
        IArchiveFileSystem cart, IArchiveFileSystem container)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(container);

        var pairs = new HashSet<(uint, uint)>();
        var animationIds = new HashSet<uint>();
        foreach (var entry in container.Entries)
        {
            var name = GobNames.TryResolve(entry.Crc);
            if (NdsModelSet.TryParseGeometryName(name, out var idA, out var idB))
                pairs.Add((idA, idB));
            else if (NdsModelSet.TryParseAnimationName(name, out var animationId))
                animationIds.Add(animationId);
        }

        if (pairs.Count == 0 || animationIds.Count == 0)
            return new Dictionary<(uint, uint), uint>();

        // One pass per image, recording where each id is and how often.
        var sites = new Dictionary<uint, (uint IdA, uint IdB)?>();
        var seen = new HashSet<uint>();
        var ambiguous = new HashSet<uint>();
        foreach (var (_, _, data) in CodeRegions(cart))
        {
            var words = data.Length / 4;
            for (var i = 3; i < words; i++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
                if (!animationIds.Contains(value))
                    continue;
                if (!seen.Add(value))
                {
                    ambiguous.Add(value);
                    continue;
                }

                var idA = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((i - 3) * 4));
                var idB = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((i - 2) * 4));
                sites[value] = pairs.Contains((idA, idB)) ? (idA, idB) : null;
            }
        }

        // The binding is a bijection in both shipped carts, so a piece claimed twice
        // means the reading is wrong for that row; drop it rather than pick one.
        var claims = new Dictionary<(uint, uint), uint>();
        var contested = new HashSet<(uint, uint)>();
        foreach (var (animationId, target) in sites)
        {
            if (target == null || ambiguous.Contains(animationId))
                continue;
            if (!claims.TryAdd(target.Value, animationId))
                contested.Add(target.Value);
        }

        foreach (var target in contested)
            claims.Remove(target);
        return claims;
    }

    /// <summary>ARM9 and every ARM9 overlay, as raw images.</summary>
    private static List<(string Name, uint VirtualBase, byte[] Data)> CodeRegions(
        IArchiveFileSystem cart)
    {
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
                // A region that will not read simply contributes nothing.
            }
        }

        return regions;
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
