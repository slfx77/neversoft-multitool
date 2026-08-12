using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxPlacedTrafficResolverTests(TestPaths paths)
{
    private const string Thps1FinalBuild =
        "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";
    private const string Thps1ProtoBuild =
        "Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)";

    [Fact]
    public void Resolve_MapsExactConstructorNames_AndCachesRepeatedSources()
    {
        var trafficBytes = BuildMinimalTrafficPsx();
        var companions = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["c_taxi.psx"] = trafficBytes,
            ["c_police.psx"] = trafficBytes,
            ["c_van.psx"] = trafficBytes,
            ["c_cable.psx"] = trafficBytes,
            ["c_kart.psx"] = trafficBytes,
            ["c_mar.psx"] = trafficBytes
        };
        var source = new TrackingCompanionSource(companions);
        var subTypes = new[]
        {
            0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xD5
        };
        var nodes = new List<TrgNode>();
        for (var i = 0; i < subTypes.Length; i++)
            nodes.Add(Baddy(i, subTypes[i], subTypes.Length + i));
        for (var i = 0; i < subTypes.Length; i++)
            nodes.Add(Road(subTypes.Length + i, i * 10, 110, -i));

        var resolved = PsxPlacedTrafficResolver.Resolve(
            source, BuildTrg(nodes), 2.25f);

        Assert.Null(source.FileSystemPath); // archive-like: companion API only
        Assert.Equal(
            [
                "c_taxi.psx", "c_police.psx", "c_van.psx",
                "c_cable.psx", "c_kart.psx", "c_mar.psx", "c_taxi.psx"
            ],
            resolved.Select(static placement => placement.Source.CompanionName));
        Assert.Same(resolved[0].Source, resolved[6].Source);
        Assert.All(companions.Keys, name => Assert.Equal(1, source.ReadCount(name)));
        Assert.All(resolved, static placement =>
        {
            Assert.True(placement.InitiallyCreated);
            Assert.True(placement.Source.MeshFile.IsSuperModel);
            Assert.Equal(PsxMeshFile.HierChunkV1Tag, placement.Source.AnimationFile.ChunkTag);
            Assert.Equal(1, placement.Source.Animation.BoneCount);
            Assert.Equal(1, placement.Source.Animation.FrameCount);
        });
    }

    [Fact]
    public void Resolve_RequiresActiveFlagAndProvenCreationPath()
    {
        var source = new TrackingCompanionSource(new Dictionary<string, byte[]>
        {
            ["c_taxi.psx"] = BuildMinimalTrafficPsx()
        });
        var scriptCreated = Baddy(1, 0xD5, 11);
        scriptCreated.BaddyFlags = [0, 2, 4];
        var initiallySuspended = Baddy(2, 0xD5, 12);
        initiallySuspended.BaddyFlags = [1, 4];
        var missingFlags = Baddy(3, 0xD5, 13);
        missingFlags.BaddyFlags = null;
        var directlyPulsed = Baddy(4, 0xD5, 14);
        directlyPulsed.BaddyFlags = [0, 2, 4];

        var resolved = PsxPlacedTrafficResolver.Resolve(
            source,
            BuildTrg(
            [
                Baddy(0, 0xD5, 10),
                scriptCreated,
                initiallySuspended,
                missingFlags,
                directlyPulsed,
                Road(10, 0, 110, 0),
                Road(11, 0, 110, 0),
                Road(12, 0, 110, 0),
                Road(13, 0, 110, 0),
                Road(14, 0, 110, 0),
                CommandPoint(20, [2, 4])
            ]),
            2.25f);

        Assert.Collection(resolved,
            static placement =>
            {
                Assert.Equal(0, placement.TriggerNodeIndex);
                Assert.True(placement.InitiallyCreated);
            },
            static placement =>
            {
                Assert.Equal(4, placement.TriggerNodeIndex);
                Assert.False(placement.InitiallyCreated);
            });
        Assert.Equal(1, source.ReadCount("c_taxi.psx"));
    }

    [Fact]
    public void Resolve_UsesFirstRoadPositionYOffsetAndNativeYxzRotation()
    {
        var source = new TrackingCompanionSource(new Dictionary<string, byte[]>
        {
            ["c_taxi.psx"] = BuildMinimalTrafficPsx()
        });
        var baddy = Baddy(0, 0xD5, 1);
        baddy.Angles = new TrgAngles
        {
            RawX = 1024,
            RawY = 1024,
            RawZ = 0
        };

        var placement = Assert.Single(PsxPlacedTrafficResolver.Resolve(
            source,
            BuildTrg([baddy, Road(1, 503, 16, -11249)]),
            2.25f));

        Assert.Equal(223.55556f, placement.RootTransform.Translation.X, 4);
        Assert.Equal(41.77778f, placement.RootTransform.Translation.Y, 4);
        Assert.Equal(4999.55556f, placement.RootTransform.Translation.Z, 4);

        // Native qY*qX*qZ, then the PSX (x,y,z)->glTF (x,-y,-z) basis.
        Assert.Equal(0f, placement.RootTransform.M11, 5);
        Assert.Equal(0f, placement.RootTransform.M12, 5);
        Assert.Equal(1f, placement.RootTransform.M13, 5);
        Assert.Equal(-1f, placement.RootTransform.M21, 5);
        Assert.Equal(0f, placement.RootTransform.M22, 5);
        Assert.Equal(0f, placement.RootTransform.M23, 5);
        Assert.Equal(0f, placement.RootTransform.M31, 5);
        Assert.Equal(-1f, placement.RootTransform.M32, 5);
        Assert.Equal(0f, placement.RootTransform.M33, 5);
    }

    [Fact]
    public void Resolve_UsesTaxiFallbackOnlyWhenPrimaryIsAbsent()
    {
        var trafficBytes = BuildMinimalTrafficPsx();
        var source = new TrackingCompanionSource(new Dictionary<string, byte[]>
        {
            ["taxi.psx"] = trafficBytes,
            ["van.psx"] = trafficBytes
        });
        var trg = BuildTrg(
        [
            Baddy(0, 0xD5, 3),
            Baddy(1, 0xD5, 4),
            Baddy(2, 0xD7, 5),
            Road(3, 0, 110, 0),
            Road(4, 1, 110, 0),
            Road(5, 2, 110, 0)
        ]);

        var resolved = PsxPlacedTrafficResolver.Resolve(source, trg, 2.25f);

        Assert.Equal(2, resolved.Count);
        Assert.All(resolved, static placement =>
            Assert.Equal("taxi.psx", placement.Source.CompanionName));
        Assert.Same(resolved[0].Source, resolved[1].Source);
        Assert.Equal(1, source.ReadCount("c_taxi.psx"));
        Assert.Equal(1, source.ReadCount("taxi.psx"));
        Assert.Equal(1, source.ReadCount("c_van.psx"));
        Assert.Equal(0, source.ReadCount("van.psx"));
    }

    [Fact]
    public void Resolve_DoesNotUseTaxiFallbackWhenPrimaryIsMalformed()
    {
        var source = new TrackingCompanionSource(new Dictionary<string, byte[]>
        {
            ["c_taxi.psx"] = [1, 2, 3],
            ["taxi.psx"] = BuildMinimalTrafficPsx()
        });

        var resolved = PsxPlacedTrafficResolver.Resolve(
            source,
            BuildTrg([Baddy(0, 0xD5, 1), Road(1, 0, 110, 0)]),
            2.25f);

        Assert.Empty(resolved);
        Assert.Equal(1, source.ReadCount("c_taxi.psx"));
        Assert.Equal(0, source.ReadCount("taxi.psx"));
    }

    [Fact]
    public void Resolve_SkipsMalformedNodesAndSourcesLocally()
    {
        var source = new TrackingCompanionSource(new Dictionary<string, byte[]>
        {
            ["c_taxi.psx"] = BuildMinimalTrafficPsx(),
            ["c_van.psx"] = [1, 2, 3]
        });
        var missingAngles = Baddy(1, 0xD5, 10);
        missingAngles.Angles = null;
        var missingLinks = Baddy(2, 0xD5, 10);
        missingLinks.Links = [];

        var resolved = PsxPlacedTrafficResolver.Resolve(
            source,
            BuildTrg(
            [
                Baddy(0, 0xD5, 10),
                missingAngles,
                missingLinks,
                Baddy(3, 0xD5, 999),
                Baddy(4, 0xD5, 11),
                Baddy(5, 0xD7, 12),
                Baddy(6, 0xD8, 13),
                Baddy(7, 0xDB, 14),
                new TrgNode { Index = 8, TypeId = TrgNodeMetadata.TypePoint, SubType = 0xD5 },
                Road(10, 20, 30, 40),
                new TrgNode { Index = 11, TypeId = TrgNodeMetadata.TypeScriptPoint },
                Road(12, 0, 110, 0),
                Road(13, 0, 110, 0),
                Road(14, 0, 110, 0)
            ]),
            2.25f);

        var placement = Assert.Single(resolved);
        Assert.Equal(0, placement.TriggerNodeIndex);
        Assert.Equal(10, placement.RoadNodeIndex);
        Assert.Equal("c_taxi.psx", placement.Source.CompanionName);
        Assert.Equal(1, source.ReadCount("c_taxi.psx"));
        Assert.Equal(1, source.ReadCount("c_van.psx"));
        Assert.Equal(1, source.ReadCount("c_cable.psx"));
    }

    [Fact]
    public void Resolve_InvalidLevelScaleReturnsNoPlacementsWithoutLoadingSources()
    {
        var source = new TrackingCompanionSource(new Dictionary<string, byte[]>
        {
            ["c_taxi.psx"] = BuildMinimalTrafficPsx()
        });
        var trg = BuildTrg([Baddy(0, 0xD5, 1), Road(1, 0, 110, 0)]);

        Assert.Empty(PsxPlacedTrafficResolver.Resolve(source, trg, 0f));
        Assert.Empty(PsxPlacedTrafficResolver.Resolve(source, trg, float.NaN));
        Assert.Equal(0, source.ReadCount("c_taxi.psx"));
    }

    [CorpusFact]
    public void Resolve_Thps1FinalDowntownFindsThreeReachableScriptedTaxis()
    {
        var (source, trg, divisor) = LoadCorpusLevel(
            Thps1FinalBuild, "skdown.psx", "skdown_t.trg");

        var candidates = TrafficNodes(trg);
        Assert.Collection(candidates,
            static node => AssertTrafficNode(node, 148, 0xD5, 156, 0, 2049, 0),
            static node => AssertTrafficNode(node, 304, 0xD5, 315, 0, 0, 0),
            static node => AssertTrafficNode(node, 728, 0xD5, 143, 0, 170, 0));
        var resolved = PsxPlacedTrafficResolver.Resolve(source, trg, divisor);

        Assert.Equal([148, 304, 728],
            resolved.Select(static placement => placement.TriggerNodeIndex));
        Assert.Equal([156, 315, 143],
            resolved.Select(static placement => placement.RoadNodeIndex));
        Assert.All(resolved, static placement =>
        {
            Assert.False(placement.InitiallyCreated);
            Assert.Equal(0xD5, placement.SubType);
            Assert.Equal("c_taxi.psx", placement.Source.CompanionName);
        });
        Assert.Same(resolved[0].Source, resolved[1].Source);
        Assert.Same(resolved[0].Source, resolved[2].Source);

        AssertRoot(resolved[0],
            new Vector3(503f / 2.25f, 94f / 2.25f, 11249f / 2.25f),
            0, 2049, 0);
        AssertRoot(resolved[1],
            new Vector3(-10614f / 2.25f, 518f / 2.25f, 3328f / 2.25f),
            0, 0, 0);
        AssertRoot(resolved[2],
            new Vector3(10414f / 2.25f, 603f / 2.25f, -1054f / 2.25f),
            0, 170, 0);

        var traffic = resolved[0].Source;
        Assert.Equal(5, traffic.MeshFile.Objects.Count);
        Assert.Equal(5, traffic.MeshFile.Meshes.Count);
        Assert.Equal(237, TriangleCount(traffic.MeshFile));
        Assert.Equal(PsxMeshFile.HierChunkV1Tag, traffic.AnimationFile.ChunkTag);
        Assert.Equal(2, traffic.Animation.FrameCount);
        Assert.Equal(5, traffic.Animation.BoneCount);
        for (var bone = 0; bone < traffic.Animation.BoneCount; bone++)
        {
            var frame0 = traffic.Animation.GetBoneRotation(bone, 0);
            var frame1 = traffic.Animation.GetBoneRotation(bone, 1);
            Assert.True(MathF.Abs(Quaternion.Dot(frame0, frame1)) < 0.99999f,
                $"Taxi bone {bone} did not rotate between its two frames.");
        }
    }

    [CorpusFact]
    public void Resolve_Thps1FinalSanFranciscoFindsThreeReachableScriptedCars()
    {
        var (source, trg, divisor) = LoadCorpusLevel(
            Thps1FinalBuild, "sksf.psx", "sksf_t.trg");

        var candidates = TrafficNodes(trg);
        Assert.Collection(candidates,
            static node => AssertTrafficNode(node, 96, 0xD7, 78, 0, 0, 0),
            static node => AssertTrafficNode(node, 484, 0xD8, 478, 0, 0, 0),
            static node => AssertTrafficNode(node, 892, 0xD8, 886, 0, 0, 0));
        var resolved = PsxPlacedTrafficResolver.Resolve(source, trg, divisor);

        Assert.Equal(3, resolved.Count);
        Assert.All(resolved, static placement =>
            Assert.False(placement.InitiallyCreated));
        var van = Assert.Single(resolved, static placement =>
            placement.Source.CompanionName == "c_van.psx");
        var cableCars = resolved.Where(static placement =>
            placement.Source.CompanionName == "c_cable.psx").ToArray();
        Assert.Equal(2, cableCars.Length);
        Assert.Same(cableCars[0].Source, cableCars[1].Source);
        Assert.Equal(210, TriangleCount(van.Source.MeshFile));
        Assert.Equal(54, TriangleCount(cableCars[0].Source.MeshFile));
        Assert.Equal(318, resolved.Sum(static placement =>
            TriangleCount(placement.Source.MeshFile)));
    }

    [CorpusFact]
    public void Resolve_Thps1PrototypeDowntownFindsReachableScriptedTaxi()
    {
        var (source, trg, divisor) = LoadCorpusLevel(
            Thps1ProtoBuild, "skdown.psx", "skdown_t.trg");

        var candidates = TrafficNodes(trg);
        Assert.Collection(candidates,
            static node => AssertTrafficNode(node, 250, 0xD5, 243, 0, 0, 0));
        var placement = Assert.Single(PsxPlacedTrafficResolver.Resolve(
            source, trg, divisor));

        Assert.False(placement.InitiallyCreated);
        Assert.Equal(250, placement.TriggerNodeIndex);
        Assert.Equal(243, placement.RoadNodeIndex);
        Assert.Equal(0xD5, placement.SubType);
        Assert.Equal("taxi.psx", placement.Source.CompanionName);
        Assert.Equal(5, placement.Source.MeshFile.Objects.Count);
        Assert.False(placement.Source.MeshFile.HasHierarchy);
        Assert.Equal(1, placement.Source.Animation.FrameCount);
        Assert.Equal(5, placement.Source.Animation.BoneCount);
    }

    [CorpusFact]
    public void Resolve_Thps1FinalBurnsideRejectsUnreachableScriptCreatedTraffic()
    {
        var (source, trg, divisor) = LoadCorpusLevel(
            Thps1FinalBuild, "skburn.psx", "skburn_t.trg");

        var candidates = TrafficNodes(trg);
        Assert.Collection(candidates,
            static node => AssertTrafficNode(node, 219, 0xD5, 209, 0, 0, 0));
        Assert.Empty(PsxPlacedTrafficResolver.Resolve(source, trg, divisor));
    }

    private (FileSystemAssetSource Source, TrgFile Trg, float Divisor) LoadCorpusLevel(
        string build,
        string levelFileName,
        string trgFileName)
    {
        var levelPath = paths.FindSampleFile(build, levelFileName);
        var trgPath = paths.FindSampleFile(build, trgFileName);
        Assert.SkipWhen(levelPath == null || trgPath == null,
            $"{build} {levelFileName}/{trgFileName} fixtures are not available");

        var level = PsxMeshFile.Parse(levelPath!);
        Assert.NotNull(level);
        return (
            new FileSystemAssetSource(levelPath!),
            TrgFile.Parse(trgPath!),
            level!.TranslationDivisor);
    }

    private static void AssertRoot(
        PsxPlacedTrafficPlacement placement,
        Vector3 translation,
        short angleX,
        short angleY,
        short angleZ)
    {
        Assert.Equal(translation.X, placement.RootTransform.Translation.X, 4);
        Assert.Equal(translation.Y, placement.RootTransform.Translation.Y, 4);
        Assert.Equal(translation.Z, placement.RootTransform.Translation.Z, 4);

        var expectedNative = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(angleY))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(angleX))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(angleZ)));
        var expectedGltf = Quaternion.Normalize(new Quaternion(
            expectedNative.X,
            -expectedNative.Y,
            -expectedNative.Z,
            expectedNative.W));
        var expected = Matrix4x4.CreateFromQuaternion(expectedGltf);

        Assert.Equal(expected.M11, placement.RootTransform.M11, 5);
        Assert.Equal(expected.M12, placement.RootTransform.M12, 5);
        Assert.Equal(expected.M13, placement.RootTransform.M13, 5);
        Assert.Equal(expected.M21, placement.RootTransform.M21, 5);
        Assert.Equal(expected.M22, placement.RootTransform.M22, 5);
        Assert.Equal(expected.M23, placement.RootTransform.M23, 5);
        Assert.Equal(expected.M31, placement.RootTransform.M31, 5);
        Assert.Equal(expected.M32, placement.RootTransform.M32, 5);
        Assert.Equal(expected.M33, placement.RootTransform.M33, 5);
    }

    private static float Radians(short angle)
    {
        return (angle & 0x0fff) * (2f * MathF.PI / 4096f);
    }

    private static int TriangleCount(PsxMeshFile file)
    {
        return file.Meshes.Sum(static mesh =>
            mesh.Faces.Sum(static face => face.IsQuad ? 2 : 1));
    }

    private static TrgNode[] TrafficNodes(TrgFile trg)
    {
        return trg.Nodes.Where(static node =>
                node.TypeId == TrgNodeMetadata.TypeBaddy
                && node.SubType is >= 0xD5 and <= 0xDA)
            .ToArray();
    }

    private static void AssertTrafficNode(
        TrgNode node,
        int index,
        int subType,
        int roadNodeIndex,
        short angleX,
        short angleY,
        short angleZ)
    {
        Assert.Equal(index, node.Index);
        Assert.Equal(subType, node.SubType);
        Assert.Equal([roadNodeIndex], node.Links);
        Assert.Equal([0, 2, 4], node.BaddyFlags);
        Assert.NotNull(node.Angles);
        Assert.Equal(angleX, node.Angles.RawX);
        Assert.Equal(angleY, node.Angles.RawY);
        Assert.Equal(angleZ, node.Angles.RawZ);
    }

    private static TrgFile BuildTrg(IEnumerable<TrgNode> nodes)
    {
        var materialized = nodes.ToList();
        return new TrgFile
        {
            FileName = "synthetic_t.trg",
            VersionMajor = 2,
            VersionMinor = 0,
            NodeCount = materialized.Count,
            Nodes = materialized
        };
    }

    private static TrgNode Baddy(int index, int subType, int roadNodeIndex)
    {
        return new TrgNode
        {
            Index = index,
            TypeId = TrgNodeMetadata.TypeBaddy,
            Type = "BADDY",
            SubType = subType,
            Links = [roadNodeIndex],
            Angles = new TrgAngles(),
            BaddyFlags = [1, 2, 4]
        };
    }

    private static TrgNode Road(int index, int rawX, int rawY, int rawZ)
    {
        return new TrgNode
        {
            Index = index,
            TypeId = TrgNodeMetadata.TypeScriptPoint,
            Type = "SCRIPTPOINT",
            Position = new TrgPosition
            {
                RawX = rawX,
                RawY = rawY,
                RawZ = rawZ
            }
        };
    }

    private static TrgNode CommandPoint(int index, List<int> links)
    {
        return new TrgNode
        {
            Index = index,
            TypeId = TrgNodeMetadata.TypeCommandPoint,
            Type = "COMMANDPOINT",
            Links = links,
            Commands =
            [
                new TrgCommand
                {
                    Opcode = 0x86,
                    Name = "SetInitialPulses",
                    Args = [(ushort)1]
                },
                new TrgCommand { Opcode = 3, Name = "SendPulse" }
            ]
        };
    }

    /// <summary>
    ///     One-object v4 super with an empty but valid mesh and one one-frame
    ///     direct-matrix animation. It exercises the production mesh, table and
    ///     decoder paths rather than substituting parsed objects in the tests.
    /// </summary>
    private static byte[] BuildMinimalTrafficPsx()
    {
        const int meshTop = 0x70;
        var data = new byte[meshTop + 0x1C];

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x00), 0x04);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x02), 0x02);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08), 1);
        // The zero-filled object record at 0x0C selects mesh index zero.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34), meshTop);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(0x38), PsxMeshFile.HierChunkV1Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 0x24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x48), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x4A), 0);

        Span<short> matrix =
        [
            4096, 0, 0,
            0, 4096, 0,
            0, 0, 4096,
            0, 0, 0
        ];
        for (var i = 0; i < matrix.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(0x4C + i * 2), matrix[i]);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x64), uint.MaxValue);
        // 0x68 mesh name hash and 0x6C texture-count remain zero.

        // Empty v4 mesh: flags/counts, bbox, then zMax/NextLOD.
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(meshTop + 0x18), short.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(meshTop + 0x1A), ushort.MaxValue);
        return data;
    }

    private sealed class TrackingCompanionSource(
        IReadOnlyDictionary<string, byte[]> companions) : AssetSource
    {
        private readonly Dictionary<string, int> _reads =
            new(StringComparer.OrdinalIgnoreCase);

        public override string DisplayName => "synthetic.pre::level.psx";
        public override string EntryName => "level.psx";

        public int ReadCount(string name)
        {
            return _reads.GetValueOrDefault(name);
        }

        public override byte[] ReadBytes()
        {
            throw new InvalidOperationException(
                "Resolver must use parent companion lookups, not read the level source.");
        }

        public override bool CompanionExists(string nameWithExtension)
        {
            return companions.ContainsKey(nameWithExtension);
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            _reads[nameWithExtension] = ReadCount(nameWithExtension) + 1;
            return companions.TryGetValue(nameWithExtension, out var bytes) ? bytes : null;
        }

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            foreach (var extension in extensions)
            {
                if (TryReadCompanion(stem + extension) is { } bytes)
                    return bytes;
            }

            return null;
        }
    }
}
