using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxPlacedTrafficParserTests(TestPaths paths)
{
    private const string Thps1FinalBuild =
        "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";

    [CorpusFact]
    public void FinalTrafficSources_AreHierModelsDrivenByAbsoluteV1Clips()
    {
        var sources = ResolveTrafficSources("skdown")
            .Concat(ResolveTrafficSources("sksf"))
            .GroupBy(static source => source.CompanionName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static source => source.CompanionName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["c_cable.psx", "c_taxi.psx", "c_van.psx"],
            sources.Select(static source => source.CompanionName));
        Assert.All(sources, static source =>
        {
            Assert.True(source.MeshFile.HasHierarchy);
            Assert.Equal(PsxMeshFile.HierChunkV1Tag, source.AnimationFile.ChunkTag);
            Assert.True(source.Animation.AbsoluteWorldTranslations);
        });
    }

    [Fact]
    public void SourceTransaction_EmptyGeometryRollsBackEveryAppend()
    {
        var document = CreateSeedDocument();
        var source = CreateSyntheticSource("empty.psx", withTriangle: false);
        var placement = CreateSyntheticPlacement(source, 10, Matrix4x4.Identity);

        var emitted = MeshModelParser.TryPopulatePsxPlacedTrafficSource(
            document, NullAssetSource.Instance, null, source, [placement]);

        Assert.False(emitted);
        AssertSeedDocumentUnchanged(document);
    }

    [Fact]
    public void SourceTransaction_SecondInstanceFailureRollsBackThenOtherSourceEmits()
    {
        var document = CreateSeedDocument();
        var rejectedSource = CreateSyntheticSource("rejected.psx", withTriangle: true);
        var invalidRoot = Matrix4x4.Identity;
        invalidRoot.M41 = float.NaN;

        var rejected = MeshModelParser.TryPopulatePsxPlacedTrafficSource(
            document,
            NullAssetSource.Instance,
            null,
            rejectedSource,
            [
                CreateSyntheticPlacement(rejectedSource, 20, Matrix4x4.Identity),
                CreateSyntheticPlacement(rejectedSource, 21, invalidRoot)
            ]);

        Assert.False(rejected);
        AssertSeedDocumentUnchanged(document);

        var validSource = CreateSyntheticSource("valid.psx", withTriangle: true);
        var valid = MeshModelParser.TryPopulatePsxPlacedTrafficSource(
            document,
            NullAssetSource.Instance,
            null,
            validSource,
            [CreateSyntheticPlacement(
                validSource, 30, Matrix4x4.CreateTranslation(4f, 5f, 6f))]);

        Assert.True(valid);
        Assert.Equal(2, document.Skeletons.Count);
        Assert.Equal(2, document.Meshes.Count);
        Assert.Equal(2, document.Nodes.Count);
        Assert.Equal(2, document.Animations.Count);
        var primitive = Assert.Single(document.Meshes[1].Primitives);
        Assert.Equal(1, primitive.TriangleCount);
        Assert.Equal(1, primitive.Skin!.SkeletonIndex);
        var animation = document.Animations[1];
        Assert.Equal("valid_anim_0", animation.Name);
        Assert.Single(animation.Channels);
        Assert.Equal(1, animation.Channels[0].SkeletonIndex);
    }

    [CorpusFact]
    public void Downtown_DefaultRegistersScriptedSnapshotWithoutEmittingTraffic()
    {
        var document = ParseLevel(Thps1FinalBuild, "skdown.psx");
        var group = Assert.Single(document.VisibilityGroups,
            static candidate => candidate.Id.StartsWith(
                "psx.scripted_traffic.", StringComparison.Ordinal));

        Assert.Equal(ScriptedTrafficId("skdown"), group.Id);
        Assert.Equal("Possible scripted traffic snapshot", group.Label);
        Assert.False(group.DefaultEnabled);
        Assert.False(group.IsEnabled);
        Assert.Equal(ModelVisibilityGroupSource.TriggerCondition, group.Source);
        Assert.Contains("BADDY nodes 148, 304, 728", group.SourceReference,
            StringComparison.Ordinal);
        Assert.Contains("no path motion, timing, or repeats", group.SourceReference,
            StringComparison.Ordinal);
        AssertNoTrafficContent(document);

        var withoutLevelObjects = ParseLevel(
            Thps1FinalBuild, "skdown.psx", includeLevelObjects: false);
        Assert.DoesNotContain(withoutLevelObjects.VisibilityGroups,
            static candidate => candidate.Id.StartsWith(
                "psx.scripted_traffic.", StringComparison.Ordinal));
        AssertNoTrafficContent(withoutLevelObjects);
    }

    [CorpusFact]
    public void Downtown_OverrideEmitsThreePlacedTaxisAndOneSharedAnimation()
    {
        var defaultDocument = ParseLevel(Thps1FinalBuild, "skdown.psx");
        var document = ParseLevel(
            Thps1FinalBuild,
            "skdown.psx",
            visibilityOverrides: new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ScriptedTrafficId("skdown")] = true
            });

        var group = Assert.Single(document.VisibilityGroups,
            candidate => candidate.Id == ScriptedTrafficId("skdown"));
        Assert.True(group.IsEnabled);
        Assert.False(group.DefaultEnabled);

        var trafficMeshes = TrafficMeshes(document);
        Assert.Equal(3, trafficMeshes.Length);
        Assert.Equal(3, trafficMeshes.Select(static mesh => mesh.Name).Distinct().Count());
        Assert.Equal(711, trafficMeshes.Sum(static mesh =>
            mesh.Primitives.Sum(static primitive => primitive.TriangleCount)));
        Assert.Equal(defaultDocument.TriangleCount + 711, document.TriangleCount);

        var trafficPrimitives = trafficMeshes
            .SelectMany(static mesh => mesh.Primitives)
            .ToArray();
        Assert.NotEmpty(trafficPrimitives);
        Assert.All(trafficPrimitives, static primitive => Assert.NotNull(primitive.Skin));
        Assert.Equal(3, trafficPrimitives
            .Select(static primitive => primitive.Skin!.SkeletonIndex)
            .Distinct()
            .Count());

        var skeletons = document.Skeletons
            .Where(static skeleton => skeleton.Name.StartsWith(
                "traffic_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, skeletons.Length);
        Assert.Equal(3, skeletons.Select(static skeleton => skeleton.Name).Distinct().Count());
        Assert.All(skeletons, static skeleton =>
        {
            Assert.Equal(5, skeleton.Bones.Count);
            // c_taxi is a HIER model driven by a v1 direct-matrix clip. Its
            // absolute per-part transforms require the established flat bind.
            Assert.All(skeleton.Bones, static bone => Assert.Equal(-1, bone.ParentIndex));
        });
        var boneNames = skeletons.SelectMany(static skeleton => skeleton.Bones)
            .Select(static bone => bone.Name)
            .ToArray();
        Assert.Equal(15, boneNames.Length);
        Assert.Equal(15, boneNames.Distinct(StringComparer.Ordinal).Count());

        // Re-pinned 2026-08-17: rotation follows the spawn road segment
        // (CCar_Update's converged heading), rebuilt here from the TRG rather
        // than hard-coded so the pin names its own evidence. Trigger nodes
        // 148/304/728 spawn at road nodes 156/315/143.
        var trg = LoadTrg(Thps1FinalBuild, "skdown_t.trg");
        var expectedRoots = new[]
        {
            CreateExpectedRoadRoot(trg, 156,
                new Vector3(503f / 2.25f, 94f / 2.25f, 11249f / 2.25f)),
            CreateExpectedRoadRoot(trg, 315,
                new Vector3(-10614f / 2.25f, 518f / 2.25f, 3328f / 2.25f)),
            CreateExpectedRoadRoot(trg, 143,
                new Vector3(10414f / 2.25f, 603f / 2.25f, -1054f / 2.25f))
        };
        for (var i = 0; i < expectedRoots.Length; i++)
            AssertMatrixClose(expectedRoots[i], skeletons[i].RootTransform);
        Assert.Equal(3, skeletons
            .Select(static skeleton => skeleton.RootTransform.Translation)
            .Distinct()
            .Count());

        var animation = Assert.Single(document.Animations);
        Assert.Equal("c_taxi_anim_0", animation.Name);
        Assert.Equal(30, animation.Channels.Count);
        Assert.All(animation.Channels, static channel =>
        {
            Assert.Equal(2, channel.KeyCount);
            Assert.Equal(0f, channel.Times[0], 6);
            Assert.Equal(1f / 30f, channel.Times[1], 6);
        });
        AssertEquivalentLocalChannels(animation, 0, 1);
        AssertEquivalentLocalChannels(animation, 0, 2);

        AssertGlbTraffic(document, expectedRoots);
        AssertBlendTraffic(document, expectedRoots);
    }

    [CorpusFact]
    public void SanFrancisco_OverrideEmitsVanAndCableCarSourceGroups()
    {
        var defaultDocument = ParseLevel(Thps1FinalBuild, "sksf.psx");
        var document = ParseLevel(
            Thps1FinalBuild,
            "sksf.psx",
            visibilityOverrides: new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ScriptedTrafficId("sksf")] = true
            });

        var group = Assert.Single(document.VisibilityGroups,
            candidate => candidate.Id == ScriptedTrafficId("sksf"));
        Assert.True(group.IsEnabled);

        var trafficMeshes = TrafficMeshes(document);
        Assert.Equal(3, trafficMeshes.Length);
        Assert.Equal(318, trafficMeshes.Sum(static mesh =>
            mesh.Primitives.Sum(static primitive => primitive.TriangleCount)));
        Assert.Equal(defaultDocument.TriangleCount + 318, document.TriangleCount);
        Assert.Single(trafficMeshes,
            static mesh => mesh.Name.Contains("c_van", StringComparison.Ordinal));
        Assert.Equal(2, trafficMeshes.Count(
            static mesh => mesh.Name.Contains("c_cable", StringComparison.Ordinal)));
        Assert.Equal(3, document.Skeletons.Count(static skeleton =>
            skeleton.Name.StartsWith("traffic_", StringComparison.Ordinal)));
        Assert.Equal(3, trafficMeshes
            .SelectMany(static mesh => mesh.Primitives)
            .Select(static primitive => primitive.Skin!.SkeletonIndex)
            .Distinct()
            .Count());
        Assert.Equal(
            ["c_cable_anim_0", "c_van_anim_0"],
            document.Animations.Select(static animation => animation.Name)
                .Order(StringComparer.Ordinal));
    }

    private ModelDocument ParseLevel(
        string build,
        string fileName,
        bool includeLevelObjects = true,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null)
    {
        var path = paths.FindSampleFile(build, fileName);
        Assert.SkipWhen(path == null, $"{build} {fileName} fixture is not available");

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path!),
            FileName = fileName,
            OutputStem = Path.GetFileNameWithoutExtension(fileName),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = includeLevelObjects,
            VisibilityOverrides = visibilityOverrides
        });
    }

    private PsxPlacedTrafficSource[] ResolveTrafficSources(string levelStem)
    {
        var levelPath = paths.FindSampleFile(Thps1FinalBuild, levelStem + ".psx");
        var trgPath = paths.FindSampleFile(Thps1FinalBuild, levelStem + "_t.trg");
        Assert.SkipWhen(levelPath == null || trgPath == null,
            $"{Thps1FinalBuild} {levelStem} traffic fixtures are not available");
        var level = PsxMeshFile.Parse(levelPath!);
        Assert.NotNull(level);
        var source = new FileSystemAssetSource(levelPath!);
        return PsxPlacedTrafficResolver.Resolve(
                source,
                TrgFile.Parse(trgPath!),
                level!.TranslationDivisor)
            .Select(static placement => placement.Source)
            .Distinct()
            .ToArray();
    }

    private static string ScriptedTrafficId(string levelStem)
    {
        var hash = NeversoftMultitool.Core.QbKey.QbKey.Hash(levelStem.ToUpperInvariant());
        return $"psx.scripted_traffic.{hash:X8}";
    }

    private static ModelMesh[] TrafficMeshes(ModelDocument document)
    {
        return document.Meshes
            .Where(static mesh => mesh.Name.StartsWith("traffic_", StringComparison.Ordinal))
            .ToArray();
    }

    private static void AssertNoTrafficContent(ModelDocument document)
    {
        Assert.Empty(TrafficMeshes(document));
        Assert.DoesNotContain(document.Skeletons, static skeleton =>
            skeleton.Name.StartsWith("traffic_", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Animations, static animation =>
            animation.Name.EndsWith("_anim_0", StringComparison.Ordinal)
            && (animation.Name.StartsWith("c_", StringComparison.Ordinal)
                || animation.Name == "taxi_anim_0"));
    }

    private static void AssertEquivalentLocalChannels(
        ModelAnimation animation,
        int expectedSkeletonIndex,
        int actualSkeletonIndex)
    {
        var expected = animation.Channels
            .Where(channel => channel.SkeletonIndex == expectedSkeletonIndex)
            .ToDictionary(static channel => (channel.BoneIndex, channel.Property));
        var actual = animation.Channels
            .Where(channel => channel.SkeletonIndex == actualSkeletonIndex)
            .ToDictionary(static channel => (channel.BoneIndex, channel.Property));

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var key in expected.Keys)
        {
            Assert.Equal(expected[key].Times, actual[key].Times);
            Assert.Equal(expected[key].Values, actual[key].Values);
        }
    }

    private static void AssertGlbTraffic(
        ModelDocument document,
        Matrix4x4[] expectedRoots)
    {
        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.Equal(document.TriangleCount, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, false);
        var glb = ModelRoot.ReadGLB(stream);

        Assert.Equal(3, glb.LogicalSkins.Count);
        var animation = Assert.Single(glb.LogicalAnimations);
        Assert.Equal("c_taxi_anim_0", animation.Name);
        Assert.Equal(30, animation.Channels.Count);
        var roots = glb.LogicalNodes
            .Where(static node => node.Name?.StartsWith(
                                      "traffic_", StringComparison.Ordinal) == true
                                  && node.Name.EndsWith(
                                      "_skeleton_root", StringComparison.Ordinal))
            .OrderBy(static node => node.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, roots.Length);

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            AssertMatrixClose(expectedRoots[i], root.LocalMatrix);
            AssertMatrixClose(root.LocalMatrix, root.GetWorldMatrix(animation, 0f));
            AssertMatrixClose(root.LocalMatrix, root.GetWorldMatrix(animation, 1f / 30f));

            var rotationChannels = animation.Channels
                .Where(channel => channel.TargetNodePath == PropertyPath.rotation
                                  && ReferenceEquals(channel.TargetNode.VisualParent, root))
                .ToArray();
            Assert.Equal(5, rotationChannels.Length);
            Assert.All(rotationChannels, static channel =>
            {
                var keys = channel.GetRotationSampler().GetLinearKeys().ToArray();
                Assert.Equal(2, keys.Length);
                Assert.True(MathF.Abs(Quaternion.Dot(keys[0].Value, keys[1].Value)) < 0.99999f,
                    $"GLB bone {channel.TargetNode.Name} did not rotate locally.");
            });
        }
    }

    private static void AssertBlendTraffic(
        ModelDocument document,
        Matrix4x4[] expectedRoots)
    {
        using var package = new MemoryStream();
        BlendPackageWriter.Write(document, package, "skdown_scripted_traffic.blend");
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);

        var skeletons = manifest.RootElement.GetProperty("Skeletons");
        Assert.Equal(3, skeletons.GetArrayLength());
        for (var i = 0; i < expectedRoots.Length; i++)
        {
            AssertMatrixClose(
                expectedRoots[i],
                ReadMatrix(skeletons[i].GetProperty("RootTransform")));
        }

        var animation = Assert.Single(
            manifest.RootElement.GetProperty("Animations").EnumerateArray());
        Assert.Equal("c_taxi_anim_0", animation.GetProperty("Name").GetString());
        Assert.Equal(30, animation.GetProperty("Channels").GetArrayLength());
        Assert.All(animation.GetProperty("Channels").EnumerateArray(), static channel =>
            Assert.Equal(2, channel.GetProperty("KeyCount").GetInt32()));
    }

    private TrgFile LoadTrg(string build, string trgFileName)
    {
        var trgPath = paths.FindSampleFile(build, trgFileName);
        Assert.SkipWhen(trgPath == null, $"{build} {trgFileName} fixture is not available");
        return TrgFile.Parse(trgPath!);
    }

    /// <summary>
    ///     Expected traffic root: facing along the spawn road segment, exactly
    ///     CCar_Update's converged orientation (yaw = atan2(-dx,-dz) about Y,
    ///     pitch = atan2(dyNormalized, 1) about X, roll 0), independently
    ///     rebuilt from the TRG's road-node graph.
    /// </summary>
    private static Matrix4x4 CreateExpectedRoadRoot(
        TrgFile trg,
        int roadNodeIndex,
        Vector3 translation)
    {
        var byIndex = trg.Nodes.ToDictionary(static n => n.Index, static n => n);
        var road = byIndex[roadNodeIndex];
        var next = byIndex[road.Links![0]];
        var direction = Vector3.Normalize(new Vector3(
            next.Position!.RawX - road.Position!.RawX,
            next.Position.RawY - road.Position.RawY,
            next.Position.RawZ - road.Position.RawZ));
        var native = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(
                Vector3.UnitY, MathF.Atan2(-direction.X, -direction.Z))
            * Quaternion.CreateFromAxisAngle(
                Vector3.UnitX, MathF.Atan2(direction.Y, 1f)));
        var gltf = Quaternion.Normalize(new Quaternion(
            native.X, -native.Y, -native.Z, native.W));
        var result = Matrix4x4.CreateFromQuaternion(gltf);
        result.Translation = translation;
        return result;
    }

    private static Matrix4x4 CreateExpectedRoot(
        Vector3 translation,
        short angleX,
        short angleY,
        short angleZ)
    {
        var native = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(angleY))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(angleX))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(angleZ)));
        var gltf = Quaternion.Normalize(new Quaternion(
            native.X, -native.Y, -native.Z, native.W));
        var result = Matrix4x4.CreateFromQuaternion(gltf);
        result.Translation = translation;
        return result;
    }

    private static float Radians(short angle)
    {
        return (angle & 0x0fff) * (2f * MathF.PI / 4096f);
    }

    private static Matrix4x4 ReadMatrix(JsonElement element)
    {
        var values = element.EnumerateArray().Select(static value => value.GetSingle()).ToArray();
        Assert.Equal(16, values.Length);
        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        var expectedValues = MatrixValues(expected);
        var actualValues = MatrixValues(actual);
        for (var i = 0; i < expectedValues.Length; i++)
            Assert.Equal(expectedValues[i], actualValues[i], 4);
    }

    private static float[] MatrixValues(Matrix4x4 matrix) =>
    [
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44
    ];

    private static ModelDocument CreateSeedDocument()
    {
        var document = new ModelDocument
        {
            Name = "seed",
            TriangleCount = 17
        };
        document.Scenes.Add(new ModelScene { Name = "seed_scene" });
        document.Nodes.Add(new ModelNode { Name = "seed_node" });
        document.Scenes[0].RootNodeIndices.Add(0);
        document.Meshes.Add(new ModelMesh { Name = "seed_mesh" });
        document.Materials.Add(new RenderMaterial { Name = "seed_material" });
        document.Textures.Add(new ModelTexture { Name = "seed_texture" });
        document.Skeletons.Add(new ModelSkeleton { Name = "seed_skeleton" });
        document.Animations.Add(new ModelAnimation { Name = "seed_animation" });
        document.NativeMetadata.Add(new CollisionRenderMetadata(1));
        return document;
    }

    private static void AssertSeedDocumentUnchanged(ModelDocument document)
    {
        Assert.Equal(17, document.TriangleCount);
        Assert.Single(document.Scenes);
        Assert.Equal([0], document.Scenes[0].RootNodeIndices);
        Assert.Equal("seed_node", Assert.Single(document.Nodes).Name);
        Assert.Equal("seed_mesh", Assert.Single(document.Meshes).Name);
        Assert.Equal("seed_material", Assert.Single(document.Materials).Name);
        Assert.Equal("seed_texture", Assert.Single(document.Textures).Name);
        Assert.Equal("seed_skeleton", Assert.Single(document.Skeletons).Name);
        Assert.Equal("seed_animation", Assert.Single(document.Animations).Name);
        Assert.Single(document.NativeMetadata);
    }

    private static PsxPlacedTrafficSource CreateSyntheticSource(
        string name,
        bool withTriangle)
    {
        var file = new PsxMeshFile
        {
            Version = 4,
            IsSuperModel = true,
            HasHierarchy = false,
            ScaleDivisor = 1f,
            TranslationDivisor = 1f,
            Objects = [new PsxMeshObject { MeshIndex = 0, ParentIndex = -1 }],
            Meshes = [withTriangle ? CreateSyntheticTriangle() : CreateEmptyMesh()],
            MeshNameHashes = [0u],
            TextureHashes = []
        };
        var channels = new short[1, PsxAnimation.ChannelsPerBone, 2];
        channels[0, 2, 1] = 512;
        var animation = new PsxAnimation
        {
            FrameCount = 2,
            BoneCount = 1,
            Channels = channels,
            AbsoluteWorldTranslations = true
        };
        return new PsxPlacedTrafficSource(name, [], file, null!, animation);
    }

    private static PsxMesh CreateEmptyMesh()
    {
        return new PsxMesh
        {
            Vertices = [],
            Normals = [],
            Faces = [],
            LodNextMeshIndex = ushort.MaxValue
        };
    }

    private static PsxMesh CreateSyntheticTriangle()
    {
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                new PsxVertex { X = 1f, Y = 0f, Z = 0f },
                new PsxVertex { X = 0f, Y = 1f, Z = 0f }
            ],
            Normals = [new PsxNormal { Z = 1f }],
            Faces =
            [
                new PsxFace
                {
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    NormalIndex = 0,
                    R = 255,
                    G = 255,
                    B = 255
                }
            ],
            VertexCount = 3,
            LodNextMeshIndex = ushort.MaxValue
        };
    }

    private static PsxPlacedTrafficPlacement CreateSyntheticPlacement(
        PsxPlacedTrafficSource source,
        int triggerNodeIndex,
        Matrix4x4 rootTransform)
    {
        return new PsxPlacedTrafficPlacement(
            triggerNodeIndex,
            triggerNodeIndex + 100,
            PsxPlacedTrafficResolver.TaxiSubType,
            true,
            rootTransform,
            source);
    }

    private sealed class NullAssetSource : AssetSource
    {
        internal static NullAssetSource Instance { get; } = new();

        public override string DisplayName => "synthetic.psx";
        public override string EntryName => "synthetic.psx";
        public override byte[] ReadBytes() => [];
        public override bool CompanionExists(string nameWithExtension) => false;
        public override byte[]? TryReadCompanion(string nameWithExtension) => null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
