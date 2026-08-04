using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Places TRG POWERUP nodes as items.psx models across the engine lineage. A
///     POWERUP node (type 5) carries a numeric <c>pickupType</c> that the engine's
///     <c>CPowerUp</c> constructor stores as <c>mType</c> and feeds through a
///     <c>switch</c> to pick an items.psx mesh, then
///     <c>Spool_GetModel(hash, ItemsRegion)</c>. <c>Trig_CreateObject</c> passes
///     the node's pickupType straight into the ctor, so TRG pickupType == mType.
///     Two <c>pickupType → hash</c> table families are reverse-engineered:
///     <list type="bullet">
///         <item>
///             Spider-Man (<c>switch(mType-8)</c>, per-build tables from
///             <c>tools/diagnostics/spiderman_pickup_table_probe.py</c>);
///         </item>
///         <item>
///             THPS1/THPS2 (<c>switch(mType)</c>, SKATE letters + bonus/money,
///             taken verbatim from the matched THPS2 decomp
///             <c>CPowerUp::CPowerUp</c> — the letter/bonus hashes are byte-identical
///             to THPS1's items.psx, so the one table serves both, hash-keyed);
///         </item>
///         <item>
///             Apocalypse (a jump table keyed by mType-1, read from the matched
///             apocalypse_final.exe ctor by locating the "items" string and the
///             items.psx hash-load cluster - signature-matched, no SYM).
///         </item>
///     </list>
///     The right table is chosen at runtime from the sibling items.psx's mesh set
///     (see <see cref="SelectTable" />); unknown items resolve to no table. POWERUP
///     nodes carry no rotation, so placements are translation-only.
/// </summary>
internal static class PsxPowerupPlacementResolver
{
    // Spider-Man items.psx model identities (mesh-name hashes, stable across builds).
    private const uint WebCartridge = 0x17646B0D; // model 1 (blue gear)
    private const uint YellowGear = 0xC6739C3B; // model 2
    private const uint GreyGear = 0x7E74F3D4; // model 3
    private const uint QuestionMark = 0x7F648179; // model 5
    private const uint PanelModel6 = 0x12820A41; // model 6 (April-29 + final)
    private const uint PanelModel7 = 0xA092D785; // model 7 (final only)

    // THPS items.psx signature: the 'S' SKATE-letter model, present in both
    // THPS1 and THPS2 items.psx (distinguishes THPS from Spider-Man/Apocalypse).
    private const uint ThpsLetterS = 0x311D55D4;

    // Apocalypse items.psx signature: the pickupType-4 model, unique to
    // Apocalypse (its grey-gear model 0x7E74F3D4 is shared with the other games,
    // so it can't serve as the discriminator).
    private const uint ApocalypseItemsMarker = 0x350E968C;

    // pickupType -> items mesh-name hash, per build. Each entry is provenanced
    // by its ctor jump table (see spiderman_pickup_table_probe.py). No
    // pickupType ever maps to two DIFFERENT non-default models across builds,
    // and the census subset {8,11,14,15,16} is identical everywhere — the drift
    // is only in which extra types resolve, which the items.psx signature
    // (below) captures.
    private static readonly Dictionary<int, uint[]> FebProtoTable = new()
    {
        // ctor @0x800349CC, jumptable @0x800B03A4 (2000-2-18, 6-mesh items.psx)
        [8] = [WebCartridge], [9] = [YellowGear], [11] = [QuestionMark],
        [14] = [GreyGear], [15] = [GreyGear], [16] = [GreyGear]
    };

    private static readonly Dictionary<int, uint[]> AprProtoTable = new()
    {
        // ctor @0x8001EA70, jumptable @0x80091570 (2000-4-29, 7-mesh items.psx)
        [8] = [WebCartridge], [9] = [YellowGear], [11] = [QuestionMark],
        [12] = [PanelModel6], [13] = [YellowGear],
        [14] = [GreyGear], [15] = [GreyGear], [16] = [GreyGear]
    };

    private static readonly Dictionary<int, uint[]> FinalTable = new()
    {
        // ctor @0x8001DE00-region, jumptable @0x80093674 (2000-9-1, 9-mesh items.psx)
        [8] = [WebCartridge], [10] = [PanelModel7], [11] = [QuestionMark],
        [12] = [PanelModel6], [13] = [YellowGear],
        [14] = [GreyGear], [15] = [GreyGear], [16] = [GreyGear], [18] = [PanelModel7]
    };

