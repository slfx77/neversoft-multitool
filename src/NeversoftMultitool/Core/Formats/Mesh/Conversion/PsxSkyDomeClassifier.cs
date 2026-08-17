using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Identifies a level's sky/background layers. Engine truth (decomp-verified
///     across Apocalypse/THPS1/THPS2/Spider-Man/SM2:EE, 2026-07-28): the sky is
///     NOT world-placed geometry — TRG command 0xAB (<c>BackgroundCreate</c>)
///     registers a bank model by checksum on <c>BackgroundList</c>, and
///     <c>M3d_RenderBackground</c> draws it each frame with the camera's
///     ROTATION ONLY (GTE translation registers explicitly zeroed,
///     M3dAsm_TransformAndOutcodeBackgroundVertices @0x80098F8C) and SZ forced
///     to the far ordering-table bucket. The mesh's bank object-table position
///     is dead data the engine never reads (sksf parks its dome 6,350 units
///     below the level). Classification therefore joins the TRG's
///     BackgroundCreate checksums against the bank's mesh name hashes
///     (298/302 corpus commands join),
///     with the older geometric heuristic kept only as a fallback for
///     TRG-less oddballs. TRG command 0xCA (<c>SetSkyColor</c>) supplies the
///     flat backdrop colour drawn behind the sky model.
/// </summary>
internal static class PsxSkyDomeClassifier
{
    private const int BackgroundCreateOpcode = 0xAB;
    private const int SetSkyColorOpcode = 0xCA;
    private const float SkyXzExtentFactor = 0.7f;
    private const float SkyMinHeightToXzRatio = 0.15f;

    /// <summary>
    ///     Item flag 0x2000 = "distant backdrop": M3d_Render forces the far
    ///     plane to 0x7FFF and disables the depth cue while the item draws
    ///     (THPS2 June-2000 proto matching decomp, M3D.cpp ~L1153/L1232) —
    ///     never far-clipped, no fog.
    /// </summary>
    private const uint DistantBackdropItemFlag = 0x2000;

    /// <summary>
    ///     Fixed co-location radius (model units) around the joined sky
    ///     objects' centroid for the flag-0x2000 second pass. A fixed radius,
    ///     NOT a multiple of the joined objects' spread: l1a1's joined pair
    ///     spans only 135 units while the unregistered skyline sits 640 from
    ///     their centroid (and ≥5,550 from every other bank object), so a
    ///     spread-relative guard would claim nothing.
    /// </summary>
    private const float SkyParkingClusterRadius = 1_500f;

    /// <summary>
    ///     <paramref name="LayerOrder" /> maps each sky object index to its paint
    ///     rank: 0 is drawn FIRST (furthest back), higher ranks paint over it.
    ///
    ///     The rank is the position of the object's <c>0xAB BackgroundCreate</c>
    ///     within the registering node's command list, because the engine's two
    ///     reversals cancel exactly (RE 2026-08-04): <c>CBackground</c>'s ctor
    ///     head-inserts onto <c>BackgroundList</c> (<c>CBody::AttachTo</c>,
    ///     OB.cpp:435) so the list runs last-registered-first;
    ///     <c>M3d_RenderBackground</c> walks it head to tail, so submission is
    ///     reverse registration order; every background vertex gets SZ 0x7FFF and
    ///     the OT index clamps to the last bucket, so ALL layers share one
    ///     bucket; and insertion into a bucket is itself a prepend into a
    ///     reversed table drawn from the top, so the earliest-submitted paints
    ///     last. Net effect: <b>layers paint in TRG registration order</b> — the
    ///     first 0xAB is furthest back, the last is in front.
    /// </summary>
    internal sealed record Result(
        IReadOnlySet<int> ObjectIndices,
        TrgPosition? AnchorNodePosition,
        uint? SkyColor,
        IReadOnlyDictionary<int, int> LayerOrder);

    /// <summary>
    ///     Classifies the bank's sky objects, preferring the exact TRG
    ///     BackgroundCreate join; the anchor is the position of the (RESTART)
    ///     node whose command list registered the background — the camera's
    ///     starting point, the best static stand-in for "centered on the
    ///     camera". Null when nothing qualifies.
    /// </summary>
    internal static Result? Classify(PsxMeshFile levelMesh, PsxMeshFile bank, TrgFile? trg)
    {
        var skyColor = FindSkyColor(trg);
        var fromTrg = ClassifyFromTrg(bank, trg);
        if (fromTrg != null)
            return fromTrg with { SkyColor = skyColor };

        var geometric = FindSkyObjectIndicesGeometric(levelMesh, bank);
        // The geometric fallback has no registration record, so it can assert no
        // order: every layer gets rank 0 rather than an invented one.
        return geometric == null
            ? null
            : new Result(geometric, null, skyColor, EmptyLayerOrder);
    }

    private static readonly Dictionary<int, int> EmptyLayerOrder = [];

