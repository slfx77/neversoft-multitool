using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Redirects level-object-bank placements to the sibling <c>items.psx</c>
///     when a bank mesh shares its name hash with an items model. Pickups and
///     markers are engine entities bound to the spooled "items" region — the
///     spidey-decomp PC port shows <c>Spidey_CIcon</c> calling
///     <c>InitItem("items")</c> with model 5 for the in-world "?" marker — so
///     the items copy is what the game draws. The bank copy is an
///     authoring-time duplicate whose palette carries a placeholder pure
///     R→G→B cycler, while items.psx owns the real look (the "?" marker's
///     staggered blue pulse). items.psx parses as a standalone prop file, so
///     its colour pulses bake (its raw palette is black for pulsed entries).
///     Bank pickups a POWERUP node already places are suppressed instead (see
///     <see cref="PsxPowerupPlacementResolver" />); only pickups with no POWERUP
///     placement (e.g. the demo level lda1's "?") still redirect here.
/// </summary>
internal static class PsxItemsBankSubstitution
{
    private const string ItemsFileName = "items.psx";

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        EmptyItems = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>();

    /// <summary>
    ///     Loads the sibling <c>items.psx</c> (the spooled "items" region shared
    ///     across every level) and its texture provider, or null when absent /
    ///     malformed / not a plain model file. Shared by the bank substitution
    ///     and the POWERUP placement layer so items.psx parses once per level.
    /// </summary>
    internal static LoadedItems? TryLoadItems(AssetSource source)
    {
        try
        {
            var itemsBytes = source.TryReadCompanion(ItemsFileName);
            if (itemsBytes == null)
                return null;
            var itemsFile = PsxMeshFile.Parse(itemsBytes);
            if (itemsFile == null
                || itemsFile.Meshes.Count == 0
                || PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(itemsFile))
            {
                return null;
            }

            return new LoadedItems(
                itemsFile,
                MeshCompanionResolver.BuildPsxTextureProvider(source, ItemsFileName, itemsBytes));
        }
        catch
        {
            // items.psx only enriches placements; a missing or malformed
            // sibling must not break the level conversion.
            return null;
        }
    }

    /// <summary>
    ///     Splits bank placements into those redirected onto items.psx object
    ///     indices (mesh name hash shared with an items model) and those the
    ///     bank keeps. Bank objects whose mesh hash is in
    ///     <paramref name="suppressHashes" /> are DROPPED entirely: the POWERUP
    ///     layer is authoritative for pickups, so a bank pickup the POWERUP nodes
    ///     already place is a duplicate. A bank pickup with no POWERUP placement
    ///     (e.g. the demo level lda1's "?") is not suppressed and still redirects
    ///     to the items copy for correct rendering. Returns null when nothing is
    ///     redirected AND nothing is suppressed.
    /// </summary>
    internal static (IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> ItemsPlacements,
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> RemainingBankPlacements)?
        Split(
            PsxMeshFile itemsFile,
            PsxMeshFile objectBank,
            IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> bankPlacements,
            IReadOnlySet<uint>? suppressHashes = null)
    {
        var itemsObjectByHash = new Dictionary<uint, int>();
        for (var objectIndex = 0; objectIndex < itemsFile.Objects.Count; objectIndex++)
        {
            var meshIndex = itemsFile.Objects[objectIndex].MeshIndex;
            if (meshIndex >= itemsFile.MeshNameHashes.Length)
                continue;
            itemsObjectByHash.TryAdd(itemsFile.MeshNameHashes[meshIndex], objectIndex);
        }

        if (itemsObjectByHash.Count == 0)
            return null;

        Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>? items = null;
        var remaining = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>();
        var suppressedAny = false;
        foreach (var (bankObjectIndex, placements) in bankPlacements)
        {
            var hash = GetBankMeshHash(objectBank, bankObjectIndex);
            if (hash != 0 && suppressHashes?.Contains(hash) == true)
            {
                suppressedAny = true; // POWERUP layer owns this pickup — drop the bank duplicate.
            }
            else if (hash != 0 && itemsObjectByHash.TryGetValue(hash, out var itemsObjectIndex))
            {
                items ??= [];
                items[itemsObjectIndex] = items.TryGetValue(itemsObjectIndex, out var existing)
                    ? [.. existing, .. placements]
                    : placements;
            }
            else
            {
                remaining[bankObjectIndex] = placements;
            }
        }

        return items == null && !suppressedAny ? null : (items ?? EmptyItems, remaining);
    }

    private static uint GetBankMeshHash(PsxMeshFile objectBank, int objectIndex)
    {
        if (objectIndex >= objectBank.Objects.Count)
            return 0;
        var meshIndex = objectBank.Objects[objectIndex].MeshIndex;
        return meshIndex < objectBank.MeshNameHashes.Length
            ? objectBank.MeshNameHashes[meshIndex]
            : 0;
    }

    internal sealed record LoadedItems(
        PsxMeshFile File,
        MeshChecksumTextureResolver? TextureProvider);
}
