using System.Globalization;
using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Throwaway measurement harness for the 2026-08-17 triage campaign
///     (docs/backlog/app-feedback-2026-08-17.md items B7 and C3). These are
///     MEASUREMENTS, not regressions: they write reports under
///     TestOutput/triage/harness and assert only that the measurement ran.
///     Delete or Skip-gate once the campaign's follow-ups are closed.
/// </summary>
public sealed class ThawTriageProbeTests(TestPaths paths, ITestOutputHelper output)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    private static string HarnessDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "TestOutput", "triage-harness");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    ///     B7 (sunken chairs, z_ho): per-instance embed depth of every
    ///     QB-placed prop against the level floor at the prop's own footprint,
    ///     computed the way the PSX chair diagnostic did it — decoded base-Y
    ///     versus a downward floor query over the render mesh.
    /// </summary>
    [CorpusFact]
    public void B7_ZHo_QbPlacedProps_EmbedDepthAgainstTheLevelFloor()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_ho.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_ho.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var resources = Ps2WorldzoneQbObjectResolver.Resolve(
            pakBytes, PakArchive.GetTypedEntries(pakBytes));
        var ownerByMdl = resources.ToDictionary(
            static pair => $"{pair.Key:X8}",
            static pair => pair.Value.OwnerName,
            StringComparer.OrdinalIgnoreCase);

        var document = new ModelDocument { Name = "z_ho_embed_probe" };
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document, pakBytes, "z_ho.pak.ps2",
            null, null, null, null, null,
            WorldzoneTimeOfDay.All, 1f);

        // World-space floor candidates: every world-pass level triangle.
        var floorTriangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var node in document.Nodes)
        {
            if (node.MeshIndex is not { } meshIndex)
                continue;
            var mesh = document.Meshes[meshIndex];
            if (!mesh.Name.Contains("_world_leaf_", StringComparison.Ordinal))
                continue;
            foreach (var primitive in mesh.Primitives)
            {
                for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
                {
                    floorTriangles.Add((
                        Vector3.Transform(primitive.Vertices[primitive.Indices[i]].Position, node.Transform),
                        Vector3.Transform(primitive.Vertices[primitive.Indices[i + 1]].Position, node.Transform),
                        Vector3.Transform(primitive.Vertices[primitive.Indices[i + 2]].Position, node.Transform)));
                }
            }
        }

        // One record per QB placement: node names are {mdl}_qb_leaf_{NNNNN}
        // (single placement) or ..._pNNNN (multi). Aggregate every leaf of one
        // placement into one prop footprint.
        var props = new Dictionary<string, (List<Vector3> Points, string Mdl)>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (node.MeshIndex is not { } meshIndex)
                continue;
            var mesh = document.Meshes[meshIndex];
            var marker = mesh.Name.IndexOf("_qb_leaf_", StringComparison.Ordinal);
            if (marker < 0)
                continue;

            var mdl = mesh.Name[..marker];
            var placement = node.Name.Length > mesh.Name.Length && node.Name.StartsWith(mesh.Name, StringComparison.Ordinal)
                ? node.Name[mesh.Name.Length..]
                : "";
            var key = $"{mdl}{placement}";
            if (!props.TryGetValue(key, out var bucket))
            {
                bucket = ([], mdl);
                props[key] = bucket;
            }

            foreach (var primitive in mesh.Primitives)
            {
                foreach (var vertex in primitive.Vertices)
                    bucket.Points.Add(Vector3.Transform(vertex.Position, node.Transform));
            }
        }

        Assert.NotEmpty(props);
        Assert.NotEmpty(floorTriangles);

        var sb = new StringBuilder();
        sb.AppendLine("owner,mdl,placement,baseY,floorY,embedDepth,floorFound,footprintX,footprintZ");
        var rows = new List<string>();
        foreach (var (key, (points, mdl)) in props.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var baseY = points.Min(static p => p.Y);
            // Sample the floor at the XZ centroid of the prop's lowest band
            // (within 5 units of the base) — a chair's legs, not its back.
            var basePoints = points.Where(p => p.Y <= baseY + 5f).ToList();
            var sample = new Vector2(
                basePoints.Average(static p => p.X),
                basePoints.Average(static p => p.Z));

            float? floorY = null;
            foreach (var (a, b, c) in floorTriangles)
            {
                if (!TryGetTrianglePlaneY(a, b, c, sample, out var y))
                    continue;
                // Nearest surface to the base within a generous band — the
                // floor a sunken chair pokes through sits ABOVE its base.
                if (Math.Abs(y - baseY) > 200f)
                    continue;
                if (floorY == null || Math.Abs(y - baseY) < Math.Abs(floorY.Value - baseY))
                    floorY = y;
            }

            var owner = ownerByMdl.TryGetValue(mdl, out var name) ? name : "?";
            var embed = floorY is { } f ? f - baseY : 0f;
            var line = string.Join(',',
                owner, mdl, key.Length > mdl.Length ? key[mdl.Length..] : "p0000",
                F(baseY), floorY is { } fy ? F(fy) : "",
                F(embed), floorY != null ? "1" : "0",
                F(sample.X), F(sample.Y));
            sb.AppendLine(line);
            rows.Add(line);
        }

        var reportPath = Path.Combine(HarnessDir, "b7_z_ho_embed.csv");
        File.WriteAllText(reportPath, sb.ToString());
        output.WriteLine($"Report: {reportPath}");
        foreach (var row in rows)
            output.WriteLine(row);
    }

    /// <summary>
    ///     C3 (front-down cutscene MDL): decline census + preamble/basis facts
    ///     for 00034A60.mdl inside sm_levelevent_main.pak.ps2 — how many
    ///     leaves parse vs reject (the "incomplete" half), and whether the MDL
    ///     carries a bone preamble whose root placement (the worldzone path's
    ///     Y-up conversion) would supply the missing basis (the "front down"
    ///     half).
    /// </summary>
    [CorpusFact]
    public void C3_CutsceneMdl_DeclineCensusAndBasisFacts()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "sm_levelevent_main.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 sm_levelevent_main.pak.ps2 sample not available");

        var backend = ArchiveAssetBackend.TryOpen(pakPath!);
        Assert.SkipWhen(backend == null, "sm_levelevent_main.pak.ps2 did not open as an archive");
        var entry = backend!.Entries.FirstOrDefault(static e =>
            e.Name.Equals("00034A60.mdl", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry == null, "entry 00034A60.mdl not found in the pak");

        var mdlBytes = backend.ReadEntryBytes(entry!);
        var rejections = new List<Ps2GeomLeafRejection>();
        var scene = Ps2GeomFile.ParsePakMdl(mdlBytes, "00034A60", rejections.Add);

        var preamble = scene.MdlPreamble;
        output.WriteLine($"bytes={mdlBytes.Length}");
        output.WriteLine($"leaves={scene.Leaves.Count} rejections={rejections.Count}");
        foreach (var group in rejections.GroupBy(static r => $"{r.Stage}/{r.Reason}"))
            output.WriteLine($"  rejection {group.Key}: {group.Count()}");
        output.WriteLine(preamble == null
            ? "preamble=none"
            : $"preamble: bones={preamble.Bones.Count} records={preamble.Records.Count} " +
              $"isLevelMdl={Ps2LevelMdlParser.IsLevelMdl(preamble)}");

        if (preamble is { Bones.Count: > 0 })
        {
            var placements = Ps2MdlPlacementResolver.ResolveWorldzonePlacements(preamble);
            output.WriteLine($"placements={placements.Count}");
            if (placements.Count > 0)
            {
                var root = placements[0];
                output.WriteLine(
                    $"rootPlacement pos=({F(root.Position.X)},{F(root.Position.Y)},{F(root.Position.Z)}) " +
                    $"rot=({F(root.Rotation.X)},{F(root.Rotation.Y)},{F(root.Rotation.Z)},{F(root.Rotation.W)})");
            }
        }

        // Aggregate authored bbox — a standing scene piece exported without
        // the basis conversion reads tall in the wrong axis.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var vertexTotal = 0;
        foreach (var leaf in scene.Leaves)
        {
            foreach (var vertex in leaf.Vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }

            vertexTotal += leaf.Vertices.Length;
        }

        var extent = max - min;
        output.WriteLine(
            $"vertices={vertexTotal} extentX={F(extent.X)} extentY={F(extent.Y)} extentZ={F(extent.Z)}");

        var reportPath = Path.Combine(HarnessDir, "c3_00034A60_census.txt");
        File.WriteAllText(reportPath,
            $"leaves={scene.Leaves.Count} rejections={rejections.Count} " +
            $"bones={preamble?.Bones.Count ?? -1} records={preamble?.Records.Count ?? -1} " +
            $"extent=({F(extent.X)},{F(extent.Y)},{F(extent.Z)})\n");
        Assert.True(scene.Leaves.Count > 0 || rejections.Count > 0);
    }

    private static bool TryGetTrianglePlaneY(
        Vector3 a, Vector3 b, Vector3 c, Vector2 point, out float y)
    {
        y = 0f;
        // 2D barycentric containment in the XZ plane.
        var v0 = new Vector2(c.X - a.X, c.Z - a.Z);
        var v1 = new Vector2(b.X - a.X, b.Z - a.Z);
        var v2 = new Vector2(point.X - a.X, point.Y - a.Z);

        var dot00 = Vector2.Dot(v0, v0);
        var dot01 = Vector2.Dot(v0, v1);
        var dot02 = Vector2.Dot(v0, v2);
        var dot11 = Vector2.Dot(v1, v1);
        var dot12 = Vector2.Dot(v1, v2);

        var denom = dot00 * dot11 - dot01 * dot01;
        if (Math.Abs(denom) < 1e-6f)
            return false;

        var inv = 1f / denom;
        var u = (dot11 * dot02 - dot01 * dot12) * inv;
        var v = (dot00 * dot12 - dot01 * dot02) * inv;
        if (u < -1e-4f || v < -1e-4f || u + v > 1f + 1e-4f)
            return false;

        y = a.Y + u * (c.Y - a.Y) + v * (b.Y - a.Y);
        return true;
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
