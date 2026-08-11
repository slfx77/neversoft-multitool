using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxPlacedCoplanarOverlayResolverTests(
    TestPaths paths,
    ITestOutputHelper output)
{
    private const string Thps1Final = "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";
    private const uint SchoolOuterWall05 = 0x8D4A74FF;
    private const uint ObjGymDoor = 0xFA591A6B;

    [Fact]
    public void SchoolGymDoor_RecoversTheSoleCrossFileOraclePair()
    {
        var levelPath = paths.FindSampleFile(Thps1Final, "skschl.psx");
        var bankPath = paths.FindSampleFile(Thps1Final, "skschl_o.psx");
        Assert.SkipWhen(
            levelPath == null || bankPath == null,
            "THPS1 School level/object-bank fixtures not available");

        var level = PsxMeshFile.Parse(levelPath!);
        var bank = PsxMeshFile.Parse(bankPath!);
        Assert.NotNull(level);
        Assert.NotNull(bank);

        var levelObjectIndex = FindObjectByMeshHash(level!, SchoolOuterWall05);
        var bankObjectIndex = FindObjectByMeshHash(bank!, ObjGymDoor);
        Assert.Equal(200, levelObjectIndex);
        Assert.Equal(4, bankObjectIndex);

        var source = new FileSystemAssetSource(levelPath!);
        var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(source, "skschl");
        var placements = PsxLevelObjectPlacementResolver.Resolve(trg, bank!);
        var doorPlacement = Assert.Single(placements[bankObjectIndex]);
        // PLATFORM node 215 coincides with and replaces the bank home slot;
        // there is exactly one emitted gym-door instance in this level.
        Assert.Equal(215, doorPlacement.TriggerNodeIndex);

        var result = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            level!, bank!, placements);

        var pair = Assert.Single(result.AcceptedPairs);
        Assert.Equal(new PsxFaceInstanceKey(200, 0), pair.LevelFace);
        Assert.Equal(new PsxPlacedFaceInstanceKey(4, 0, 1), pair.BankFace);
        Assert.InRange(pair.SharedAreaFraction, 0.088f, 0.09f);
        Assert.Equal(0f, pair.PlaneDistanceDelta, 5);
        var assignment = Assert.Single(result.Assignments);
        Assert.Equal(pair.BankFace, assignment.Key);
        Assert.Equal(new PsxCoplanarOverlayAssignment(0, 1), assignment.Value);

        // The source bank alone cannot discover this face; the assembled parse
        // must split only its one placed instance and publish draw-order metadata.
        Assert.DoesNotContain(
            new PsxFaceInstanceKey(4, 1),
            PsxCoplanarOverlayDetector.Find(bank!));
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = Path.GetFileName(levelPath),
            OutputStem = "skschl",
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = true
        });
        var overlayNode = Assert.Single(document.Nodes, static node =>
            node.Name.StartsWith("objects_004__overlay", StringComparison.Ordinal));
        var metadata = Assert.Single(document.Meshes[overlayNode.MeshIndex!.Value]
            .Primitives.SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<MeshDrawOrderMetadata>());
        Assert.Equal((1, 1), (metadata.DrawIndex, metadata.PassIndex));
        Assert.DoesNotContain(document.Nodes, static node =>
            node.Name.StartsWith("objects_004_node_", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateRotatedPlacements_AreComparedOnceThenExpandedWithoutMarkingFarRepeat()
    {
        var level = CreateFile(CreateQuad(10f, 1));
        var bank = CreateFile(CreateQuad(2f, 2));
        var rotation = Matrix4x4.CreateRotationY(MathF.PI);
        var placements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] =
            [
                new PsxLevelObjectPlacement(10, rotation),
                new PsxLevelObjectPlacement(11, rotation),
                new PsxLevelObjectPlacement(
                    12,
                    rotation * Matrix4x4.CreateTranslation(0f, 2f, 0f))
            ]
        };

        var result = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            level, bank, placements);

        Assert.Equal(
            [
                new PsxPlacedFaceInstanceKey(0, 0, 0),
                new PsxPlacedFaceInstanceKey(0, 1, 0)
            ],
            result.Assignments.Keys.OrderBy(static key => key.PlacementIndex));
        Assert.DoesNotContain(
            new PsxPlacedFaceInstanceKey(0, 2, 0),
            result.Assignments.Keys);
        Assert.Equal(2, result.AcceptedPairs.Count);

        var document = new ModelDocument { Name = "placed-overlays" };
        PsxGeometryWriter.PopulatePsx(
            document,
            bank,
            textureProvider: null,
            objectPlacements: placements,
            placedCoplanarOverlays: result.Assignments);

        Assert.Contains(document.Nodes, static node =>
            node.Name == "object_000_node_010__overlay00");
        Assert.Contains(document.Nodes, static node =>
            node.Name == "object_000_node_011__overlay00");
        var far = Assert.Single(document.Nodes, static node =>
            node.Name == "object_000_node_012");
        Assert.DoesNotContain(document.Nodes, static node =>
            node.Name.StartsWith("object_000_node_012__overlay", StringComparison.Ordinal));

        foreach (var overlayNode in document.Nodes.Where(static node =>
                     node.Name.EndsWith("__overlay00", StringComparison.Ordinal)))
        {
            var metadata = Assert.Single(document.Meshes[overlayNode.MeshIndex!.Value]
                .Primitives.SelectMany(static primitive => primitive.NativeMetadata)
                .OfType<MeshDrawOrderMetadata>());
            Assert.Equal((1, 1, 0),
                (metadata.DrawIndex, metadata.PassIndex, metadata.OverlapGroup));
        }

        Assert.Empty(document.Meshes[far.MeshIndex!.Value]
            .Primitives.SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<MeshDrawOrderMetadata>());
    }

    [Fact]
    public void CrossFileExactTwin_UsesRenderedPaletteColoursRatherThanRawIndices()
    {
        var red = new Vector4(1f, 0f, 0f, 1f);
        var blue = new Vector4(0f, 0f, 1f, 1f);
        var level = CreateFile(CreateQuad(2f, 1, gouraudIndex: 0), [red]);
        var differentColourAtSameIndex = CreateFile(
            CreateQuad(2f, 1, gouraudIndex: 0),
            [blue]);
        var sameColourAtDifferentIndex = CreateFile(
            CreateQuad(2f, 1, gouraudIndex: 1),
            [blue, red]);
        var placements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(10, Matrix4x4.Identity)]
        };

        var different = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            level, differentColourAtSameIndex, placements);
        var detection = Assert.Single(different.DetectedPairs);
        Assert.False(detection.BankFaceSelected); // equal-size OT order selects the level
        Assert.Empty(different.Assignments); // the already-emitted level cannot be retrofitted

        var same = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            level, sameColourAtDifferentIndex, placements);
        Assert.Empty(same.DetectedPairs); // same rendered pixels: exact-twin decline
        Assert.Empty(same.Assignments);
    }

    [Fact]
    public void CrossFileExactTwin_PreservesColourPulseAndTextureWibbleIdentity()
    {
        var red = new Vector4(1f, 0f, 0f, 1f);
        var levelPulse = CreatePulse(
            new PsxColourPulseKey(255, 0, 0, 1),
            new PsxColourPulseKey(0, 255, 0, 1));
        var bankPulse = CreatePulse(
            new PsxColourPulseKey(255, 0, 0, 1),
            new PsxColourPulseKey(0, 0, 255, 1));
        var level = CreateFile(CreateQuad(2f, 1, gouraudIndex: 0), [red], [levelPulse]);
        var bank = CreateFile(CreateQuad(2f, 1, gouraudIndex: 0), [red], [bankPulse]);
        var placements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(10, Matrix4x4.Identity)]
        };

        var pulseResult = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            level, bank, placements);
        Assert.Single(pulseResult.DetectedPairs);

        var levelWibbleMesh = CreateQuad(2f, 1);
        var bankWibbleMesh = CreateQuad(2f, 1);
        levelWibbleMesh.Faces[0].ApplyTextureWibble(CreateWibble(uVelocity: 1));
        bankWibbleMesh.Faces[0].ApplyTextureWibble(CreateWibble(uVelocity: 2));

        var wibbleResult = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            CreateFile(levelWibbleMesh),
            CreateFile(bankWibbleMesh),
            placements);
        Assert.Single(wibbleResult.DetectedPairs);
    }

    [Fact]
    public void CrossFileExactTwin_UsesAuthoritativeTextureCoordinates()
    {
        var placements = new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        {
            [0] = [new PsxLevelObjectPlacement(10, Matrix4x4.Identity)]
        };
        var levelMesh = CreateQuad(2f, 1, legacyU0: 10);
        var sameUvMesh = CreateQuad(2f, 1, legacyU0: 20);
        levelMesh.Faces[0].TextureCoordinates[0] = new PsxTextureCoordinate(300, 4);
        sameUvMesh.Faces[0].TextureCoordinates[0] = new PsxTextureCoordinate(300, 4);

        var same = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            CreateFile(levelMesh),
            CreateFile(sameUvMesh),
            placements);
        Assert.Empty(same.DetectedPairs); // legacy placeholders are non-authoritative

        var differentUvMesh = CreateQuad(2f, 1, legacyU0: 10);
        differentUvMesh.Faces[0].TextureCoordinates[0] = new PsxTextureCoordinate(301, 4);
        var different = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            CreateFile(levelMesh),
            CreateFile(differentUvMesh),
            placements);
        Assert.Single(different.DetectedPairs); // widened emitted UVs differ
    }

    [Fact]
    public void Downhill_HighPlacementScope_RemainsBounded()
    {
        var levelPath = paths.FindSampleFile(Thps1Final, "skdown.psx");
        var bankPath = paths.FindSampleFile(Thps1Final, "skdown_o.psx");
        Assert.SkipWhen(
            levelPath == null || bankPath == null,
            "THPS1 Downhill level/object-bank fixtures not available");

        var level = PsxMeshFile.Parse(levelPath!);
        var bank = PsxMeshFile.Parse(bankPath!);
        Assert.NotNull(level);
        Assert.NotNull(bank);
        // The final School/Downhill-scale level is deliberately representative
        // of the large THPS object tables (the final Downhill fixture has 936).
        Assert.Equal(936, level!.Objects.Count);

        var source = new FileSystemAssetSource(levelPath!);
        var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(source, "skdown");
        var placements = PsxLevelObjectPlacementResolver.Resolve(trg, bank!);
        var emittedPlacementCount = placements.Values.Sum(static list => list.Count);

        var stopwatch = Stopwatch.StartNew();
        var result = PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
            level, bank!, placements);
        stopwatch.Stop();

        output.WriteLine(
            "Downhill assembled overlay scope: {0} level objects, {1} bank placements, "
            + "{2} accepted pairs, {3:F1} ms",
            level.Objects.Count,
            emittedPlacementCount,
            result.AcceptedPairs.Count,
            stopwatch.Elapsed.TotalMilliseconds);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Assembled overlay discovery took {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static int FindObjectByMeshHash(PsxMeshFile file, uint hash)
    {
        return Enumerable.Range(0, file.Objects.Count).Single(objectIndex =>
        {
            var meshIndex = file.Objects[objectIndex].MeshIndex;
            return meshIndex < file.MeshNameHashes.Length
                   && file.MeshNameHashes[meshIndex] == hash;
        });
    }

    private static PsxMeshFile CreateFile(
        PsxMesh mesh,
        Vector4[]? palette = null,
        IReadOnlyList<PsxColourPulse>? colourPulses = null)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes = [mesh],
            MeshNameHashes = [0],
            TextureHashes = [1, 2],
            GouraudPalette = palette,
            ColourPulses = colourPulses ?? [],
            ScaleDivisor = 2.25f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxColourPulse CreatePulse(params PsxColourPulseKey[] keys)
    {
        return new PsxColourPulse
        {
            ColourIndex = 0,
            InitialKeyIndex = 0,
            InitialTimeAccumulator = 0,
            Keys = keys
        };
    }

    private static PsxTextureWibble CreateWibble(short uVelocity)
    {
        return new PsxTextureWibble
        {
            UVelocity = uVelocity,
            VVelocity = 0,
            Frequency = 1,
            ZeroUAmplitudes = false,
            ZeroVAmplitudes = true,
            Vertices =
            [
                new PsxTextureWibbleVertex(0, 0, 0x10, 0),
                new PsxTextureWibbleVertex(0, 0, 0x10, 0),
                new PsxTextureWibbleVertex(0, 0, 0x10, 0),
                new PsxTextureWibbleVertex(0, 0, 0x10, 0)
            ]
        };
    }

    private static PsxMesh CreateQuad(
        float size,
        uint textureHash,
        byte? gouraudIndex = null,
        byte legacyU0 = 0)
    {
        var half = size * 0.5f;
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = -half, Z = -half },
                new PsxVertex { X = half, Z = -half },
                new PsxVertex { X = -half, Z = half },
                new PsxVertex { X = half, Z = half }
            ],
            Normals = [new PsxNormal { Y = 1f }],
            Faces =
            [
                new PsxFace
                {
                    IsQuad = true,
                    IsTextured = true,
                    IsGouraud = gouraudIndex.HasValue,
                    TextureHash = textureHash,
                    R = gouraudIndex ?? 0,
                    G = gouraudIndex ?? 0,
                    B = gouraudIndex ?? 0,
                    Mode = gouraudIndex ?? 0,
                    U0 = legacyU0,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    Index3 = 3
                }
            ]
        };
    }
}
