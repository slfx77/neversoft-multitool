using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins the POWERUP placement layer: Spider-Man TRG POWERUP nodes place
///     items.psx models keyed by pickupType through the per-build table, at the
///     node's world position (translation-only), with the right table selected
///     by the sibling items.psx's mesh set.
/// </summary>
public sealed class PsxPowerupPlacementResolverTests(TestPaths paths)
{
    private const uint WebCartridge = 0x17646B0D; // model 1, pickupType 8
    private const uint QuestionMark = 0x7F648179; // model 5, pickupType 11
    private const uint PanelModel6 = 0x12820A41; // model 6, April-29/final type 12
    private const uint PanelModel7 = 0xA092D785; // model 7, final types 10/18
    private const uint ThpsLetterS = 0x311D55D4; // THPS items 'S' letter, pickupType 5
    private const uint ThpsLetterK = 0x2328A71C; // THPS items 'K' letter, pickupType 4
    private const uint ApocMarker = 0x350E968C; // Apocalypse items, pickupType 4
    private const uint ApocType5 = 0xD40B155E; // Apocalypse items, pickupType 5
    private const float Divisor = 2.25f;

    [Fact]
    public void Resolve_PlacesMappedPickupAtNodeWorldPosition()
    {
        // items with cartridge at object index 1.
        var items = BuildItems([0xB08EC1FB, WebCartridge]);
        var trg = BuildTrg(true,
            Powerup(7, 8, 900, 450, -1800));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        var entry = Assert.Single(placements);
        Assert.Equal(1, entry.Key); // cartridge items object index
        var placement = Assert.Single(entry.Value);
        Assert.Equal(7, placement.TriggerNodeIndex);
        // world = raw / divisor, then glTF flips Y and Z.
        Assert.Equal(new Vector3(900f / Divisor, -450f / Divisor, 1800f / Divisor),
            placement.Transform.Translation);
    }

    [Fact]
    public void Resolve_SkipsUnmappedAndUnresolvableTypes()
    {
        var items = BuildItems([WebCartridge]); // only cartridge present
        var trg = BuildTrg(true,
            Powerup(1, 99, 1, 2, 3), // not in any table
            Powerup(2, 11, 4, 5, 6)); // "?" not in this items.psx

        Assert.Null(PsxPowerupPlacementResolver.Resolve(trg, items, Divisor));
    }

    [Fact]
    public void Resolve_ReturnsNullForNullTrgOrUnrecognizedItems()
    {
        Assert.Null(PsxPowerupPlacementResolver.Resolve(null, BuildItems([WebCartridge]), Divisor));

        // items.psx whose mesh set matches no known table (Apocalypse / unknown)
        // resolves to no pickup layer, regardless of TRG version.
        var unknownItems = BuildItems([0xDEADBEEF, 0x12345678]);
        var trg = BuildTrg(false, Powerup(1, 8, 1, 2, 3));
        Assert.Null(PsxPowerupPlacementResolver.Resolve(trg, unknownItems, Divisor));
    }

    [Fact]
    public void Resolve_PlacesThpsSkateLettersFromV20Trg()
    {
        // THPS items.psx (the 'S' letter marks it) → THPS table; a v2.0 TRG's
        // SKATE-letter POWERUP nodes place from the items copy, translation-only.
        var items = BuildItems([ThpsLetterS, ThpsLetterK]);
        var trg = BuildTrg(false,
            Powerup(1, 5, 900, 0, 0), // 'S'
            Powerup(2, 4, 0, 0, 900)); // 'K'

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        Assert.Equal(2, placements!.Count);
        var sPlacement = Assert.Single(placements[0]);
        Assert.Equal(new Vector3(900f / Divisor, 0f, 0f), sPlacement.Transform.Translation);
    }

    [Fact]
    public void Resolve_ThpsPickupsKeepAuthoredHeightEvenWithTerrain()
    {
        // The matched THPS2 decomp's CPowerUp::DoPhysics writes
        // mOrgPos.vy = mGroundY - hover only on the mDropping path — placed
        // pickups render at their authored TRG Y. THPS items models are
        // origin-CENTERED, so the old origin-on-floor snap half-buried every
        // SKATE letter (THPS2-DC user report, fixed 2026-07-28).
        var terrain = PsxTerrainHeightField.Build([Floor(500f)]);
        var items = BuildItems([ThpsLetterS, ThpsLetterK]);
        var trg = BuildTrg(false, Powerup(1, 5, 900, 0, 0));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor, terrain);

