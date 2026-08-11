using System.Globalization;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins "What If?" content gating: PLATFORM nodes whose scripts run
///     C_IF_WHAT_IF before their V_MODEL_CHECKSUM (the final l2a1's rooftop
///     motorcycle et al.) spawn only when the easter-egg mode is active, so
///     their placements are removed by default behind an opt-in visibility
///     group.
/// </summary>
public sealed class PsxWhatIfContentGateTests(TestPaths paths)
{
    private const string SpiderManFinalBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string EnterElectroFinalBuild =
        "Spider-Man 2 - Enter Electro (2001-8-15, PSX - Final)";
    private const uint ModelHash = 0x12345678;
    private const uint WhatIfModelHash = 0x0F0F0F0F;
    private const uint AssetHash = 0xCAFEF00D;
    private const string GroupId = "psx.whatif.CAFEF00D";

    public static TheoryData<string, int[], int, int, uint> SpiderManDisplayGateCases => new()
    {
        { "l1a3", [322], 0, 0, 0xA9933E06u },
        { "l5a3", [192, 196, 198], 6, 6, 0xA2907FFCu }
    };

    public static TheoryData<string, int[], int, int, uint> EnterElectroElseBranchCases => new()
    {
        { "e1m2", [316], 14, 14, 0x88A65242u },
        { "e3m3", [3, 306], 13, 13, 0x88A65242u }
    };

    [Theory]
    [MemberData(nameof(SpiderManDisplayGateCases))]
    public void FinalSpiderMan_DisplayOffWhatIfNodes_GateTheirExactPlacements(
        string levelStem,
        int[] expectedNodeIndices,
        int expectedObjectIndex,
        int expectedMeshIndex,
        uint expectedModelHash)
    {
        var (trg, objectBank) = LoadCorpusLevel(SpiderManFinalBuild, levelStem);
        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, objectBank);

        Assert.All(expectedNodeIndices, nodeIndex =>
        {
            Assert.Contains(nodeIndex, resolved.WhatIfNodeIndices);
            var node = Assert.Single(trg.Nodes, candidate => candidate.Index == nodeIndex);
            Assert.Contains(node.Script!, static op => op.Opcode == "0x212F");
            Assert.Contains(node.Script!, static op => op.Opcode == "0x4204");
            Assert.Contains(node.Script!, static op => op.Opcode == "0x4117");
            Assert.Contains(node.Script!, static op => op.Opcode == "0x4203");
        });

        var placedObjects = expectedNodeIndices.ToDictionary(
            static nodeIndex => nodeIndex,
            nodeIndex => FindPlacedObjectIndex(resolved.Placements, nodeIndex));
        var modelIdentities = placedObjects.ToDictionary(
            static pair => pair.Key,
            pair => GetModelIdentity(objectBank, pair.Value));
        Assert.All(placedObjects, pair => Assert.Equal(expectedObjectIndex, pair.Value));
        Assert.All(modelIdentities, pair =>
            Assert.Equal((expectedMeshIndex, expectedModelHash), pair.Value));

        var hiddenDocument = new ModelDocument { Name = levelStem + "_g" };
        var hidden = PsxWhatIfContentGate.Apply(
            hiddenDocument, null, AssetHash, resolved);
        Assert.All(placedObjects, pair =>
            Assert.DoesNotContain(
                hidden.GetValueOrDefault(pair.Value, []),
                placement => placement.TriggerNodeIndex == pair.Key));

