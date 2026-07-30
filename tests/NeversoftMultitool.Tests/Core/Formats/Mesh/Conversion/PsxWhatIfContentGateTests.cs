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
public sealed class PsxWhatIfContentGateTests
{
    private const uint ModelHash = 0x12345678;
    private const uint WhatIfModelHash = 0x0F0F0F0F;
    private const uint AssetHash = 0xCAFEF00D;
    private const string GroupId = "psx.whatif.CAFEF00D";

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
