using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins ghost emission: an object whose mesh has ONLY loader-invisible
///     faces (the disc bit7-set opaque class) renders as the engine's
///     forced-blend apparition when a TRG entity node places it (l1a2's
///     Watcher head), behind a per-object visibility group that is DEFAULT-OFF
///     — the apparition is a transient runtime state (that node waits for a
///     collision and then dies quietly), so showing it by default misrepresents
///     the level. Non-placed invisible meshes (collision blockers, trigger
///     volumes) keep rendering nothing either way.
/// </summary>
public sealed class PsxGhostEmissionTests
{
    private const uint MeshHash = 0xD7833D12; // unresolved hash → fallback label
    private const uint AssetHash = 0xABCD1234;
    private const string GroupId = "psx.ghost.ABCD1234.000";

    [Fact]
    public void CreateForcedBlendFaces_FlipsOnSemiTransparencyAtTheAdditiveRate()
    {
        var mesh = BuildInvisibleMesh();

        var ghosts = PsxGhostFaces.CreateForcedBlendFaces(mesh);

        var ghost = Assert.Single(ghosts);
        var original = mesh.InvisibleFaces[0];
        Assert.Equal((ushort)(original.Flags | 0x0040), ghost.Flags);
        Assert.True(ghost.IsSemiTransparent);
        // Bit 7 is already set on the invisible class, so the forced blend
        // reads the additive ABR rate — the __st1 material family.
        Assert.Equal(1, ghost.BlendRate);
        Assert.Equal(original.Index0, ghost.Index0);
        Assert.Equal(original.Index1, ghost.Index1);
        Assert.Equal(original.Index2, ghost.Index2);
        Assert.Equal(original.NormalIndex, ghost.NormalIndex);
        Assert.Equal((original.R, original.G, original.B), (ghost.R, ghost.G, ghost.B));
        Assert.Equal(original.IsQuad, ghost.IsQuad);
    }

    [Fact]
    public void PlacedInvisibleMesh_RendersGhostFacesWhenTheGroupIsEnabled()
    {
        var document = new ModelDocument { Name = "test" };

        PsxGeometryWriter.PopulatePsx(
            document,
            BuildFile(),
            null,
            nodeNamePrefix: "objects",
            objectPlacements: NodePlacements(5),
            ghostOptions: BuildOptions(new Dictionary<string, bool> { [GroupId] = true }));

        var node = Assert.Single(document.Nodes);
        Assert.Equal("objects_000", node.Name);
        var mesh = Assert.Single(document.Meshes);
        Assert.EndsWith("__ghost", mesh.Name, StringComparison.Ordinal);
        var primitive = Assert.Single(mesh.Primitives);
        Assert.EndsWith("__st1", document.Materials[primitive.MaterialIndex].Name,
            StringComparison.Ordinal);
        Assert.Equal(1, document.TriangleCount);

        var group = Assert.Single(document.VisibilityGroups);
        Assert.Equal(GroupId, group.Id);
        Assert.Equal("objects_000 (hidden apparition)", group.Label);
        Assert.False(group.DefaultEnabled);
        Assert.True(group.IsEnabled);
        Assert.Equal(ModelVisibilityGroupSource.HiddenApparition, group.Source);
    }

    [Fact]
    public void PlacedInvisibleMesh_EmitsNothingByDefault()
    {
        var document = new ModelDocument { Name = "test" };

        PsxGeometryWriter.PopulatePsx(
            document,
            BuildFile(),
            null,
            nodeNamePrefix: "objects",
            objectPlacements: NodePlacements(5),
            ghostOptions: BuildOptions());

        // The group is still advertised so the apparition can be switched on;
        // it just contributes no geometry until it is.
        Assert.Empty(document.Nodes);
        var group = Assert.Single(document.VisibilityGroups);
        Assert.False(group.IsEnabled);
        Assert.False(group.DefaultEnabled);
    }

    [Fact]
    public void BankInstanceOnlyInvisibleMesh_RendersNothing()
    {
        // No TRG entity placement → the engine never forces the blend; the
        // bank's env copy stays invisible and no group is offered.
        var document = new ModelDocument { Name = "test" };

        PsxGeometryWriter.PopulatePsx(
            document,
            BuildFile(),
            null,
            nodeNamePrefix: "objects",
            objectPlacements: NodePlacements(
                PsxLevelObjectPlacementResolver.BankInstanceNodeIndex),
            ghostOptions: BuildOptions());

        Assert.Empty(document.Nodes);
        Assert.Empty(document.VisibilityGroups);
    }

    [Fact]
    public void NonPlacedInvisibleMesh_RendersNothing()
    {
        // The level-file path (no objectPlacements) must keep dropping
        // invisible meshes — THPS1 skdown collision blockers, skph trigger
        // volumes — even if ghost options were supplied.
        var document = new ModelDocument { Name = "test" };

        PsxGeometryWriter.PopulatePsx(
            document,
            BuildFile(),
            null,
            ghostOptions: BuildOptions());

        Assert.Empty(document.Nodes);
        Assert.Empty(document.VisibilityGroups);
    }

    [Fact]
    public void PlacedInvisibleMesh_WithoutGhostOptions_RendersNothing()
    {
        // The items pickup pass never opts in, so a fully invisible items
        // mesh keeps contributing nothing.
        var document = new ModelDocument { Name = "test" };

        PsxGeometryWriter.PopulatePsx(
            document,
            BuildFile(),
            null,
            nodeNamePrefix: "items",
            objectPlacements: NodePlacements(5));

        Assert.Empty(document.Nodes);
        Assert.Empty(document.VisibilityGroups);
    }

    private static PsxGhostEmissionOptions BuildOptions(
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        return new PsxGhostEmissionOptions
        {
            AssetHash = AssetHash,
            VisibilityOverrides = overrides
        };
    }

    private static Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> NodePlacements(
        int triggerNodeIndex)
    {
        return new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(triggerNodeIndex, Matrix4x4.Identity)]
        };
    }

    private static PsxMeshFile BuildFile()
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes = [BuildInvisibleMesh()],
            MeshNameHashes = [MeshHash],
            TextureHashes = [],
            ScaleDivisor = 36f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxMesh BuildInvisibleMesh()
    {
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                new PsxVertex { X = 10f, Y = 0f, Z = 0f },
                new PsxVertex { X = 0f, Y = 10f, Z = 0f }
            ],
            Normals = [new PsxNormal { X = 0f, Y = 0f, Z = 1f }],
            Faces = [],
            InvisibleFaces =
            [
                // Disc invisible class: bit7 set with bit6 clear (the loader's
                // STP toggle turns it dark); bit4 set = triangle.
                new PsxFace
                {
                    Flags = 0x0090,
                    IsQuad = false,
                    IsTextured = false,
                    IsGouraud = false,
                    IsSemiTransparent = false,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    NormalIndex = 0,
                    R = 40,
                    G = 40,
                    B = 200
                }
            ],
            VertexCount = 3
        };
    }
}
