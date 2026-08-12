using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins the items.psx bank substitution: bank meshes sharing a name hash
///     with an items model render from the items copy (the engine binds
///     pickups/markers to the spooled "items" region — Spidey_CIcon draws the
///     in-world "?" as items model 5), unless a POWERUP node already places that
///     mesh (then the bank duplicate is suppressed). Unshared bank objects keep
///     their own placements.
/// </summary>
public sealed class PsxItemsBankSubstitutionTests(TestPaths paths)
{
    private const uint SharedHash = 0x7F648179; // the "?" marker
    private const uint BankOnlyHash = 0x11111111;

    [Fact]
    public void Split_RedirectsSharedHashesAndKeepsTheRest()
    {
        var bank = BuildFile([SharedHash, BankOnlyHash]);
        var items = BuildFile([0xAAAAAAAA, SharedHash]);
        var bankPlacements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(-1, Matrix4x4.CreateTranslation(1f, 2f, 3f))],
            [1] = [new PsxLevelObjectPlacement(-1, Matrix4x4.Identity)]
        };

        var split = PsxItemsBankSubstitution.Split(items, bank, bankPlacements);

        Assert.NotNull(split);
        var (itemsPlacements, remaining) = split.Value;
        var itemsEntry = Assert.Single(itemsPlacements);
        Assert.Equal(1, itemsEntry.Key); // items object index of the shared mesh
        Assert.Equal(new Vector3(1f, 2f, 3f), Assert.Single(itemsEntry.Value).Transform.Translation);
        var remainingEntry = Assert.Single(remaining);
        Assert.Equal(1, remainingEntry.Key); // the bank-only object stays a bank placement
    }

    [Fact]
    public void Split_ReturnsNullWithoutSharedHashes()
    {
        var bank = BuildFile([BankOnlyHash]);
        var items = BuildFile([0xAAAAAAAA]);
        var bankPlacements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(-1, Matrix4x4.Identity)]
        };

        Assert.Null(PsxItemsBankSubstitution.Split(items, bank, bankPlacements));
    }

    [Fact]
    public void Split_SuppressesHashesThePowerupLayerPlaces()
    {
        var bank = BuildFile([SharedHash, BankOnlyHash]);
        var items = BuildFile([0xAAAAAAAA, SharedHash]);
        var bankPlacements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(-1, Matrix4x4.Identity)],
            [1] = [new PsxLevelObjectPlacement(-1, Matrix4x4.Identity)]
        };

        // POWERUP already places the "?" → the bank duplicate is dropped, not
        // redirected. The non-pickup bank object still stays a bank placement.
        var split = PsxItemsBankSubstitution.Split(
            items, bank, bankPlacements, new HashSet<uint> { SharedHash });

        Assert.NotNull(split);
        var (itemsPlacements, remaining) = split.Value;
        Assert.Empty(itemsPlacements);
        Assert.Equal([1], remaining.Keys);
    }

    [CorpusFact]
    public void Split_ProtoL1a1QuestionMark_RedirectsWhenUnsuppressed_DropsWhenSuppressed()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var buildDir = Path.Combine(
            paths.SampleBuildsDir!, "Spider-Man (2000-2-18, PSX - Prototype)", "CD");
        var geometryPath = Path.Combine(buildDir, "l1a1_g.psx");
        Assert.SkipWhen(!File.Exists(geometryPath), "l1a1_g.psx not present");

        var source = new FileSystemAssetSource(geometryPath);
        var items = PsxItemsBankSubstitution.TryLoadItems(source);
        Assert.NotNull(items);
        var bankBytes = source.TryReadCompanion("l1a1_o.psx");
        Assert.NotNull(bankBytes);
        var bank = PsxMeshFile.Parse(bankBytes!, false);
        Assert.NotNull(bank);

        var placements = PsxLevelObjectPlacementResolver.Resolve(source, "l1a1_g.psx", bank!);

        // Unsuppressed: the bank "?" (object 4) redirects to items mesh 5.
        var redirected = PsxItemsBankSubstitution.Split(items!.File, bank!, placements);
        Assert.NotNull(redirected);
        var itemsObjectIndex = Assert.Single(redirected.Value.ItemsPlacements).Key;
        Assert.Equal(5, items.File.Objects[itemsObjectIndex].MeshIndex);
        Assert.DoesNotContain(4, redirected.Value.RemainingBankPlacements.Keys);

        // Suppressed (POWERUP layer owns the "?"): the bank "?" is dropped.
        var suppressed = PsxItemsBankSubstitution.Split(
            items.File, bank!, placements, new HashSet<uint> { SharedHash });
        Assert.NotNull(suppressed);
        Assert.Empty(suppressed.Value.ItemsPlacements);
        Assert.DoesNotContain(4, suppressed.Value.RemainingBankPlacements.Keys);
    }

    private static PsxMeshFile BuildFile(uint[] meshHashes)
    {
        return new PsxMeshFile
        {
            Version = 0x04,
            Objects = [.. meshHashes.Select((_, i) => new PsxMeshObject { MeshIndex = (ushort)i })],
            Meshes =
            [
                .. meshHashes.Select(_ => new PsxMesh
                {
                    Vertices = [],
                    Normals = [],
                    Faces = []
                })
            ],
            MeshNameHashes = meshHashes,
            TextureHashes = []
        };
    }
}
