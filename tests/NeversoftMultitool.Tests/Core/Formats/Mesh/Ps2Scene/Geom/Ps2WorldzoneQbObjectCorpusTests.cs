using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using QbKeyHash = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2WorldzoneQbObjectCorpusTests(TestPaths paths, ITestOutputHelper output)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [CorpusFact]
    public void Resolve_ZBh_PinsFiveResourcesThirtySixAuthoredTransforms()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_bh.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var resources = Ps2WorldzoneQbObjectResolver.Resolve(
            pakBytes,
            PakArchive.GetTypedEntries(pakBytes));
        var byOwner = resources.Values.ToDictionary(
            static resource => resource.OwnerName,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(5, resources.Count);
        Assert.Equal(17, byOwner["ac_unit01"].Instances.Count);
        Assert.Equal(2, byOwner["table_iron_01"].Instances.Count);
        Assert.Equal(4, byOwner["chair_iron_01"].Instances.Count);
        Assert.Equal(8, byOwner["plant_bh_01"].Instances.Count);
        Assert.Equal(5, byOwner["plant_bh_02"].Instances.Count);
        Assert.Equal(36, resources.Values.Sum(static resource => resource.Instances.Count));

        AssertInstance(byOwner["ac_unit01"], "Z_BH_Bouncy_AC_01",
            new Vector3(-12137.661f, 538.9507f, 4473.5576f), Vector3.Zero);
        AssertInstance(byOwner["ac_unit01"], "Z_BH_Bouncy_AC_03",
            new Vector3(-11997.485f, 477.0167f, 3675.0952f), new Vector3(-0.027466f, 0f, 0f));
        AssertInstance(byOwner["chair_iron_01"], "Z_BH_Bouncy_Chair_01",
            new Vector3(-13551.755f, -19.952545f, 4161.0874f), new Vector3(0f, -1.638663f, 0f));
        AssertInstance(byOwner["chair_iron_01"], "Z_BH_Bouncy_Chair_03",
            new Vector3(-13623.282f, -19.952545f, 4151.6284f), new Vector3(0f, 1.501717f, 0f));
    }

    [CorpusFact]
    public void PopulatePs2Worldzone_ZBh_EmitsAuthoredCompactInstancesWithoutOriginFallback()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_bh.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var document = new ModelDocument { Name = "z_bh_qb_objects" };
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document,
            pakBytes,
            "z_bh.pak.ps2",
            null,
            null,
            null,
            null,
            null,
            WorldzoneTimeOfDay.All,
            1f);

        var expectedTriangles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["0003D1D0"] = 204,
            ["00040420"] = 40,
            ["00042FB0"] = 36,
            ["00045120"] = 416,
            ["00047960"] = 260
        };
        var compactPrimitives = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Select(static primitive => (Primitive: primitive, Metadata: primitive.NativeMetadata
                .OfType<Ps2WorldzoneLeafRenderMetadata>().Single()))
            .Where(item => expectedTriangles.ContainsKey(item.Metadata.MdlName))
            .ToList();

        Assert.NotEmpty(compactPrimitives);
        Assert.All(compactPrimitives, static item => Assert.Equal("qb", item.Metadata.Space));
        foreach (var (mdlName, triangleCount) in expectedTriangles)
        {
            Assert.Equal(triangleCount, compactPrimitives
                .Where(item => item.Metadata.MdlName.Equals(mdlName, StringComparison.OrdinalIgnoreCase))
                .Sum(static item => item.Primitive.TriangleCount));
        }

        Assert.Equal(956, compactPrimitives.Sum(static item => item.Primitive.TriangleCount));
    }

    [CorpusFact]
    public void PopulatePs2Worldzone_ZSr_ResolvedEmptyObjectsDoNotFallBackToOrigin()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_sr.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_sr.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var resources = Ps2WorldzoneQbObjectResolver.Resolve(
            pakBytes,
            PakArchive.GetTypedEntries(pakBytes));
        var byOwner = resources.Values.ToDictionary(
            static resource => resource.OwnerName,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(3, byOwner.Count);
        Assert.Equal(0x000A8CF0, byOwner["grbg_pizza01"].MdlEntry.Offset);
        Assert.Equal(0x000AB8B0, byOwner["ac_unit01"].MdlEntry.Offset);
        Assert.Equal(0x000AD820, byOwner["metal_barrel01"].MdlEntry.Offset);
        Assert.All(resources.Values, static resource => Assert.Empty(resource.Instances));
        var expectedResourceTriangles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["grbg_pizza01"] = 12,
            ["ac_unit01"] = 12,
            ["metal_barrel01"] = 18
        };

        // Prove suppression is causal: all three resources are valid zero-bone
        // compact MDLs with parsed geometry, rather than unresolved/empty payloads.
        foreach (var resource in resources.Values)
        {
            var entry = resource.MdlEntry;
            var mdlData = new byte[checked((int)entry.Size)];
            Array.Copy(pakBytes, entry.Offset, mdlData, 0, mdlData.Length);
            mdlData = Ps2WorldzoneMdlPreamble.ExtendLevelMdlPreambleIfNeeded(
                pakBytes, entry, mdlData);
            Assert.True(Ps2GeomFile.IsPakMdl(mdlData));
            var scene = Ps2GeomFile.ParsePakMdl(mdlData, resource.OwnerName);
            Assert.NotNull(scene.MdlPreamble);
            Assert.Empty(scene.MdlPreamble.Bones);
            Assert.Equal(
                expectedResourceTriangles[resource.OwnerName],
                scene.Leaves.Sum(CountRenderableStripTriangles));

            var legacyDocument = new ModelDocument { Name = resource.OwnerName + "_legacy_origin" };
            var materialCache =
                new Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int>();
            Ps2WorldzoneGeometryWriter.PopulatePs2WorldzoneLeaves(
                legacyDocument,
                scene,
                resource.OwnerName,
                [(Vector3.Zero, Quaternion.Identity)],
                static _ => null,
                materialCache,
                null,
                null,
                null,
                1f,
                "world");
            Assert.True(
                legacyDocument.Meshes.SelectMany(static mesh => mesh.Primitives)
                    .Sum(static primitive => primitive.TriangleCount) > 0,
                $"{resource.OwnerName} must have non-empty parsed compact geometry");
        }

        var document = new ModelDocument { Name = "z_sr_qb_objects" };
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document,
            pakBytes,
            "z_sr.pak.ps2",
            null,
            null,
            null,
            null,
            null,
            WorldzoneTimeOfDay.All,
            1f);

        var mappedMdlNames = resources.Keys
            .Select(static offset => offset.ToString("X8", CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(document.Meshes
                .SelectMany(static mesh => mesh.Primitives)
                .SelectMany(static primitive => primitive.NativeMetadata)
                .OfType<Ps2WorldzoneLeafRenderMetadata>(),
            metadata => mappedMdlNames.Contains(metadata.MdlName));
    }

    [CorpusFact]
    public void Resolve_AllThawPs2Worldzones_PinsProvenCompactObjectCensus()
    {
        var pakPaths = paths.FindSampleFiles(ThawPs2Build, "*.pak.ps2")
            .Where(static path => path.Replace('\\', '/').Contains(
                "/worlds/worldzones/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(pakPaths.Count == 0, "THAW PS2 worldzone PAK corpus not available");
        Assert.Equal(128, pakPaths.Count);

        var total = Stopwatch.StartNew();
        var resolverTime = TimeSpan.Zero;
        var writerTime = TimeSpan.Zero;
        var triadCount = 0;
        var resourceCount = 0;
        var instanceCount = 0;
        var authoredPlacedTriangleCount = 0;
        var placedTriangleCount = 0;
        // Triangle recall against THAW GC's zone-specific .geom.mdl.ngc
        // counterparts. The installed PC payloads are 48-byte wrapped and not
        // independently readable by the current PC detector.
        var referenceTrianglesByOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ac_unit01"] = 12,
            ["table_iron_01"] = 20,
            ["chair_iron_01"] = 9,
            ["plant_bh_01"] = 52,
            ["plant_bh_02"] = 52,
            ["metal_barrel01"] = 18,
            ["cardboard_box01"] = 12,
            ["barricade_01"] = 28,
            ["grbg_40oz01"] = 20,
            ["chair_plastic01"] = 36,
            ["table_little01"] = 20,
            ["umbrella_g_01"] = 26,
            ["tire01"] = 28,
            ["plant_big_01"] = 52
        };

        foreach (var pakPath in pakPaths)
        {
            var pakBytes = File.ReadAllBytes(pakPath);
            var typedEntries = PakArchive.GetTypedEntries(pakBytes);
            triadCount += Ps2WorldzoneQbObjectResolver.FindObjectResourceTriads(typedEntries).Count;

            var stopwatch = Stopwatch.StartNew();
            var resources = Ps2WorldzoneQbObjectResolver.Resolve(
                pakBytes,
                typedEntries,
                Path.GetFileName(pakPath).Contains("_net.", StringComparison.OrdinalIgnoreCase));
            stopwatch.Stop();
            resolverTime += stopwatch.Elapsed;
            resourceCount += resources.Count;
            instanceCount += resources.Values.Sum(static resource => resource.Instances.Count);

            foreach (var resource in resources.Values)
            {
                if (resource.Instances.Count == 0)
                    continue;

                var mdlEntry = resource.MdlEntry;
                var mdlData = new byte[checked((int)mdlEntry.Size)];
                Array.Copy(pakBytes, mdlEntry.Offset, mdlData, 0, mdlData.Length);
                mdlData = Ps2WorldzoneMdlPreamble.ExtendLevelMdlPreambleIfNeeded(
                    pakBytes, mdlEntry, mdlData);
                var scene = Ps2GeomFile.ParsePakMdl(
                    mdlData,
                    mdlEntry.Offset.ToString("X8", CultureInfo.InvariantCulture));
                Assert.NotNull(scene.MdlPreamble);
                Assert.Empty(scene.MdlPreamble.Bones);
                var resourceTriangles = scene.Leaves.Sum(CountRenderableStripTriangles);
                Assert.Equal(referenceTrianglesByOwner[resource.OwnerName], resourceTriangles);
                var resourceAuthoredTriangles = resourceTriangles * resource.Instances.Count;
                authoredPlacedTriangleCount += resourceAuthoredTriangles;

                var document = new ModelDocument { Name = resource.OwnerName };
                var placements = resource.Instances
                    .Select(static instance => (instance.Position, instance.Rotation))
                    .ToList();
                var materialCache =
                    new Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int>();
                stopwatch.Restart();
                Ps2WorldzoneGeometryWriter.PopulatePs2WorldzoneLeaves(
                    document,
                    scene,
                    mdlEntry.Offset.ToString("X8", CultureInfo.InvariantCulture),
                    placements,
                    static _ => null,
                    materialCache,
                    null,
                    null,
                    null,
                    1f,
                    "qb");
                stopwatch.Stop();
                writerTime += stopwatch.Elapsed;
                var resourcePlacedTriangles = document.Meshes
                    .SelectMany(static mesh => mesh.Primitives)
                    .Sum(static primitive => primitive.TriangleCount);
                placedTriangleCount += resourcePlacedTriangles;
                output.WriteLine(
                    $"{Path.GetFileName(pakPath)} {resource.OwnerName}: " +
                    $"instances={resource.Instances.Count}, resourceTriangles={resourceTriangles}, " +
                    $"authoredTriangles={resourceAuthoredTriangles}, " +
                    $"emittedTriangles={resourcePlacedTriangles}");
            }
        }

        total.Stop();
        output.WriteLine(
            $"128-PAK QB object census: resolver={resolverTime.TotalMilliseconds:F1} ms, " +
            $"compact writer={writerTime.TotalMilliseconds:F1} ms, wall={total.Elapsed.TotalSeconds:F2} s");
        Assert.Equal(24, triadCount);
        Assert.Equal(24, resourceCount);
        Assert.Equal(161, instanceCount);
        Assert.Equal(3318, authoredPlacedTriangleCount);
        Assert.Equal(3318, placedTriangleCount);
    }

    /// <summary>
    ///     QB props seat on the level floor. The prop models are vertically
    ///     center-pivoted while their authored node Y sits at floor level, so
    ///     a faithful origin-at-node placement sank chairs/tables/barricades
    ///     by ~half their height (chairs 19-24.5 units); PCSX2 GS captures of
    ///     the z_ho patio show the engine standing them ON the floor. The
    ///     writer's spawn-seating lift must leave every floored instance
    ///     resting within a small tolerance.
    /// </summary>
    [CorpusFact]
    public void PopulatePs2Worldzone_ZHo_QbPropsRestOnTheLevelFloor()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_ho.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_ho.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var ownerByMdl = Ps2WorldzoneQbObjectResolver
            .Resolve(pakBytes, PakArchive.GetTypedEntries(pakBytes))
            .ToDictionary(
                static pair => pair.Key.ToString("X8", CultureInfo.InvariantCulture),
                static pair => pair.Value.OwnerName,
                StringComparer.OrdinalIgnoreCase);
        var document = new ModelDocument { Name = "z_ho_seating" };
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document,
            pakBytes,
            "z_ho.pak.ps2",
            null,
            null,
            null,
            null,
            null,
            WorldzoneTimeOfDay.All,
            1f);

        var floorTriangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        var props = new Dictionary<string, List<Vector3>>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (node.MeshIndex is not { } meshIndex)
                continue;
            var mesh = document.Meshes[meshIndex];
            if (mesh.Name.Contains("_world_leaf_", StringComparison.Ordinal))
            {
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

                continue;
            }

            if (!mesh.Name.Contains("_qb_leaf_", StringComparison.Ordinal))
                continue;

            if (!props.TryGetValue(node.Name, out var points))
            {
                points = [];
                props[node.Name] = points;
            }

            foreach (var primitive in mesh.Primitives)
            {
                foreach (var vertex in primitive.Vertices)
                    points.Add(Vector3.Transform(vertex.Position, node.Transform));
            }
        }

        Assert.NotEmpty(floorTriangles);
        Assert.True(props.Count >= 60, $"expected the z_ho prop population, found {props.Count}");

        var measured = 0;
        foreach (var (name, points) in props)
        {
            var baseY = points.Min(static p => p.Y);
            var topY = points.Max(static p => p.Y);
            var centerY = (baseY + topY) * 0.5f;
            var basePoints = points.Where(p => p.Y <= baseY + 5f).ToList();
            var sampleX = basePoints.Average(static p => p.X);
            var sampleZ = basePoints.Average(static p => p.Z);

            // Same minimal-correction rule as the writer: the supporting
            // surface is the first floor at-or-above the base, no higher than
            // the spawn height (vertical center). The tolerance absorbs the
            // patio's ~2.7-unit terrace steps, which a point sample cannot
            // always attribute to the same step the writer's sample hit —
            // the pre-fix defect was 19-38 units.
            float? floorY = null;
            foreach (var (a, b, c) in floorTriangles)
            {
                if (!TryGetTrianglePlaneY(a, b, c, sampleX, sampleZ, out var y))
                    continue;
                if (y > centerY + 1f || y < baseY - 6f)
                    continue;
                if (floorY == null || y < floorY.Value)
                    floorY = y;
            }

            if (floorY is not { } floor)
                continue;

            measured++;
            var embed = floor - baseY;
            // The patio furniture (the reported defect: pre-fix embeds of
            // 19-24.5 units, GS-capture-proven to rest on the floor in-game)
            // gates strictly. The strict bound is 8, not ~0, because seating
            // is a Y-only lift to the lowest sampled support and the patio's
            // ~2.7-unit terrace steps add sample noise. Slope-sitting props
            // take a loose regression bound instead: a rotated barricade on
            // the road bank grounds its downhill corner and reads up to ~14
            // embedded at the uphill end (the engine's collision rest TILTS,
            // which a translation cannot express), and the wide plants span
            // ~113 units of terraced hillside where two legitimate samples
            // land on different terraces. Pre-fix those were 19-38 and 9-52.
            var marker = name.IndexOf("_qb_leaf_", StringComparison.Ordinal);
            var owner = marker > 0 && ownerByMdl.TryGetValue(name[..marker], out var ownerName)
                ? ownerName
                : "?";
            var bound = owner.StartsWith("chair_", StringComparison.OrdinalIgnoreCase)
                        || owner.StartsWith("table_", StringComparison.OrdinalIgnoreCase)
                ? 8f
                : 50f;
            Assert.True(
                Math.Abs(embed) <= bound,
                $"{name} ({owner}): base {baseY:0.###} vs floor {floor:0.###} (embed {embed:0.###})");
        }

        Assert.True(measured >= 50, $"expected most props to have a measurable floor, measured {measured}");
    }

    private static bool TryGetTrianglePlaneY(
        Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
    {
        y = 0f;
        var v0 = new Vector2(c.X - a.X, c.Z - a.Z);
        var v1 = new Vector2(b.X - a.X, b.Z - a.Z);
        var v2 = new Vector2(x - a.X, z - a.Z);

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

    private static int CountRenderableStripTriangles(Ps2GeomLeaf leaf)
    {
        var mesh = new ModelMesh { Name = "triangle_count" };
        return Ps2SceneGeometryWriter.AddPs2StripPrimitive(
            mesh,
            "strip",
            materialIndex: 0,
            leaf.Vertices,
            startsOnOddOutputSlot: false,
            dedup: null,
            preserveVertexAlpha: true,
            bakeVertexColorsToWhite: false)?.TriangleCount ?? 0;
    }

    private static void AssertInstance(
        Ps2WorldzoneQbObjectResolver.ObjectResourceInstances resource,
        string nodeName,
        Vector3 expectedPosition,
        Vector3 expectedAngles)
    {
        var instance = Assert.Single(resource.Instances,
            candidate => candidate.NodeChecksum == QbKeyHash.HashLower(nodeName));
        AssertVector(expectedPosition, instance.Position);
        AssertVector(expectedAngles, instance.Angles);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 0.001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 0.001f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 0.001f);
    }
}