        Assert.NotNull(placements);
        var placement = Assert.Single(Assert.Single(placements!).Value);
        Assert.Equal(0f, placement.Transform.Translation.Y, 3); // authored Y, no snap
    }

    [Fact]
    public void Resolve_SpiderManPickupsStillSnapToHoverAboveTerrain()
    {
        // Spider-Man's CPowerUp rests the origin mHoverHeight (128 world
        // units) above the resting floor — engine-exact, unchanged by the
        // THPS no-snap fix.
        var terrain = PsxTerrainHeightField.Build([Floor(500f)]);
        var items = BuildItems([0xB08EC1FB, QuestionMark]);
        var trg = BuildTrg(true, Powerup(1, 11, 900, 0, 0));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor, terrain);

        Assert.NotNull(placements);
        var placement = Assert.Single(Assert.Single(placements!).Value);
        // Native (+Y down): origin snaps to groundY − 128/divisor; glTF flips Y.
        Assert.Equal(-(500f - 128f / Divisor), placement.Transform.Translation.Y, 2);
    }

    // Native +Y = DOWN; this winding yields a floor-facing (ny < 0) triangle
    // large enough to sit under the test pickups' XZ footprints.
    private static (Vector3, Vector3, Vector3) Floor(float y) =>
        (new Vector3(0, y, 0), new Vector3(1000, y, 0), new Vector3(0, y, 1000));

    [Fact]
    public void Resolve_PlacesApocalypsePickupsAndSkipsPlusOneRegion()
    {
        // Apocalypse items.psx (marked by its pickupType-4 model) → Apocalypse
        // table. Type 17 spools the "plus_one" region, not items, so it is skipped.
        var items = BuildItems([ApocMarker, ApocType5]);
        var trg = BuildTrg(false,
            Powerup(1, 4, 900, 0, 0),
            Powerup(2, 5, 0, 0, 900),
            Powerup(3, 17, 0, 450, 0));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        Assert.Equal(2, placements!.Count); // types 4 and 5 placed; 17 skipped
    }

    [Fact]
    public void Resolve_SelectsFinalTableWhenItemsHasModel7()
    {
        // Final items.psx carries model 7 → type 18 resolves to it.
        var items = BuildItems([0xB08EC1FB, WebCartridge, PanelModel6, PanelModel7]);
        var trg = BuildTrg(true, Powerup(1, 18, 100, 0, 0));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        var entry = Assert.Single(placements);
        Assert.Equal(PanelModel7, items.MeshNameHashes[items.Objects[entry.Key].MeshIndex]);
    }

    [Fact]
    public void Resolve_SelectsProtoTableWhenItemsLacksPanels()
    {
        // Feb proto items.psx (no m6/m7): type 12 has no case → skipped; type 8 resolves.
        var items = BuildItems([0xB08EC1FB, WebCartridge, QuestionMark]);
        var trg = BuildTrg(true,
            Powerup(1, 12, 1, 2, 3), // proto: default (no model)
            Powerup(2, 8, 4, 5, 6));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        var entry = Assert.Single(placements); // only type 8 placed
        Assert.Equal(WebCartridge, items.MeshNameHashes[items.Objects[entry.Key].MeshIndex]);
    }

    [Theory]
    [InlineData("Spider-Man (2000-2-18, PSX - Prototype)")]
    [InlineData("Spider-Man (2000-4-29, PSX - Prototype)")]
    [InlineData("Spider-Man (2000-6-12, PSX - Prototype)")]
    [InlineData("Spider-Man (2000-9-1, PSX - Final)")]
    public void Resolve_ProtoL1a1_PlacesEveryMappedPowerupNode(string buildName)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var cd = Path.Combine(paths.SampleBuildsDir!, buildName, "CD");
        var geometryPath = Path.Combine(cd, "l1a1_g.psx");
        Assert.SkipWhen(!File.Exists(geometryPath), "l1a1_g.psx not present");

        var source = new FileSystemAssetSource(geometryPath);
        var items = PsxItemsBankSubstitution.TryLoadItems(source);
        Assert.NotNull(items);
        var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(source, "l1a1");
        Assert.NotNull(trg);

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items!.File, Divisor);
        Assert.NotNull(placements);

        // Placement count == count of POWERUP nodes whose pickupType resolves to
        // a mesh present in this build's items.psx.
        var placed = placements.Values.Sum(list => list.Count);
        var expected = trg!.Nodes.Count(node =>
            node.TypeId == TrgNodeMetadata.TypePowerup && node.PickupType is { } t
                                                       && PsxPowerupPlacementResolver.Resolve(
                                                           BuildTrg(true, Powerup(0, t, 0, 0, 0)), items.File,
                                                           Divisor) != null);
        Assert.Equal(expected, placed);
        Assert.True(placed >= 3, $"expected at least the three '?' markers, got {placed}");
    }

    private static TrgNode Powerup(int index, int pickupType, int rawX, int rawY, int rawZ)
    {
        return new TrgNode
        {
            Index = index,
            TypeId = TrgNodeMetadata.TypePowerup,
            PickupType = pickupType,
            Position = new TrgPosition { RawX = rawX, RawY = rawY, RawZ = rawZ }
        };
    }

    // Secret-tape candidates: THPS2 Videotape_01, THPS1-final Itm_Tape01,
    // THPS1-proto grey-gear placeholder (thps1_final.exe @0x8001FC68,
    // thps1_proto.exe jump table case 16 @0x8001733C).
    private const uint Thps2Tape = 0x7C1B2C4A;
    private const uint Thps1Tape = 0xD5108E8C;
    private const uint ProtoPlaceholder = 0x7E74F3D4;

    [Fact]
    public void Resolve_Thps1FinalTape_FallsThroughToItmTape01()
    {
        // THPS1 final's items.psx has NO Videotape_01 — its tape is Itm_Tape01.
        // The single-hash table silently skipped every type-16 node (the
        // reported missing skmall secret tape); candidates scan in order.
        var items = BuildItems([ThpsLetterS, Thps1Tape]);
        var trg = BuildTrg(false, Powerup(3, 16, 450, 225, -900));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        var entry = Assert.Single(placements!);
        Assert.Equal(1, entry.Key); // Itm_Tape01's items object index
        Assert.Equal(3, Assert.Single(entry.Value).TriggerNodeIndex);
    }

    [Fact]
    public void Resolve_Thps2Tape_StillPrefersVideotape01()
    {
        // First-present ordering must keep THPS2 byte-identical: when both
        // names exist, Videotape_01 wins.
        var items = BuildItems([ThpsLetterS, Thps1Tape, Thps2Tape]);
        var trg = BuildTrg(false, Powerup(4, 16, 0, 0, 0));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        Assert.Equal(2, Assert.Single(placements!).Key); // Videotape_01's index
    }

    [Fact]
    public void Resolve_Thps1ProtoBonus_CollapsesOntoThePlaceholderMesh()
    {
        // The 1999-4-9 proto maps bonus types 21/22/23 onto one mesh
        // (0x91EEBD52); the distinct final models are absent from its items.
        var items = BuildItems([ThpsLetterS, 0x91EEBD52]);
        var trg = BuildTrg(false,
            Powerup(5, 21, 0, 0, 0),
            Powerup(6, 22, 90, 0, 0));

        var placements = PsxPowerupPlacementResolver.Resolve(trg, items, Divisor);

        Assert.NotNull(placements);
        var entry = Assert.Single(placements!);
        Assert.Equal(1, entry.Key);
        Assert.Equal(2, entry.Value.Count); // both types collapse onto it
    }

    private static TrgFile BuildTrg(bool spiderMan, params TrgNode[] nodes)
    {
        return new TrgFile
        {
            VersionMajor = 2,
            VersionMinor = spiderMan ? 1 : 0,
            Nodes = [.. nodes]
        };
    }

    private static PsxMeshFile BuildItems(uint[] meshHashes)
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