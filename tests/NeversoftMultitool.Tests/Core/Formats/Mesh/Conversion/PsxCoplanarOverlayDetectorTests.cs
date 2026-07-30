using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxCoplanarOverlayDetectorTests(TestPaths paths)
{
    [Fact]
    public void Find_MarksSmallerOpaqueFaceNestedOnDifferentMaterial()
    {
        var file = CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0f, 2));

        var overlays = PsxCoplanarOverlayDetector.Find(file);

        Assert.DoesNotContain(new PsxFaceInstanceKey(0, 0), overlays);
        Assert.Contains(new PsxFaceInstanceKey(1, 0), overlays);
    }

    [Fact]
    public void Find_DoesNotMarkNearbyGeometry()
    {
        // A face on a parallel but distinct plane is not an overlay.
        var nearby = PsxCoplanarOverlayDetector.Find(
            CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0.1f, 2)));

        Assert.Empty(nearby);
    }

    [Fact]
    public void Find_MarksTheOrderingTableTopOfAnEqualSizedCoplanarPair()
    {
        // Baked light/shadow duplicates ship whole floor sections twice with
        // different textures on the identical plane (retail skmar: 57 pairs,
        // striped z-fighting). The PS1 resolves them by OT insertion order —
        // earliest face paints last, i.e. on top — so that face splits into
        // the draw-order overlay mesh.
        var equal = PsxCoplanarOverlayDetector.Find(
            CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(10f, 0f, 2)));
        var adjacent = PsxCoplanarOverlayDetector.Find(
            CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(10f, 0f, 2, offsetX: 10f)));

        Assert.Equal(new PsxFaceInstanceKey(0, 0), Assert.Single(equal));
        Assert.Empty(adjacent); // edge-adjacent tiling is not a stack
    }

    [Fact]
    public void SpiderManL2A1_FindsOpaqueRoofOverlayFaces()
    {
        var path = paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Spider-Man (2000-9-1, PSX - Final)",
                "CD",
                "l2a1_g.psx");
        Assert.SkipWhen(!File.Exists(path), "Spider-Man PSX final sample not available");

        var file = PsxMeshFile.Parse(path);
        Assert.NotNull(file);
        var overlays = PsxCoplanarOverlayDetector.Find(file!);

        // Re-pinned 2026-07-30: the near-equal branch now requires real SHARED
        // AREA (exact coplanar polygon clip, >=1% of the smaller face) on top
        // of AABB penetration. Diagonal faces that merely share an edge passed
        // the axis test: of the 296 same-texture pairs reaching the branch on
        // this file, 266 have zero polygon intersection, which is how the
        // AABB-only rule flagged 319 faces here (294-312 of them false).
        // 75 small-decal overlays + 7 genuine near-equal/same-texture stacks
        // remain; PsxAnalyzer overlay-census reports 82 with same-plane
        // interior-overlapping partners.
        Assert.Equal(82, overlays.Count);
        Assert.Contains(new PsxFaceInstanceKey(47, 0), overlays);
        Assert.Contains(new PsxFaceInstanceKey(47, 5), overlays);
        Assert.All(
            overlays.Where(key => key.ObjectIndex == 47),
            key => Assert.False(file!.Meshes[file.Objects[key.ObjectIndex].MeshIndex]
                .Faces[key.FaceIndex].IsSemiTransparent));
    }

    [Fact]
    public void FindSemiTransparentLayerSteps_LiftsTheSoleAnimatedLayerAboveTheStaticOne()
    {
        // The animated sheet is the LATER object, so the OT-order tie-break
        // alone would put it underneath — the sole-animated rule must win.
        var animated = CreateQuad(10f, 0f, 2, semiTransparent: true, wibble: true);
        var file = CreateFile(CreateQuad(10f, 0f, 1, semiTransparent: true), animated);

        var steps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(file);

        Assert.Equal(2, Assert.Single(steps).Value);
        Assert.Equal(new PsxFaceInstanceKey(1, 0), steps.Keys.Single());
    }

    [Fact]
    public void FindSemiTransparentLayerSteps_BreaksTiesByOrderingTableInsertion()
    {
        // No sole animated layer (none / both animated): the earliest-inserted
        // group paints last on the PS1 (bucket prepend) and so lifts on top.
        foreach (var wibble in new[] { false, true })
        {
            var steps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(
                CreateFile(
                    CreateQuad(10f, 0f, 1, semiTransparent: true, wibble: wibble),
                    CreateQuad(10f, 0f, 2, semiTransparent: true, wibble: wibble)));

            Assert.Equal(2, Assert.Single(steps).Value);
            Assert.Equal(new PsxFaceInstanceKey(0, 0), steps.Keys.Single());
        }
    }

    [Fact]
    public void FindSemiTransparentLayerSteps_IgnoresSingleAndNonOverlappingLayers()
    {
        var single = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(
            CreateFile(CreateQuad(10f, 0f, 1, semiTransparent: true, wibble: true)));
        var apart = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(
            CreateFile(
                CreateQuad(10f, 0f, 1, semiTransparent: true),
                CreateQuad(10f, 0f, 2, semiTransparent: true, offsetX: 100f)));

        Assert.Empty(single);
        Assert.Empty(apart);
    }

    [Fact]
    public void FindSemiTransparentLayerSteps_LeavesEdgeAdjacentPanelsAlone()
    {
        // Side-by-side panels sharing an edge on one plane (retail levels tile
        // walls with alternating sign textures) are independent layers, not a
        // stack — union-bounds grouping used to chain them into deep lifted
        // piles (lda1_g's 9-panel wall floated 2+ units).
        var steps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(
            CreateFile(
                CreateQuad(10f, 0f, 1, semiTransparent: true),
                CreateQuad(10f, 0f, 2, semiTransparent: true, offsetX: 10f),
                CreateQuad(10f, 0f, 1, semiTransparent: true, offsetX: 20f)));

        Assert.Empty(steps);
    }

    [Fact]
    public void FindSemiTransparentLayerSteps_LiftsOnlyTheOverlappingFacesOfTheTopLayer()
    {
        // The animated (top) texture has two faces: one on the static base,
        // one far away. Only the face actually sitting on the base lifts —
        // steps must never apply group-wide.
        var steps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(
            CreateFile(
                CreateQuad(10f, 0f, 1, semiTransparent: true, wibble: true),
                CreateQuad(10f, 0f, 1, semiTransparent: true, wibble: true, offsetX: 100f),
                CreateQuad(10f, 0f, 2, semiTransparent: true)));

        Assert.Equal(2, Assert.Single(steps).Value);
        Assert.Equal(new PsxFaceInstanceKey(0, 0), steps.Keys.Single());
    }

    [Fact]
    public void Thps2DcSkb2_DoesNotStackThePatchworkWaterTiles()
    {
        var path = paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)",
                "SKB2.PSX");
        Assert.SkipWhen(!File.Exists(path), "THPS2 DC sample not available");

        var file = PsxMeshFile.Parse(path);
        Assert.NotNull(file);
        var steps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(file!);

        // The water plane at Y≈301.8 tiles two scrolling ST textures
        // (0x36D4916F + 0x629763D7) side by side over an OPAQUE base
        // (0x2CB42720) at the exact same Y. The reported z-fight is the
        // ST-over-opaque pair at DC-scale depth precision — resolved by the
        // uniform one-step lift plus the viewer's log depth buffer. The two
        // ST tile sets never interpenetrate, so the layer detector must NOT
        // invent a stack for them (union-bounds grouping used to lift one
        // whole set 0.5 spuriously).
        foreach (var key in steps.Keys)
        {
            var face = file!.Meshes[file.Objects[key.ObjectIndex].MeshIndex].Faces[key.FaceIndex];
            Assert.False(face.TextureHash is 0x36D4916F or 0x629763D7,
                $"patchwork water tile 0x{face.TextureHash:X8} must not receive a stacked lift");
        }
    }

    private static PsxMeshFile CreateFile(params PsxMesh[] meshes)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = meshes.Select((_, index) => new PsxMeshObject { MeshIndex = (ushort)index }).ToList(),
            Meshes = meshes.ToList(),
            MeshNameHashes = new uint[meshes.Length],
            TextureHashes = [1, 2],
            ScaleDivisor = 2.25f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxMesh CreateQuad(
        float size, float y, uint textureHash, bool semiTransparent = false, bool wibble = false,
        float offsetX = 0f)
    {
        var half = size * 0.5f;
        var face = new PsxFace
        {
            IsQuad = true,
            IsTextured = true,
            IsSemiTransparent = semiTransparent,
            TextureHash = textureHash,
            Index0 = 0,
            Index1 = 1,
            Index2 = 2,
            Index3 = 3
        };
        if (wibble)
        {
            face.ApplyTextureWibble(new PsxTextureWibble
            {
                UVelocity = 1,
                VVelocity = 0,
                Frequency = 1,
                ZeroUAmplitudes = true,
                ZeroVAmplitudes = true,
                Vertices = []
            });
        }

        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = offsetX - half, Y = y, Z = -half },
                new PsxVertex { X = offsetX + half, Y = y, Z = -half },
                new PsxVertex { X = offsetX - half, Y = y, Z = half },
                new PsxVertex { X = offsetX + half, Y = y, Z = half }
            ],
            Normals = [new PsxNormal { Y = 1f }],
            Faces = [face]
        };
    }
}