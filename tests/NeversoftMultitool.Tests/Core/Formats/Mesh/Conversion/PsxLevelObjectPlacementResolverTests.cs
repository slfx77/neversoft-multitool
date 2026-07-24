using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxLevelObjectPlacementResolverTests
{
    private const uint ModelHash = 0x12345678;

    [Theory]
    [InlineData("l1a2_g.psx", "l1a2", "l1a2_o.psx")]
    [InlineData("L1A2_G.PSX", "L1A2", "L1A2_o.psx")]
    public void Companions_RecognizeSpiderManGeometryUnconditionally(
        string fileName,
        string expectedStem,
        string expectedBank)
    {
        // The _g marker resolves without probing siblings (the bank is optional).
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource(), fileName, out var companions));
        Assert.Equal(expectedStem, companions.LevelStem);
        Assert.Equal(expectedBank, companions.BankCompanionName);
    }

    [Fact]
    public void Companions_RecognizeThpsBareStemLevelBySiblingBankAndTrigger()
    {
        // THPS1/THPS2 level geometry (skware.psx) carries no marker suffix; a
        // sibling _o bank plus a _t.trg proves it is a placeable level.
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource("skware_o.psx", "skware_t.trg"),
            "skware.psx",
            out var companions));
        Assert.Equal("skware", companions.LevelStem);
        Assert.Equal("skware_o.psx", companions.BankCompanionName);
    }

    [Theory]
    [InlineData("l1a2_o.psx")]  // the bank itself has no _o bank of its own
    [InlineData("control.psx")] // a character model has neither companion
    [InlineData("l1a2_g.ddm")]  // wrong extension
    [InlineData("_g.psx")]      // too short for a _g stem, and no siblings
    public void Companions_RejectNonLevelInputs(string fileName)
    {
        Assert.False(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource(), fileName, out _));
    }

    [Fact]
    public void Companions_SpiderManAndThpsEnableTheTriggerOverlay()
    {
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource(), "l1a2_g.psx", out var spiderMan));
        Assert.True(spiderMan.ApplyTriggerOverlay);

        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource("skware_o.psx", "skware_t.trg"),
            "skware.psx", out var thps));
        Assert.True(thps.ApplyTriggerOverlay);
    }

    [Fact]
    public void Companions_ApocalypseBarePrimary_AttachesLegacyObjBankWithoutOverlay()
    {
        // death.psx is the bare primary; its bank has no separator (deathobj.psx).
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource("deathobj.psx", "death_t.trg"),
            "death.psx",
            out var companions));
        Assert.Equal("death", companions.LevelStem);
        Assert.Equal("deathobj.psx", companions.BankCompanionName);
        Assert.False(companions.ApplyTriggerOverlay);
    }

    [Theory]
    [InlineData("city_1.psx", "city")]  // numeric first chunk
    [InlineData("sewr_1a.psx", "sewr")] // lettered first chunk
    public void Companions_ApocalypseFirstChunkPrimary_AttachesSharedObjBank(
        string fileName,
        string expectedStem)
    {
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource(expectedStem + "_obj.psx", expectedStem + "_t.trg"),
            fileName,
            out var companions));
        Assert.Equal(expectedStem, companions.LevelStem);
        Assert.Equal(expectedStem + "_obj.psx", companions.BankCompanionName);
        Assert.False(companions.ApplyTriggerOverlay);
    }

    [Fact]
    public void Companions_ApocalypseNonPrimaryChunk_IsRejected()
    {
        // A later chunk must not re-place the shared bank.
        Assert.False(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource("city_obj.psx", "city_t.trg"),
            "city_2.psx",
            out _));
    }

    [Fact]
    public void Companions_ApocalypseFirstChunk_DefersToBarePrimary()
    {
        // When the bare <base>.psx exists it owns the attach, so <base>_1.psx must
        // not double-place the bank.
        Assert.False(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource("deathobj.psx", "death_t.trg", "death.psx"),
            "death_1.psx",
            out _));
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

        // The bank's own instance (at its stored position) plus every
        // non-coincident platform node instance.
        var placements = Assert.Single(resolved).Value;
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, 3, 7, 11],
            placements.Select(static placement => placement.TriggerNodeIndex));
        Assert.Equal(0f, placements[0].Transform.Translation.X, 5);
        Assert.Equal(40f, placements[1].Transform.Translation.X, 5);
        Assert.Equal(80f, placements[2].Transform.Translation.X, 5);
        Assert.Equal(120f, placements[3].Transform.Translation.X, 5);
    }

    [Fact]
    public void Resolve_WithoutTriggerData_PlacesEveryBankObjectAtItsStoredPosition()
    {
        // Bank object raws are 20.12 fixed-point: 900×4096 raw at translation
        // divisor 2.25 = world 400.
        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            (TrgFile?)null,
            BuildObjectBank(objectRawX: 900 * 4096));

        var placement = Assert.Single(Assert.Single(resolved).Value);
        Assert.Equal(PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, placement.TriggerNodeIndex);
        Assert.Equal(400f, placement.Transform.Translation.X, 5);
    }

    [Fact]
    public void Resolve_PlatformNodeCoincidingWithBankInstance_ReplacesItInsteadOfDoubling()
    {
        // Node raw 905 (TRG raws carry no 4096 factor) = world 402.2, within
        // the coincidence tolerance of the bank object's world 400 → the node
        // IS the bank's own instance, and its placement (which can carry an
        // authored rotation) supersedes the bank's translation-only one.
        var trg = BuildTriggerFile(BuildPlatformNode(index: 3, rawX: 905, active: true));

        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            trg,
            BuildObjectBank(objectRawX: 900 * 4096));

        var placement = Assert.Single(Assert.Single(resolved).Value);
        Assert.Equal(3, placement.TriggerNodeIndex);
        Assert.Equal(402.222f, placement.Transform.Translation.X, 3);
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

        var placements = Assert.Single(resolved).Value;
        Assert.Contains(placements, static placement => placement.TriggerNodeIndex == 4);
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

        var placements = Assert.Single(resolved).Value;
        Assert.Contains(placements, static placement => placement.TriggerNodeIndex == 5);
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

        var placements = Assert.Single(resolved).Value;
        Assert.Contains(placements, static placement => placement.TriggerNodeIndex == 8);
    }

    [Fact]
    public void Resolve_PreservesNativeYxzRotationAcrossTheGltfBasisChange()
    {
        var node = BuildPlatformNode(index: 6, rawX: 90, active: true);
        node.Angles = new TrgAngles
        {
            RawX = 1024,
            RawY = 1024,
            RawZ = 0
        };

        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            BuildTriggerFile(node),
            BuildObjectBank());

        var transform = Assert.Single(resolved).Value
            .Single(static placement => placement.TriggerNodeIndex == 6)
            .Transform;
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
    public void Resolve_TriggerReadFailureStillPlacesTheBankLayer()
    {
        var resolved = PsxLevelObjectPlacementResolver.Resolve(
            new ThrowingCompanionSource(),
            "test_g.psx",
            BuildObjectBank());

        var placement = Assert.Single(Assert.Single(resolved).Value);
        Assert.Equal(PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, placement.TriggerNodeIndex);
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

    private static PsxMeshFile BuildObjectBank(int objectRawX = 0)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = [new PsxMeshObject { MeshIndex = 0, RawX = objectRawX }],
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

    private sealed class CompanionAvailabilitySource(params string[] companionNames) : AssetSource
    {
        public override string DisplayName => "synthetic";
        public override string EntryName => "l1a2_g.psx";

        public override byte[] ReadBytes() => [];

        public override bool CompanionExists(string nameWithExtension) =>
            companionNames.Any(name =>
                string.Equals(name, nameWithExtension, StringComparison.OrdinalIgnoreCase));

        public override byte[]? TryReadCompanion(string nameWithExtension) => null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
