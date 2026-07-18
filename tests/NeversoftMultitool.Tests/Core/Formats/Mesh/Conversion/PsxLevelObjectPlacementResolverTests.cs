using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxLevelObjectPlacementResolverTests
{
    private const uint ModelHash = 0x12345678;

    [Theory]
    [InlineData("l1a2_g.psx", "l1a2_o.psx")]
    [InlineData("L1A2_G.PSX", "L1A2_o.PSX")]
    public void CompanionName_RecognizesSupportedPsxLevelGeometry(
        string fileName,
        string expectedCompanion)
    {
        Assert.True(MeshCompanionResolver.TryGetPsxLevelObjectCompanionName(
            fileName,
            out var companionName));
        Assert.Equal(expectedCompanion, companionName);
    }

    [Theory]
    [InlineData("l1a2_o.psx")]
    [InlineData("control.psx")]
    [InlineData("l1a2_g.ddm")]
    [InlineData("_g.psx")]
    public void CompanionName_RejectsUnsupportedInputs(string fileName)
    {
        Assert.False(MeshCompanionResolver.TryGetPsxLevelObjectCompanionName(
            fileName,
            out var companionName));
        Assert.Empty(companionName);
    }

    [Fact]
    public void CompanionAvailability_RequiresExistingSupportedSibling()
    {
        Assert.True(MeshCompanionResolver.HasSupportedLevelObjectCompanion(
            new CompanionAvailabilitySource("L1A2_O.PSX"),
            "l1a2_g.psx"));
        Assert.False(MeshCompanionResolver.HasSupportedLevelObjectCompanion(
            new CompanionAvailabilitySource("different_o.psx"),
            "l1a2_g.psx"));
        Assert.False(MeshCompanionResolver.HasSupportedLevelObjectCompanion(
            new CompanionAvailabilitySource("control_o.psx"),
            "control.psx"));
    }

    [Fact]
    public void Resolve_EmitsEveryAuthoredPlatformInstanceIncludingRuntimeSuspendedNodes()
    {
        var trg = BuildTriggerFile(
            BuildPlatformNode(index: 3, rawX: 90, active: true),
            BuildPlatformNode(index: 7, rawX: 180, active: false),
            BuildPlatformNode(index: 11, rawX: 270, active: true));

        var resolved = PsxLevelObjectPlacementResolver.Resolve(trg, BuildObjectBank());

        var placements = Assert.Single(resolved).Value;
        Assert.Equal([3, 7, 11], placements.Select(static placement => placement.TriggerNodeIndex));
        Assert.Equal(40f, placements[0].Transform.Translation.X, 5);
        Assert.Equal(80f, placements[1].Transform.Translation.X, 5);
        Assert.Equal(120f, placements[2].Transform.Translation.X, 5);
    }

    [Fact]
    public void Resolve_UsesUnconditionalDefaultBeforeConditionalReplacement()
    {
        var node = BuildPlatformNode(index: 4, rawX: 90, active: true);
        node.Script!.Add(new TrgScriptOp { Opcode = "0x4117", Name = "C_IF" });
        node.Script.Add(ModelChecksum(0x87654321));
        node.Script.Add(new TrgScriptOp { Opcode = "0x4120", Name = "C_ENDIF" });

        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            BuildTriggerFile(node),
            BuildObjectBank());

        var placement = Assert.Single(Assert.Single(resolved).Value);
        Assert.Equal(4, placement.TriggerNodeIndex);
    }

    [Fact]
    public void Resolve_UsesUnconditionalFallbackAfterConditionalReplacement()
    {
        var node = BuildPlatformNode(index: 5, rawX: 90, active: true);
        node.Script =
        [
            new TrgScriptOp { Opcode = "0x4117", Name = "C_IF" },
            ModelChecksum(0x87654321),
            new TrgScriptOp { Opcode = "0x4120", Name = "C_ENDIF" },
            ModelChecksum(ModelHash)
        ];

        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            BuildTriggerFile(node),
            BuildObjectBank());

        var placement = Assert.Single(Assert.Single(resolved).Value);
        Assert.Equal(5, placement.TriggerNodeIndex);
    }

    [Fact]
    public void Resolve_KeepsConditionalOnlyModelInAuthoredOverview()
    {
        var node = BuildPlatformNode(index: 8, rawX: 90, active: true);
        node.Script =
        [
            new TrgScriptOp { Opcode = "0x4117", Name = "C_IF_WHAT_IF" },
            ModelChecksum(ModelHash),
            new TrgScriptOp { Opcode = "0x4120", Name = "C_ENDIF" }
        ];

        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            BuildTriggerFile(node),
            BuildObjectBank());

        var placement = Assert.Single(Assert.Single(resolved).Value);
        Assert.Equal(8, placement.TriggerNodeIndex);
    }

    [Fact]
    public void Resolve_PreservesNativeYxzRotationAcrossTheGltfBasisChange()
    {
        var node = BuildPlatformNode(index: 6, rawX: 0, active: true);
        node.Angles = new TrgAngles
        {
            RawX = 1024,
            RawY = 1024,
            RawZ = 0
        };

        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            BuildTriggerFile(node),
            BuildObjectBank());

        var transform = Assert.Single(Assert.Single(resolved).Value).Transform;
        Assert.Equal(0f, transform.M11, 5);
        Assert.Equal(0f, transform.M12, 5);
        Assert.Equal(1f, transform.M13, 5);
        Assert.Equal(-1f, transform.M21, 5);
        Assert.Equal(0f, transform.M22, 5);
        Assert.Equal(0f, transform.M23, 5);
        Assert.Equal(0f, transform.M31, 5);
        Assert.Equal(-1f, transform.M32, 5);
        Assert.Equal(0f, transform.M33, 5);
    }

    [Fact]
    public void Resolve_TriggerReadFailureReturnsNoPlacements()
    {
        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            new ThrowingCompanionSource(),
            "test_g.psx",
            BuildObjectBank());

        Assert.Empty(resolved);
    }

    private static TrgFile BuildTriggerFile(params TrgNode[] nodes)
    {
        return new TrgFile
        {
            FileName = "test_t.trg",
            VersionMajor = 2,
            VersionMinor = 1,
            NodeCount = nodes.Length,
            Nodes = [.. nodes]
        };
    }

    private static TrgNode BuildPlatformNode(
        int index,
        int rawX,
        bool active)
    {
        return new TrgNode
        {
            Index = index,
            TypeId = TrgNodeMetadata.TypeBaddy,
            Type = "BADDY",
            SubType = 0x192,
            BaddyFlags = active ? [2, 5] : [5],
            Position = new TrgPosition { RawX = rawX },
            Angles = new TrgAngles(),
            Script =
            [
                ModelChecksum(ModelHash)
            ]
        };
    }

    private static TrgScriptOp ModelChecksum(uint checksum)
    {
        return new TrgScriptOp
        {
            Opcode = "0x212F",
            Name = "V_MODEL_CHECKSUM",
            Value = $"0x{checksum:X8}"
        };
    }

    private static PsxMeshFile BuildObjectBank()
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes =
            [
                new PsxMesh
                {
                    Vertices = [],
                    Normals = [],
                    Faces = []
                }
            ],
            MeshNameHashes = [ModelHash],
            TextureHashes = [],
            ScaleDivisor = 36f,
            TranslationDivisor = 2.25f
        };
    }

    private sealed class ThrowingCompanionSource : AssetSource
    {
        public override string DisplayName => "synthetic";
        public override string EntryName => "test_g.psx";
        public override string? FileSystemPath => null;

        public override byte[] ReadBytes()
        {
            return [];
        }

        public override bool CompanionExists(string nameWithExtension)
        {
            return true;
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            throw new InvalidDataException("Synthetic trigger read failure");
        }

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            throw new InvalidDataException("Synthetic trigger read failure");
        }
    }

    private sealed class CompanionAvailabilitySource(string companionName) : AssetSource
    {
        public override string DisplayName => "synthetic";
        public override string EntryName => "l1a2_g.psx";

        public override byte[] ReadBytes() => [];

        public override bool CompanionExists(string nameWithExtension) =>
            string.Equals(nameWithExtension, companionName, StringComparison.OrdinalIgnoreCase);

        public override byte[]? TryReadCompanion(string nameWithExtension) => null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
