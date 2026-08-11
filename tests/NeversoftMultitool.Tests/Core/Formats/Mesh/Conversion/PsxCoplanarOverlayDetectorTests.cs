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
        // Re-pinned 2026-08-03 (82 to 77): back-to-back single-sided pairs no
        // longer flag anywhere — backface culling already separates them — and
        // that removes five of this file's flags; the appearance-twin and
        // exact-area-decal additions do not fire here. The retired corpus
        // census also measured 77.
        // Re-pinned 2026-08-10 (77 to 80): secondary-triangle discovery finds
        // the independently verified o209f33 overlay on o124f7, while opaque
        // sprite candidates now use the writer's expanded corners and recover
        // o154f0/o46f8 and o151f0/o46f10.
        Assert.Equal(80, overlays.Count);
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
        // Assert the OUTCOME, not a loop over it: the detector finds no stack
        // anywhere in this file, so iterating its (empty) results asserted
        // nothing at all and passed no matter what the detector did.
        Assert.Empty(steps);
    }

    [Fact]
    public void DiagnosePairs_ReportsTheOverlaySelectedByProductionClassification()
    {
        var file = CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0f, 2));

        var diagnostic = Assert.Single(PsxCoplanarOverlayDetector.DiagnosePairs(file));

        Assert.Equal(new PsxFaceInstanceKey(1, 0), diagnostic.Overlay);
        Assert.Null(diagnostic.DeclineReason);
        Assert.Null(diagnostic.NotComparedReason);
        Assert.Equal(diagnostic.FirstPlanes!.Value.Primary, diagnostic.SecondPlanes!.Value.Primary);
        Assert.Equal(Assert.Single(PsxCoplanarOverlayDetector.Find(file)), diagnostic.Overlay);
    }

    [Theory]
    [InlineData("back-to-back", nameof(PsxCoplanarPairDeclineReason.BackToBackSingleSided))]
    [InlineData("exact-twin", nameof(PsxCoplanarPairDeclineReason.SameTextureExactTwin))]
    [InlineData("same-texture-apart", nameof(PsxCoplanarPairDeclineReason.SameTextureWithoutBoundsOverlap))]
    [InlineData("semi-transparent", nameof(PsxCoplanarPairDeclineReason.SemiTransparentMember))]
    [InlineData("small-face-apart", nameof(PsxCoplanarPairDeclineReason.SmallerFaceHasInsufficientSharedArea))]
    [InlineData("near-equal-apart", nameof(PsxCoplanarPairDeclineReason.NearEqualWithoutBoundsOverlap))]
    public void DiagnosePairs_ReportsTheExactProductionDeclineReason(
        string scenario,
        string expectedName)
    {
        var file = scenario switch
        {
            "back-to-back" => CreateFile(
                CreateQuad(10f, 0f, 1),
                CreateQuad(10f, 0f, 2, reverseWinding: true)),
            "exact-twin" => CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(10f, 0f, 1)),
            "same-texture-apart" => CreateFile(
                CreateQuad(10f, 0f, 1),
                CreateQuad(10f, 0f, 1, offsetX: 20f)),
            "semi-transparent" => CreateFile(
                CreateQuad(10f, 0f, 1),
                CreateQuad(2f, 0f, 2, semiTransparent: true)),
            "small-face-apart" => CreateFile(
                CreateQuad(10f, 0f, 1),
                CreateQuad(2f, 0f, 2, offsetX: 20f)),
            "near-equal-apart" => CreateFile(
                CreateQuad(10f, 0f, 1),
                CreateQuad(10f, 0f, 2, offsetX: 20f)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        var diagnostic = Assert.Single(PsxCoplanarOverlayDetector.DiagnosePairs(file));

        Assert.Null(diagnostic.Overlay);
        Assert.Equal(Enum.Parse<PsxCoplanarPairDeclineReason>(expectedName), diagnostic.DeclineReason);
        Assert.Null(diagnostic.NotComparedReason);
        Assert.Empty(PsxCoplanarOverlayDetector.Find(file));
    }

    [Fact]
    public void DiagnosePairs_ReportsNearEqualFacesWithInsufficientSharedArea()
    {
        // These triangles have identical AABBs but occupy opposite halves of
        // the box, touching only along their shared diagonal edge.
        var file = CreateFile(
            CreateDiagonalHalfTriangle(upper: false, textureHash: 1),
            CreateDiagonalHalfTriangle(upper: true, textureHash: 2));

        var diagnostic = Assert.Single(PsxCoplanarOverlayDetector.DiagnosePairs(file));

        Assert.Null(diagnostic.Overlay);
        Assert.Equal(
            PsxCoplanarPairDeclineReason.NearEqualHasInsufficientSharedArea,
            diagnostic.DeclineReason);
        Assert.Null(diagnostic.NotComparedReason);
        Assert.Empty(PsxCoplanarOverlayDetector.Find(file));
    }

    [Fact]
    public void DiagnosePair_DistinguishesPlaneBucketMissFromAClassifierDecline()
    {
        var file = CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0.1f, 2));

        var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(
            file,
            new PsxFaceInstanceKey(0, 0),
            new PsxFaceInstanceKey(1, 0));

        Assert.Null(diagnostic.Overlay);
        Assert.Null(diagnostic.DeclineReason);
        Assert.Equal(
            PsxCoplanarPairNotComparedReason.DifferentPlaneBuckets,
            diagnostic.NotComparedReason);
        Assert.NotEqual(diagnostic.FirstPlanes!.Value.Primary, diagnostic.SecondPlanes!.Value.Primary);
        Assert.Empty(PsxCoplanarOverlayDetector.DiagnosePairs(file));
    }

    [Fact]
    public void Find_AdmitsOnlyTheMeasuredRawSeamBetweenAdjacentDistanceBuckets()
    {
        // Both pairs straddle a 1/100 quantisation boundary. The first stays
        // within the 0.005 raw seam measured on the round-3 corpus residue.
        // the second is just beyond it and must not become a comparison merely
        // because its rounded distance keys are still neighbours.
        var admitted = CreateFile(
            CreateQuad(10f, 0.004f, 1),
            CreateQuad(2f, 0.0089f, 2));
        var rejected = CreateFile(
            CreateQuad(10f, 0.0049f, 1),
            CreateQuad(2f, 0.0101f, 2));
        var first = new PsxFaceInstanceKey(0, 0);
        var second = new PsxFaceInstanceKey(1, 0);

        var admittedDiagnostic = PsxCoplanarOverlayDetector.DiagnosePair(
            admitted, first, second);
        var rejectedDiagnostic = PsxCoplanarOverlayDetector.DiagnosePair(
            rejected, first, second);

        Assert.Equal(second, admittedDiagnostic.Overlay);
        Assert.Null(admittedDiagnostic.DeclineReason);
        Assert.Null(admittedDiagnostic.NotComparedReason);
        Assert.InRange(admittedDiagnostic.AdmittedPlaneDistanceDelta!.Value, 0f, 0.005f);
        Assert.Equal(
            1,
            Math.Abs(
                admittedDiagnostic.FirstPlanes!.Value.Primary.Distance
                - admittedDiagnostic.SecondPlanes!.Value.Primary.Distance));
        Assert.Equal(second, Assert.Single(PsxCoplanarOverlayDetector.Find(admitted)));

        Assert.Null(rejectedDiagnostic.Overlay);
        Assert.Null(rejectedDiagnostic.DeclineReason);
        Assert.Equal(
            PsxCoplanarPairNotComparedReason.DifferentPlaneBuckets,
            rejectedDiagnostic.NotComparedReason);
        Assert.Equal(
            1,
            Math.Abs(
                rejectedDiagnostic.FirstPlanes!.Value.Primary.Distance
                - rejectedDiagnostic.SecondPlanes!.Value.Primary.Distance));
        Assert.Empty(PsxCoplanarOverlayDetector.Find(rejected));
    }

    [Fact]
    public void DiagnosePair_ReportsARequestForTheSameFace()
    {
        var file = CreateFile(CreateQuad(10f, 0f, 1));
        var key = new PsxFaceInstanceKey(0, 0);

        var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(file, key, key);

        Assert.Equal(key, diagnostic.First);
        Assert.Equal(key, diagnostic.Second);
        Assert.Null(diagnostic.Overlay);
        Assert.Null(diagnostic.DeclineReason);
        Assert.Equal(PsxCoplanarPairNotComparedReason.SameFace, diagnostic.NotComparedReason);
        Assert.Null(diagnostic.FirstPlanes);
        Assert.Null(diagnostic.SecondPlanes);
    }

    [Theory]
    [InlineData("first", nameof(PsxCoplanarPairNotComparedReason.FirstFaceHasNoCandidate))]
    [InlineData("second", nameof(PsxCoplanarPairNotComparedReason.SecondFaceHasNoCandidate))]
    [InlineData("both", nameof(PsxCoplanarPairNotComparedReason.BothFacesHaveNoCandidate))]
    public void DiagnosePair_ReportsWhichRequestedFacesHaveNoProductionCandidate(
        string scenario,
        string expectedName)
    {
        var file = CreateFile(CreateQuad(10f, 0f, 1));
        var valid = new PsxFaceInstanceKey(0, 0);
        var missingFirst = new PsxFaceInstanceKey(98, 0);
        var missingSecond = new PsxFaceInstanceKey(99, 0);
        var (first, second) = scenario switch
        {
            "first" => (missingFirst, valid),
            "second" => (valid, missingSecond),
            "both" => (missingFirst, missingSecond),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(file, first, second);

        Assert.Null(diagnostic.Overlay);
        Assert.Null(diagnostic.DeclineReason);
        Assert.Equal(Enum.Parse<PsxCoplanarPairNotComparedReason>(expectedName), diagnostic.NotComparedReason);
        Assert.Equal(first == valid, diagnostic.FirstPlanes.HasValue);
        Assert.Equal(second == valid, diagnostic.SecondPlanes.HasValue);
    }

    [Fact]
    public void KnownRound3ResiduePairs_UseWriterGeometryAndRecoverEverySameFilePair()
    {
        var schoolPath = paths.FindSampleFile(
            "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)",
            "skschl.psx");
        var l2A1Path = paths.FindSampleFile(
            "Spider-Man (2000-9-1, PSX - Final)",
            "l2a1_g.psx");
        Assert.SkipWhen(
            schoolPath == null || l2A1Path == null,
            "PSX round-3 residue fixtures not available");

        var school = PsxMeshFile.Parse(schoolPath!);
        var l2A1 = PsxMeshFile.Parse(l2A1Path!);
        Assert.NotNull(school);
        Assert.NotNull(l2A1);

        // The independent exported-GLB oracle's 14 same-file actionable
        // triangle pairs collapse to eight source-face pairs. Six reach
        // production through the measured distance seam / rendered secondary
        // triangle; the final two use the writer's expanded sprite corners.
        var schoolOverlays = PsxCoplanarOverlayDetector.Find(school!);
        var l2A1Overlays = PsxCoplanarOverlayDetector.Find(l2A1!);
        AssertAdjacentPlaneDistanceBuckets(school!, schoolOverlays,
        [
            (new PsxFaceInstanceKey(286, 0), new PsxFaceInstanceKey(981, 34),
                new PsxFaceInstanceKey(286, 0)),
            (new PsxFaceInstanceKey(286, 1), new PsxFaceInstanceKey(981, 34),
                new PsxFaceInstanceKey(286, 1)),
            (new PsxFaceInstanceKey(367, 3), new PsxFaceInstanceKey(982, 20),
                new PsxFaceInstanceKey(367, 3)),
            (new PsxFaceInstanceKey(720, 17), new PsxFaceInstanceKey(968, 1),
                new PsxFaceInstanceKey(968, 1))
        ]);
        AssertSecondaryTriangleMatch(
            school!,
            schoolOverlays,
            new PsxFaceInstanceKey(367, 3),
            new PsxFaceInstanceKey(982, 8),
            new PsxFaceInstanceKey(982, 8));
        AssertRecovered(
            l2A1!,
            l2A1Overlays,
            new PsxFaceInstanceKey(46, 8),
            new PsxFaceInstanceKey(154, 0),
            new PsxFaceInstanceKey(154, 0));
        AssertRecovered(
            l2A1!,
            l2A1Overlays,
            new PsxFaceInstanceKey(46, 10),
            new PsxFaceInstanceKey(151, 0),
            new PsxFaceInstanceKey(151, 0));
        AssertSecondaryTriangleMatch(
            l2A1!,
            l2A1Overlays,
            new PsxFaceInstanceKey(124, 7),
            new PsxFaceInstanceKey(209, 33),
            new PsxFaceInstanceKey(209, 33));

        static void AssertAdjacentPlaneDistanceBuckets(
            PsxMeshFile file,
            IReadOnlySet<PsxFaceInstanceKey> overlays,
            (PsxFaceInstanceKey First, PsxFaceInstanceKey Second,
                PsxFaceInstanceKey Overlay)[] pairs)
        {
            Assert.NotEmpty(pairs);
            foreach (var (first, second, overlay) in pairs)
            {
                var diagnostic = AssertRecovered(file, overlays, first, second, overlay);
                var firstPlane = diagnostic.FirstPlanes!.Value.Primary;
                var secondPlane = diagnostic.SecondPlanes!.Value.Primary;
                Assert.Equal((firstPlane.X, firstPlane.Y, firstPlane.Z),
                    (secondPlane.X, secondPlane.Y, secondPlane.Z));
                Assert.Equal(1, Math.Abs(firstPlane.Distance - secondPlane.Distance));
            }
        }

        static void AssertSecondaryTriangleMatch(
            PsxMeshFile file,
            IReadOnlySet<PsxFaceInstanceKey> overlays,
            PsxFaceInstanceKey first,
            PsxFaceInstanceKey second,
            PsxFaceInstanceKey overlay)
        {
            var diagnostic = AssertRecovered(file, overlays, first, second, overlay);
            var firstPlanes = diagnostic.FirstPlanes!.Value;
            var secondPlanes = diagnostic.SecondPlanes!.Value;
            Assert.True(
                firstPlanes.Secondary == secondPlanes.Primary
                || secondPlanes.Secondary == firstPlanes.Primary,
                $"No primary/secondary plane-key match for {first} and {second}");
        }

        static PsxCoplanarPairDiagnostic AssertRecovered(
            PsxMeshFile file,
            IReadOnlySet<PsxFaceInstanceKey> overlays,
            PsxFaceInstanceKey first,
            PsxFaceInstanceKey second,
            PsxFaceInstanceKey overlay)
        {
            var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(file, first, second);
            Assert.Equal(overlay, diagnostic.Overlay);
            Assert.Null(diagnostic.DeclineReason);
            Assert.Null(diagnostic.NotComparedReason);
            Assert.InRange(diagnostic.AdmittedPlaneDistanceDelta!.Value, 0f, 0.005f);
            var admittedSharedArea = diagnostic.FirstAdmissionUsesPrimaryTriangle == true
                                     && diagnostic.SecondAdmissionUsesPrimaryTriangle == true
                ? diagnostic.SharedAreaFraction
                : diagnostic.AdmittedTriangleSharedAreaFraction;
            Assert.True(
                admittedSharedArea >= CoplanarOverlayGeometry.MinimumSharedAreaFraction,
                $"Insufficient admitted shared area for {first} and {second}: "
                + admittedSharedArea);
            Assert.Contains(overlay, overlays);
            return diagnostic;
        }

    }

    [Fact]
    public void Thps2Marseille_SecondarySliversDeclineAndRawDistanceSeamStaysNarrow()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)",
            "skmar.psx");
        Assert.SkipWhen(path == null, "THPS2 PSX Marseille fixture not available");

        var file = PsxMeshFile.Parse(path!);
        Assert.NotNull(file);

        // Whole-quad projection reports 4.8..6.9% shared area for these
        // warped neighbours, but the triangle planes that admitted them share
        // only 0.23..0.47%. The established 1% floor must apply to those
        // rendered triangles, or secondary-plane discovery invents overlays.
        var secondarySlivers = new[]
        {
            (new PsxFaceInstanceKey(594, 8), new PsxFaceInstanceKey(594, 9),
                PsxCoplanarPairDeclineReason.SmallerFaceHasInsufficientSharedArea),
            (new PsxFaceInstanceKey(595, 1), new PsxFaceInstanceKey(595, 4),
                PsxCoplanarPairDeclineReason.SmallerFaceHasInsufficientSharedArea),
            (new PsxFaceInstanceKey(596, 1), new PsxFaceInstanceKey(596, 4),
                PsxCoplanarPairDeclineReason.NearEqualHasInsufficientSharedArea)
        };
        foreach (var (first, second, reason) in secondarySlivers)
        {
            var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(
                file!, first, second);
            Assert.Null(diagnostic.Overlay);
            Assert.Equal(reason, diagnostic.DeclineReason);
            Assert.Null(diagnostic.NotComparedReason);
            Assert.False(
                diagnostic.FirstAdmissionUsesPrimaryTriangle!.Value
                && diagnostic.SecondAdmissionUsesPrimaryTriangle!.Value);
            Assert.True(
                diagnostic.SharedAreaFraction
                >= CoplanarOverlayGeometry.MinimumSharedAreaFraction);
            Assert.True(
                diagnostic.AdmittedTriangleSharedAreaFraction
                < CoplanarOverlayGeometry.MinimumSharedAreaFraction);
        }

        var overlays = PsxCoplanarOverlayDetector.Find(file!);
        AssertAccepted(
            new PsxFaceInstanceKey(610, 1),
            new PsxFaceInstanceKey(610, 4),
            new PsxFaceInstanceKey(610, 4));
        AssertAccepted(
            new PsxFaceInstanceKey(610, 6),
            new PsxFaceInstanceKey(610, 7),
            new PsxFaceInstanceKey(610, 7));

        // This otherwise-identical neighbour measured 0.0073242188 apart,
        // outside the evidence-backed 0.005 seam, and remains undiscovered.
        var outside = PsxCoplanarOverlayDetector.DiagnosePair(
            file!,
            new PsxFaceInstanceKey(609, 1),
            new PsxFaceInstanceKey(609, 4));
        Assert.Null(outside.Overlay);
        Assert.Null(outside.DeclineReason);
        Assert.Equal(
            PsxCoplanarPairNotComparedReason.DifferentPlaneBuckets,
            outside.NotComparedReason);
        Assert.Equal(
            1,
            Math.Abs(
                outside.FirstPlanes!.Value.Primary.Distance
                - outside.SecondPlanes!.Value.Primary.Distance));

        void AssertAccepted(
            PsxFaceInstanceKey first,
            PsxFaceInstanceKey second,
            PsxFaceInstanceKey overlay)
        {
            var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(
                file!, first, second);
            Assert.Equal(overlay, diagnostic.Overlay);
            Assert.Null(diagnostic.DeclineReason);
            Assert.Null(diagnostic.NotComparedReason);
            Assert.InRange(diagnostic.AdmittedPlaneDistanceDelta!.Value, 0f, 0.005f);
            Assert.True(
                diagnostic.AdmittedTriangleSharedAreaFraction
                >= CoplanarOverlayGeometry.MinimumSharedAreaFraction);
            Assert.Contains(overlay, overlays);
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
        float offsetX = 0f, bool reverseWinding = false)
    {
        var half = size * 0.5f;
        var face = new PsxFace
        {
            IsQuad = true,
            IsTextured = true,
            IsSemiTransparent = semiTransparent,
            TextureHash = textureHash,
            Index0 = 0,
            Index1 = reverseWinding ? 2u : 1u,
            Index2 = reverseWinding ? 1u : 2u,
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

    private static PsxMesh CreateDiagonalHalfTriangle(bool upper, uint textureHash)
    {
        // Face slot order emits (0,2,1). Both halves use the same winding and
        // cover opposite sides of the diagonal from (0,10) to (10,0).
        var vertices = upper
            ? new List<PsxVertex>
            {
                new() { X = 10f, Z = -10f },
                new() { X = 10f, Z = 0f },
                new() { X = 0f, Z = -10f }
            }
            :
            [
                new PsxVertex { X = 0f, Z = 0f },
                new PsxVertex { X = 0f, Z = -10f },
                new PsxVertex { X = 10f, Z = 0f }
            ];
        return new PsxMesh
        {
            Vertices = vertices,
            Normals = [new PsxNormal { Y = 1f }],
            Faces =
            [
                new PsxFace
                {
                    IsTextured = true,
                    TextureHash = textureHash,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2
                }
            ]
        };
    }
}