    // THPS1/THPS2 pickupType -> items.psx mesh-name hash, transcribed verbatim
    // from the matched THPS2 decomp pickup constructor (POWERUP.cpp): types
    // 4/5/6/10/15 are the K/S/A/T/E SKATE letters, 21 through 32 the bonus/money
    // pickups. The three medal types (0x664-0x666) are OMITTED — they spool from
    // skmedals, not items. Hash-keyed placement (Resolve) emits only the entries
    // whose hash is actually in the level's items.psx, so THPS1 (letters + 21-23
    // only) and THPS2 (full set) each place their own subset.
    private static readonly Dictionary<int, uint[]> ThpsTable = new()
    {
        [4] = [0x2328A71C], [5] = [ThpsLetterS], [6] = [0x2EBF22CA], [10] = [0x29B68A16],
        [15] = [0x34524351], // Secret tape: THPS2 names it Videotape_01 (0x7C1B2C4A); THPS1 final
        // ships Itm_Tape01 (0xD5108E8C — thps1_final.exe CPowerUp ctor
        // if-chain @0x8001FAF4, case-16 body @0x8001FC68); the 1999-4-9
        // proto reuses the grey-gear placeholder (thps1_proto.exe jump
        // table @0x8006AF50, case 16 @0x8001733C). Candidates are scanned
        // first-present-in-items.psx, so one table serves all builds — the
        // single-hash table silently skipped every THPS1 tape node.
        [16] = [0x7C1B2C4A, 0xD5108E8C, 0x7E74F3D4],
        [18] = [0x7C1B2C4A],
        // Bonus/money: the THPS1 proto collapses all three types onto one
        // mesh (0x91EEBD52, proto jump table cases 21/22/23 @0x80017360).
        [21] = [0x6F0F6B9F, 0x91EEBD52], [22] = [0xF6063A25, 0x91EEBD52],
        [23] = [0x81010AB3, 0x91EEBD52],
        [24] = [0x694ED947], [25] = [0x260F4F80], [26] = [0xCC4E141F],
        [27] = [0x483701E6], [28] = [0x608E06C7], [29] = [0x10122D2A],
        [30] = [0xC12ED64B], [31] = [0x22908B06], [32] = [0x56891A69]
    };

    // Apocalypse pickupType -> items.psx mesh-name hash, read from the matched
    // CPowerUp ctor jump table (apocalypse_final.exe @0x800A11EC, a 20-entry table
    // keyed by mType-1) cross-checked against the TRG POWERUP census (types 4/5/6/10/14/15/16
    // account for 176 of 281 nodes; 14/15/16 are three spin variants of one
    // grey-gear model). pickupType 17 spools the "plus_one" region (not items),
    // and 1/2/3/7/19/20 render no model — all omitted. No per-type scale.
    private static readonly Dictionary<int, uint[]> ApocalypseTable = new()
    {
        [4] = [ApocalypseItemsMarker], [5] = [0xD40B155E], [6] = [0x51E46AAF],
        [10] = [0x4D2C5D7C], [14] = [0x7E74F3D4], [15] = [0x7E74F3D4], [16] = [0x7E74F3D4]
    };

    internal static readonly IReadOnlySet<uint> EmptyHashSet = new HashSet<uint>();