    private static Result? ClassifyFromTrg(PsxMeshFile bank, TrgFile? trg)
    {
        if (trg == null)
            return null;

        var checksumRanks = CollectBackgroundRegistrations(trg, out var anchor);
        if (checksumRanks.Count == 0)
            return null;

        HashSet<int>? indices = null;
        var layerOrder = new Dictionary<int, int>();
        for (var objectIndex = 0; objectIndex < bank.Objects.Count; objectIndex++)
        {
            var meshIndex = bank.Objects[objectIndex].MeshIndex;
            if (meshIndex < bank.MeshNameHashes.Length
                && checksumRanks.TryGetValue(bank.MeshNameHashes[meshIndex], out var rank))
            {
                indices ??= [];
                indices.Add(objectIndex);
                layerOrder[objectIndex] = rank;
            }
        }

        if (indices == null)
            return null;

        var claimed = ClaimColocatedDistantBackdrops(bank, indices);

        // A claimed backdrop has no 0xAB record, so its rank is not measured. It
        // paints IN FRONT of every registered layer: the engine draws it as
        // ordinary world geometry (far-clip disabled), and the background pass
        // runs before the world pass, so it is necessarily nearer than the
        // registered background. l1a1's claimed object is its skyline, which is
        // exactly what should sit in front of the dome.
        if (claimed.Count > 0)
        {
            var front = layerOrder.Count == 0 ? 0 : layerOrder.Values.Max() + 1;
            foreach (var objectIndex in claimed)
                layerOrder[objectIndex] = front;
        }

        return new Result(indices, anchor, null, layerOrder);
    }

    /// <summary>
    ///     Second pass, run ONLY after a successful TRG join: additionally claim
    ///     bank objects carrying the <see cref="DistantBackdropItemFlag" /> whose
    ///     authored position is parked inside the joined sky objects' cluster
    ///     (within <see cref="SkyParkingClusterRadius" /> of their centroid).
    ///     This DELIBERATELY diverges from the shipped TRG: l1a1_t.trg registers
    ///     only two of the daytime-NY three-layer sky set in every build
    ///     (Feb-18/Apr-29/Jun-12/Sep-1 PSX, DC proto, PC final — the 0xAB record
    ///     for skyline 0x62D17F19 is simply absent from the command stream), so
    ///     the shipped engine renders that layer WORLD-PLACED at its parked
    ///     position with far-clip disabled. The registration is a shipped
    ///     authoring omission, not intent: l1a1_o.psx is byte-identical to
    ///     lda1_o.psx, whose TRG registers all three layers, and the parked
    ///     position sits outside the level's playable footprint (dead data by
    ///     the background convention). Corpus effect (6 PS1-era builds, 2,129
    ///     bank objects surveyed): exactly 2 objects change — l1a1 obj3 in the
    ///     Sep-1 final and the Apr-29 proto. The other 13 flag-0x2000 objects
    ///     corpus-wide are already claimed by their own TRG join.
    /// </summary>
    private static List<int> ClaimColocatedDistantBackdrops(PsxMeshFile bank, HashSet<int> indices)
    {
        var centroid = Vector3.Zero;
        foreach (var index in indices)
            centroid += PsxMeshSemantics.GetObjectOffset(bank, bank.Objects[index]);
        centroid /= indices.Count;

        var claimed = new List<int>();
        for (var objectIndex = 0; objectIndex < bank.Objects.Count; objectIndex++)
        {
            var obj = bank.Objects[objectIndex];
            if ((obj.Flags & DistantBackdropItemFlag) == 0
                || obj.MeshIndex >= bank.Meshes.Count
                || indices.Contains(objectIndex))
            {
                continue;
            }

            var position = PsxMeshSemantics.GetObjectOffset(bank, obj);
            if (Vector3.Distance(position, centroid) > SkyParkingClusterRadius)
                continue;

            indices.Add(objectIndex);
            claimed.Add(objectIndex);
        }

        return claimed;
    }

    /// <summary>
    ///     Every <c>0xAB BackgroundCreate</c> in the file, mapped to its paint
    ///     rank, plus the position of the first node that registers one (the
    ///     camera's start, used as the static sky anchor).
    ///
    ///     A level repeats the same registration sequence in each of its RESTART
    ///     nodes — l2a1 has three — so the rank is the command's position WITHIN
    ///     its own node and the lowest wins. A counter running across nodes would
    ///     give the second node's copies ranks 2 and 3 and invent an order.
    /// </summary>
    private static Dictionary<uint, int> CollectBackgroundRegistrations(
        TrgFile trg,
        out TrgPosition? anchor)
    {
        var checksumRanks = new Dictionary<uint, int>();
        anchor = null;
        foreach (var node in trg.Nodes)
        {
            if (node.Commands == null)
                continue;

            var rank = 0;
            foreach (var command in node.Commands)
            {
                if (command.Opcode != BackgroundCreateOpcode
                    || !TryParseChecksumArg(command, out var checksum))
                {
                    continue;
                }

                if (!checksumRanks.TryGetValue(checksum, out var existing) || rank < existing)
                    checksumRanks[checksum] = rank;
                rank++;
            }

            if (rank > 0)
                anchor ??= node.Position;
        }

        return checksumRanks;
    }