        var visibleDocument = new ModelDocument { Name = levelStem + "_g" };
        var visible = PsxWhatIfContentGate.Apply(
            visibleDocument,
            new Dictionary<string, bool> { [GroupId] = true },
            AssetHash,
            resolved);
        Assert.All(placedObjects, pair =>
        {
            Assert.Contains(
                visible[pair.Value],
                placement => placement.TriggerNodeIndex == pair.Key);
            Assert.Equal(modelIdentities[pair.Key], GetModelIdentity(objectBank, pair.Value));
        });
        var group = Assert.Single(hiddenDocument.VisibilityGroups);
        Assert.All(expectedNodeIndices, nodeIndex =>
            Assert.Contains(
                nodeIndex.ToString(CultureInfo.InvariantCulture),
                group.SourceReference,
                StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EnterElectroElseBranchCases))]
    public void EnterElectro_ElseBranchNodes_KeepTheirNormalPlayPlacements(
        string levelStem,
        int[] expectedNodeIndices,
        int expectedObjectIndex,
        int expectedMeshIndex,
        uint expectedModelHash)
    {
        var (trg, objectBank) = LoadCorpusLevel(EnterElectroFinalBuild, levelStem);
        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, objectBank);
        var placedObjects = expectedNodeIndices.ToDictionary(
            static nodeIndex => nodeIndex,
            nodeIndex => FindPlacedObjectIndex(resolved.Placements, nodeIndex));
        Assert.All(placedObjects, pair => Assert.Equal(expectedObjectIndex, pair.Value));
        Assert.Equal(
            (expectedMeshIndex, expectedModelHash),
            GetModelIdentity(objectBank, expectedObjectIndex));

        Assert.All(expectedNodeIndices, nodeIndex =>
        {
            var node = Assert.Single(trg.Nodes, candidate => candidate.Index == nodeIndex);
            Assert.Contains(node.Script!, static op => op.Opcode == "0x4117");
            Assert.Contains(node.Script!, static op => op.Opcode == "0x4122");
            Assert.True(node.Script!
                .Where(static op => op.Opcode == "0x212F")
                .Select(static op => op.Value)
                .Distinct()
                .Count() >= 2);
            Assert.DoesNotContain(nodeIndex, resolved.WhatIfNodeIndices);
        });

        var document = new ModelDocument { Name = levelStem + "_g" };
        var normalPlay = PsxWhatIfContentGate.Apply(document, null, AssetHash, resolved);
        Assert.All(placedObjects, pair =>
        {
            Assert.Contains(
                normalPlay[pair.Value],
                placement => placement.TriggerNodeIndex == pair.Key);
            Assert.Equal(
                (expectedMeshIndex, expectedModelHash),
                GetModelIdentity(objectBank, pair.Value));
        });
    }

    [Fact]
    public void ResolveDetailed_DetectsWhatIfGatedPlatformNodes()
    {
        var trg = BuildTriggerFile(
            BuildPlatformNode(3, 90),
            BuildWhatIfNode(8, 500));

        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());

        Assert.Equal([8], resolved.WhatIfNodeIndices);
        // Resolution itself keeps every scripted instance (bank + both nodes).
        var placements = Assert.Single(resolved.Placements).Value;
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, 3, 8],
            placements.Select(static placement => placement.TriggerNodeIndex));
    }

    [Fact]
    public void ResolveDetailed_PlainPlatformNodesAreNotGated()
    {
        var trg = BuildTriggerFile(BuildPlatformNode(3, 90));

        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());

        Assert.Empty(resolved.WhatIfNodeIndices);
    }

    [Fact]
    public void ResolveDetailed_UnconditionalChecksumThenWhatIfOverride_IsNotGated()
    {
        // Final l6a2 nodes 85-88: the placed model is assigned at depth 0 and
        // the What If block merely OVERRIDES it — the prop is authored, so it
        // must not gate (2026-07-29; bare 0x4117+0x212F co-occurrence used to
        // hide these).
        var node = BuildPlatformNode(8, 500);
        node.Script =
        [
            ModelChecksum(),
            Op("0x4117", "C_IF_WHAT_IF"),
            ModelChecksum(WhatIfModelHash),
            Op("0x4120", "C_ENDIF")
        ];
        var trg = BuildTriggerFile(node);

        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());

        Assert.Empty(resolved.WhatIfNodeIndices);
        var placements = Assert.Single(resolved.Placements).Value;
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, 8],
            placements.Select(static placement => placement.TriggerNodeIndex));
    }

    [Fact]
    public void ResolveDetailed_DisplayOffByDefaultWithWhatIfDisplayOn_IsGated()
    {
        // Final l1a3 node 322 / l5a3 192/196/198 / l8a5 29/50: the model is
        // unconditional but the script turns display off at depth 0 and only
        // the C_IF_WHAT_IF block turns it back on — invisible outside the
        // mode, so it stays gated (2026-07-29).
        var node = BuildPlatformNode(8, 500);
        node.Script =
        [
            ModelChecksum(),
            Op("0x4204", "C_DISPLAY_OFF"),
            Op("0x4117", "C_IF_WHAT_IF"),
            Op("0x4203", "C_DISPLAY_ON"),
            Op("0x4120", "C_ENDIF")
        ];
        var trg = BuildTriggerFile(node);

        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());

        Assert.Equal([8], resolved.WhatIfNodeIndices);
    }

    [Fact]
    public void ResolveDetailed_IfElseGrammar_PlacesElseBranchModelUngated()
    {
        // SM2EE e1m2 node 316 / e3m3 nodes 3+306: "0x4117 A 0x4120 0x4122 B
        // 0x4120" — the else-branch model B is what normal play shows, so the
        // node places B and is not What If content (2026-07-29).
        var node = BuildPlatformNode(8, 500);
        node.Script =
        [
            Op("0x4117", "C_IF_WHAT_IF"),
            ModelChecksum(WhatIfModelHash),
            Op("0x4120", "C_ENDIF"),
            Op("0x4122", "C_ELSE"),
            ModelChecksum(),
            Op("0x4120", "C_ENDIF")
        ];
        var trg = BuildTriggerFile(node);

        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(
            trg, BuildTwoModelObjectBank());

        Assert.Empty(resolved.WhatIfNodeIndices);
        // Object 0 = ModelHash (the else branch): bank instance + the node.
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, 8],
            resolved.Placements[0].Select(static placement => placement.TriggerNodeIndex));
        // Object 1 = the What If branch model: untouched bank instance only.
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex],
            resolved.Placements[1].Select(static placement => placement.TriggerNodeIndex));
    }

    [Fact]
    public void Apply_DefaultDisabled_RemovesWhatIfPlacementsAndRegistersGroup()
    {
        var trg = BuildTriggerFile(
            BuildPlatformNode(3, 90),
            BuildWhatIfNode(8, 500));
        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());
        var document = new ModelDocument { Name = "l2a1_g" };

        var gated = PsxWhatIfContentGate.Apply(document, null, AssetHash, resolved);

        var placements = Assert.Single(gated).Value;
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, 3],
            placements.Select(static placement => placement.TriggerNodeIndex));
        var group = Assert.Single(document.VisibilityGroups);
        Assert.Equal(GroupId, group.Id);
        Assert.Equal("\"What If?\" content", group.Label);
        Assert.False(group.DefaultEnabled);
        Assert.False(group.IsEnabled);
        Assert.Equal(ModelVisibilityGroupSource.TriggerCondition, group.Source);
        Assert.Contains("8", group.SourceReference, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_EnabledOverride_KeepsWhatIfPlacements()
    {
        var trg = BuildTriggerFile(
            BuildPlatformNode(3, 90),
            BuildWhatIfNode(8, 500));
        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());
        var document = new ModelDocument { Name = "l2a1_g" };

        var gated = PsxWhatIfContentGate.Apply(
            document,
            new Dictionary<string, bool> { [GroupId] = true },
            AssetHash,
            resolved);

        var placements = Assert.Single(gated).Value;
        Assert.Equal(
            [PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, 3, 8],
            placements.Select(static placement => placement.TriggerNodeIndex));
        var group = Assert.Single(document.VisibilityGroups);
        Assert.True(group.IsEnabled);
        Assert.False(group.DefaultEnabled);
    }

    [Fact]
    public void Apply_CoincidenceReplacedBankInstance_DisappearsEntirely()
    {
        // The What If node sits on the bank instance (raw 905 vs the bank's
        // world 400 within tolerance), so it REPLACED the bank placement and
        // carries the node index. Outside What If mode the game never shows
        // this object — gating removes it entirely.
        var trg = BuildTriggerFile(BuildWhatIfNode(8, 905));
        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(
            trg, BuildObjectBank(900 * 4096));
        var placement = Assert.Single(Assert.Single(resolved.Placements).Value);
        Assert.Equal(8, placement.TriggerNodeIndex);
        var document = new ModelDocument { Name = "l2a1_g" };

        var gated = PsxWhatIfContentGate.Apply(document, null, AssetHash, resolved);

        Assert.Empty(gated);
        Assert.Single(document.VisibilityGroups);
    }

    [Fact]
    public void Apply_WithoutWhatIfNodes_ReturnsPlacementsUnchangedAndAddsNoGroup()
    {
        var trg = BuildTriggerFile(BuildPlatformNode(3, 90));
        var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(trg, BuildObjectBank());
        var document = new ModelDocument { Name = "l2a1_g" };

        var gated = PsxWhatIfContentGate.Apply(document, null, AssetHash, resolved);

        Assert.Same(resolved.Placements, gated);
        Assert.Empty(document.VisibilityGroups);
    }

    private (TrgFile Trg, PsxMeshFile ObjectBank) LoadCorpusLevel(
        string buildName,
        string levelStem)
    {
        var wadPath = paths.FindSampleFile(buildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, $"{buildName} CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var geometryEntry = backend.FindEntry(levelStem + "_g.psx");
        Assert.NotNull(geometryEntry);
        var source = new ArchiveAssetSource(backend, geometryEntry!);

        var triggerBytes = source.TryReadCompanion(levelStem + "_t.trg");
        Assert.NotNull(triggerBytes);
        TrgFile trg;
        using (var stream = new MemoryStream(triggerBytes!, false))
        using (var reader = new BinaryReader(stream))
            trg = TrgFile.Parse(reader, levelStem + "_t.trg");

        var objectBytes = source.TryReadCompanion(levelStem + "_o.psx");
        Assert.NotNull(objectBytes);
        var objectBank = PsxMeshFile.Parse(objectBytes!);
        Assert.NotNull(objectBank);
        return (trg, objectBank!);
    }

    private static int FindPlacedObjectIndex(
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> placements,
        int nodeIndex)
    {
        return Assert.Single(placements
            .Where(pair => pair.Value.Any(placement =>
                placement.TriggerNodeIndex == nodeIndex))
            .Select(static pair => pair.Key));
    }

    private static (int MeshIndex, uint ModelHash) GetModelIdentity(
        PsxMeshFile objectBank,
        int objectIndex)
    {
        var meshIndex = objectBank.Objects[objectIndex].MeshIndex;
        return (meshIndex, objectBank.MeshNameHashes[meshIndex]);
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

    private static TrgNode BuildPlatformNode(int index, int rawX)
    {
        return new TrgNode
        {
            Index = index,
            TypeId = TrgNodeMetadata.TypeBaddy,
            Type = "BADDY",
            SubType = 0x192,
            BaddyFlags = [2, 5],
            Position = new TrgPosition { RawX = rawX },
            Angles = new TrgAngles(),
            Script = [ModelChecksum()]
        };
    }

    private static TrgNode BuildWhatIfNode(int index, int rawX)
    {
        var node = BuildPlatformNode(index, rawX);
        node.Script =
        [
            Op("0x4117", "C_IF_WHAT_IF"),
            ModelChecksum(),
            Op("0x4120", "C_ENDIF")
        ];
        return node;
    }

    private static TrgScriptOp Op(string opcode, string name)
    {
        return new TrgScriptOp { Opcode = opcode, Name = name };
    }

    private static TrgScriptOp ModelChecksum(uint hash = ModelHash)
    {
        return new TrgScriptOp
        {
            Opcode = "0x212F",
            Name = "V_MODEL_CHECKSUM",
            Value = $"0x{hash:X8}"
        };
    }

    private static PsxMeshFile BuildObjectBank(int objectRawX = 0)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = [new PsxMeshObject { MeshIndex = 0, RawX = objectRawX }],
            Meshes = [BuildEmptyMesh()],
            MeshNameHashes = [ModelHash],
            TextureHashes = [],
            ScaleDivisor = 36f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxMeshFile BuildTwoModelObjectBank()
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects =
            [
                new PsxMeshObject { MeshIndex = 0 },
                new PsxMeshObject { MeshIndex = 1 }
            ],
            Meshes = [BuildEmptyMesh(), BuildEmptyMesh()],
            MeshNameHashes = [ModelHash, WhatIfModelHash],
            TextureHashes = [],
            ScaleDivisor = 36f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxMesh BuildEmptyMesh()
    {
        return new PsxMesh
        {
            Vertices = [],
            Normals = [],
            Faces = []
        };
    }
}