    /// <summary>
    ///     Resolves POWERUP placements keyed by items.psx object index (the same
    ///     key space as <see cref="PsxItemsBankSubstitution" />, so the two
    ///     merge into one items geometry pass). Returns null when there is no TRG,
    ///     the sibling items.psx matches no known pickup table (Apocalypse /
    ///     unknown), or no mapped pickups are placed.
    /// </summary>
    internal static Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>? Resolve(
        TrgFile? trg,
        PsxMeshFile itemsFile,
        float translationDivisor,
        PsxTerrainHeightField? terrain = null)
    {
        if (trg == null || itemsFile.Objects.Count == 0)
            return null;

        var objectByHash = BuildObjectIndexByHash(itemsFile);
        if (objectByHash.Count == 0)
            return null;
        if (SelectTable(objectByHash) is not { } table)
            return null;
        var hoverWorldUnits = HoverWorldUnitsForTable(table);

        Dictionary<int, List<PsxLevelObjectPlacement>>? placements = null;
        foreach (var node in trg.Nodes)
        {
            if (node.TypeId != TrgNodeMetadata.TypePowerup
                || node.PickupType is not { } pickupType
                || node.Position is not { } position
                || !table.TryGetValue(pickupType, out var candidates)
                || !TryResolveFirstPresent(candidates, objectByHash, out var objectIndex))
            {
                continue;
            }

            placements ??= [];
            if (!placements.TryGetValue(objectIndex, out var list))
            {
                list = [];
                placements[objectIndex] = list;
            }

            // A grounded POWERUP (GroundedCheck 0, or absent) reseats to
            // groundY - hover, matching the engine's CPowerUp ground-snap; an
            // explicitly non-grounded pickup keeps its authored spawn height.
            float? hover = terrain != null && node.GroundedCheck is null or 0
                ? hoverWorldUnits
                : null;
            list.Add(new PsxLevelObjectPlacement(
                node.Index,
                PsxLevelObjectPlacementResolver.CreateNodeTranslation(
                    position, translationDivisor, terrain, hover)));
        }

        return placements?.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<PsxLevelObjectPlacement>)pair.Value);
    }

    /// <summary>
    ///     The items.psx mesh hashes the POWERUP layer placed for a level, given
    ///     its <see cref="Resolve" /> result — the pickups the bank layer must
    ///     drop as duplicates (the POWERUP layer is authoritative for them).
    /// </summary>
    internal static IReadOnlySet<uint> PlacedModelHashes(
        PsxMeshFile itemsFile,
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>? powerupPlacements)
    {
        if (powerupPlacements == null || powerupPlacements.Count == 0)
            return EmptyHashSet;

        var hashes = new HashSet<uint>();
        foreach (var objectIndex in powerupPlacements.Keys)
        {
            if (objectIndex < itemsFile.Objects.Count)
            {
                var meshIndex = itemsFile.Objects[objectIndex].MeshIndex;
                if (meshIndex < itemsFile.MeshNameHashes.Length)
                    hashes.Add(itemsFile.MeshNameHashes[meshIndex]);
            }
        }

        return hashes;
    }

    /// <summary>
    ///     Picks the pickup table matching the sibling items.psx by its mesh set,
    ///     or null when none applies (Apocalypse / unknown). Spider-Man's final
    ///     items.psx uniquely carries model 7 (<see cref="PanelModel7" />) and
    ///     April-29 adds model 6 (<see cref="PanelModel6" />) over the Feb proto's
    ///     six meshes; the Feb proto is recognized by its "?" marker or web
    ///     cartridge. THPS items.psx is recognized by the 'S' SKATE-letter model
    ///     (<see cref="ThpsLetterS" />) and Apocalypse by its pickupType-4 model
    ///     (<see cref="ApocalypseItemsMarker" />). The signatures are mutually
    ///     exclusive across games, so order only matters within the Spider-Man
    ///     family. Unknown items resolve to no table.
    /// </summary>
    /// <summary>
    ///     Each pickupType maps to CANDIDATE mesh hashes in priority order; the
    ///     first one present in the level's items.psx wins. The engines name the
    ///     same pickup differently per build (THPS2 Videotape_01 vs THPS1
    ///     Itm_Tape01), and the THPS1 proto collapses several types onto one
    ///     placeholder — a single-hash table cannot express either.
    /// </summary>
    private static bool TryResolveFirstPresent(
        uint[] candidates,
        Dictionary<uint, int> objectByHash,
        out int objectIndex)
    {
        foreach (var hash in candidates)
        {
            if (objectByHash.TryGetValue(hash, out objectIndex))
                return true;
        }

        objectIndex = -1;
        return false;
    }

    private static Dictionary<int, uint[]>? SelectTable(Dictionary<uint, int> objectByHash)
    {
        if (objectByHash.ContainsKey(PanelModel7))
            return FinalTable;
        if (objectByHash.ContainsKey(PanelModel6))
            return AprProtoTable;
        if (objectByHash.ContainsKey(QuestionMark) || objectByHash.ContainsKey(WebCartridge))
            return FebProtoTable;
        if (objectByHash.ContainsKey(ThpsLetterS))
            return ThpsTable;
        if (objectByHash.ContainsKey(ApocalypseItemsMarker))
            return ApocalypseTable;
        return null;
    }

    private static Dictionary<uint, int> BuildObjectIndexByHash(PsxMeshFile itemsFile)
    {
        var objectByHash = new Dictionary<uint, int>();
        for (var objectIndex = 0; objectIndex < itemsFile.Objects.Count; objectIndex++)
        {
            var meshIndex = itemsFile.Objects[objectIndex].MeshIndex;
            if (meshIndex < itemsFile.MeshNameHashes.Length)
                objectByHash.TryAdd(itemsFile.MeshNameHashes[meshIndex], objectIndex);
        }

        return objectByHash;
    }

    /// <summary>
    ///     The <c>CPowerUp::mHoverHeight</c> (world units) the origin rests above
    ///     the floor, per game family, or <c>null</c> to leave the pickup at its
    ///     authored Y (no snap). Spider-Man (all builds) = 128 (decompiled from
    ///     the final ctor @0x8001DCC4). THPS does NOT snap: the matched THPS2
    ///     decomp's <c>CPowerUp::DoPhysics</c> only writes
    ///     <c>mOrgPos.vy = mGroundY - hover</c> on the <c>mDropping</c> path
    ///     (ctor <c>Flags &amp; 4</c>); placed pickups render at authored Y plus a
    ///     wobble centered on it (and its items models are origin-CENTERED, so an
    ///     origin-on-floor snap half-buried every SKATE letter). Apocalypse is a
    ///     different (1998) engine whose pickup ground-snap/hover is NOT
    ///     reverse-engineered, so its pickups are left where authored rather than
    ///     snapped with a guessed value.
    /// </summary>
    private static float? HoverWorldUnitsForTable(Dictionary<int, uint[]> table)
    {
        if (ReferenceEquals(table, ApocalypseTable) || ReferenceEquals(table, ThpsTable))
            return null;

        return PsxGroundSnap.SpiderManHoverWorldUnits;
    }
}