    private static bool TryParseChecksumArg(TrgCommand command, out uint checksum)
    {
        checksum = 0;
        var arg = command.Args is { Count: > 0 } ? command.Args[0] as string : null;
        return arg != null
               && arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
               && uint.TryParse(arg[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out checksum);
    }

    /// <summary>
    ///     The TRG's SetSkyColor backdrop, independent of dome classification —
    ///     the engine applies it as the framebuffer clear colour whether or not
    ///     any 0xAB background joins a bank mesh.
    /// </summary>
    internal static uint? FindSkyColor(TrgFile? trg)
    {
        if (trg == null)
            return null;

        foreach (var node in trg.Nodes)
        {
            foreach (var command in node.Commands ?? [])
            {
                // Db_UpdateSky reads R from the first arg and G/B from the
                // high/low bytes of the second; two 0xFFFF args mean the
                // level disables the flat backdrop entirely.
                if (command.Opcode != SetSkyColorOpcode
                    || command.Args is not { Count: >= 2 }
                    || command.Args[0] is not ushort r
                    || command.Args[1] is not ushort gb)
                {
                    continue;
                }

                if (r == 0xFFFF && gb == 0xFFFF)
                    return null;
                return ((uint)(r & 0xFF) << 16) | (uint)(gb >> 8 << 8) | (uint)(gb & 0xFF);
            }
        }

        return null;
    }

    /// <summary>
    ///     The level's placed-geometry centroid in glTF space — the anchor
    ///     fallback when no TRG background node position exists.
    /// </summary>
    internal static Vector3 LevelCentroidGltf(PsxMeshFile levelMesh)
    {
        return TryGetPlacedBounds(levelMesh, out var min, out var max)
            ? PsxMeshSemantics.ToGltfPosition((min + max) * 0.5f)
            : Vector3.Zero;
    }

    /// <summary>
    ///     Geometric fallback (native space, +Y down): a bank object spanning
    ///     most of the level on BOTH horizontal axes (SKMAR 0.83/0.96 of the
    ///     level's X/Z, SKBUL 1.38 — every other bank prop is under 0.1),
    ///     enclosing the level centroid, with real dome height.
    /// </summary>
    private static HashSet<int>? FindSkyObjectIndicesGeometric(PsxMeshFile levelMesh, PsxMeshFile bank)
    {
        if (!TryGetPlacedBounds(levelMesh, out var levelMin, out var levelMax))
            return null;

        var levelExtent = levelMax - levelMin;
        var levelCenter = (levelMin + levelMax) * 0.5f;

        HashSet<int>? sky = null;
        for (var objectIndex = 0; objectIndex < bank.Objects.Count; objectIndex++)
        {
            var obj = bank.Objects[objectIndex];
            if (obj.MeshIndex >= bank.Meshes.Count)
                continue;
            if (!TryGetMeshBounds(bank.Meshes[obj.MeshIndex], out var min, out var max))
                continue;

            var offset = PsxMeshSemantics.GetObjectOffset(bank, obj);
            min += offset;
            max += offset;

            var extent = max - min;
            if (extent.X < levelExtent.X * SkyXzExtentFactor
                || extent.Z < levelExtent.Z * SkyXzExtentFactor)
            {
                continue;
            }

            if (levelCenter.X < min.X || levelCenter.X > max.X
                || levelCenter.Z < min.Z || levelCenter.Z > max.Z)
            {
                continue;
            }

            // A dome curves upward; an oversized flat sheet (water/ocean
            // plane) has near-zero Y extent and must keep normal depth.
            if (extent.Y < MathF.Max(extent.X, extent.Z) * SkyMinHeightToXzRatio)
                continue;

            sky ??= [];
            sky.Add(objectIndex);
        }

        return sky;
    }

    private static bool TryGetPlacedBounds(PsxMeshFile file, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        var any = false;
        foreach (var obj in file.Objects)
        {
            if (obj.MeshIndex >= file.Meshes.Count)
                continue;
            if (!TryGetMeshBounds(file.Meshes[obj.MeshIndex], out var meshMin, out var meshMax))
                continue;

            var offset = PsxMeshSemantics.GetObjectOffset(file, obj);
            min = Vector3.Min(min, meshMin + offset);
            max = Vector3.Max(max, meshMax + offset);
            any = true;
        }

        return any;
    }

    private static bool TryGetMeshBounds(PsxMesh mesh, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        if (mesh.Vertices.Count == 0)
            return false;

        foreach (var vertex in mesh.Vertices)
        {
            var position = new Vector3(vertex.X, vertex.Y, vertex.Z);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return true;
    }
}
