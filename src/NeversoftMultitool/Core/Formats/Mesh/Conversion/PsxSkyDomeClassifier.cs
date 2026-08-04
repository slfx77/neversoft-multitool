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
///     (298/302 corpus commands join; tools/diagnostics/psx_sky_background_survey.py),
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

    internal sealed record Result(
        IReadOnlySet<int> ObjectIndices,
        TrgPosition? AnchorNodePosition,
        uint? SkyColor);

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
        return geometric == null ? null : new Result(geometric, null, skyColor);
    }

    private static Result? ClassifyFromTrg(PsxMeshFile bank, TrgFile? trg)
    {
        if (trg == null)
            return null;

        var checksums = new HashSet<uint>();
        TrgPosition? anchor = null;
        foreach (var node in trg.Nodes)
        {
            if (node.Commands == null)
                continue;

            var registersBackground = false;
            foreach (var command in node.Commands)
            {
                if (command.Opcode != BackgroundCreateOpcode)
                    continue;
                if (TryParseChecksumArg(command, out var checksum))
                {
                    checksums.Add(checksum);
                    registersBackground = true;
                }
            }

            if (registersBackground)
                anchor ??= node.Position;
        }

        if (checksums.Count == 0)
            return null;

        HashSet<int>? indices = null;
        for (var objectIndex = 0; objectIndex < bank.Objects.Count; objectIndex++)
        {
            var meshIndex = bank.Objects[objectIndex].MeshIndex;
            if (meshIndex < bank.MeshNameHashes.Length
                && checksums.Contains(bank.MeshNameHashes[meshIndex]))
            {
                indices ??= [];
                indices.Add(objectIndex);
            }
        }

        if (indices == null)
            return null;

        ClaimColocatedDistantBackdrops(bank, indices);
        return new Result(indices, anchor, null);
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
    private static void ClaimColocatedDistantBackdrops(PsxMeshFile bank, HashSet<int> indices)
    {
        var centroid = Vector3.Zero;
        foreach (var index in indices)
            centroid += PsxMeshSemantics.GetObjectOffset(bank, bank.Objects[index]);
        centroid /= indices.Count;

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
            if (Vector3.Distance(position, centroid) <= SkyParkingClusterRadius)
                indices.Add(objectIndex);
        }
    }

    private static bool TryParseChecksumArg(TrgCommand command, out uint checksum)
    {
        checksum = 0;
        var arg = command.Args is { Count: > 0 } ? command.Args[0] as string : null;
        return arg != null
               && arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
               && uint.TryParse(arg[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out checksum);
    }

    private static uint? FindSkyColor(TrgFile? trg)
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
