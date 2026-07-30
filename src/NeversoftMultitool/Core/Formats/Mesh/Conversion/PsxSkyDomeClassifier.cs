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

        return indices == null ? null : new Result(indices, anchor, null);
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
