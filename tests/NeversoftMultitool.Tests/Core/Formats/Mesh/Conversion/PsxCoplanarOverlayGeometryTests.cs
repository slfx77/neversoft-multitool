using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Ground-truth geometry for <see cref="PsxCoplanarOverlayDetector" />'s
///     exact coplanar shared-area test, which decides whether two coplanar faces
///     genuinely overlap or merely touch. Every expectation here is hand-computed
///     from the fixture coordinates rather than read back from the implementation,
///     which matters because the shipped near-equal overlay branch is defined in
///     terms of this fraction: an AABB-only rule flagged 266 of 296 same-texture
///     l2a1_g pairs that share nothing but an edge, while the cheaper
///     centroid-inside rule dropped real partial overlaps (THPS2 DC SKPH 0.32,
///     THPS1 skmall 0.23).
///
///     NOTE the vertex order. PSX quads are stored in triangle-STRIP order, so
///     slots 0,1,2,3 walk the perimeter as 0,1,3,2 — feeding the raw slot order
///     to a polygon clipper produces a self-intersecting bowtie whose area is
///     zero. Several cases below would return 0 instead of 1 under that bug.
/// </summary>
public sealed class PsxCoplanarOverlayGeometryTests
{
    /// <summary>Unit quad in PSX strip order (0,1,2,3 => perimeter 0,1,3,2).</summary>
    private static Vector3[] StripQuad(float x, float y, float size = 1f, float z = 0f)
    {
        return
        [
            new Vector3(x, y, z),
            new Vector3(x + size, y, z),
            new Vector3(x, y + size, z),
            new Vector3(x + size, y + size, z)
        ];
    }

    [Fact]
    public void CoincidentQuads_ShareTheirWholeArea()
    {
        // Also the strip-order guard: a bowtie reading of these points has area 0.
        var fraction = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            StripQuad(0f, 0f), StripQuad(0f, 0f));

        Assert.Equal(1f, fraction, 3);
    }

    [Fact]
    public void EdgeAdjacentQuads_ShareNothing()
    {
        // Tiling: touch along x = 1 with zero interior overlap. AABB penetration
        // alone accepts this pair, which is the false-positive class.
        var fraction = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            StripQuad(0f, 0f), StripQuad(1f, 0f));

        Assert.Equal(0f, fraction, 4);
        Assert.True(fraction < PsxCoplanarOverlayDetector.MinimumSharedAreaFraction);
    }

    [Fact]
    public void CornerTouchingQuads_ShareNothing()
    {
        var fraction = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            StripQuad(0f, 0f), StripQuad(1f, 1f));

        Assert.Equal(0f, fraction, 4);
    }

    [Fact]
    public void QuarterOverlap_ReportsExactlyAQuarterOfTheSmallerFace()
    {
        // Offset by (0.5, 0.5): the intersection is a 0.5 x 0.5 square = 0.25,
        // and both faces have area 1. This is the partial-overlap class the
        // centroid-inside test silently dropped — neither centroid (0.5, 0.5) and
        // (1.0, 1.0) lies strictly inside the other face's interior region.
        var fraction = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            StripQuad(0f, 0f), StripQuad(0.5f, 0.5f));

        Assert.Equal(0.25f, fraction, 3);
        Assert.True(fraction >= PsxCoplanarOverlayDetector.MinimumSharedAreaFraction);
    }

    [Fact]
    public void SmallFaceInsideLargeFace_ReportsFractionOfTheSmallerFace()
    {
        // A 0.5-unit decal wholly inside a 2-unit base: the shared area is the
        // decal's own 0.25, so the fraction of the SMALLER face is 1.0.
        var fraction = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            StripQuad(0f, 0f, 2f), StripQuad(0.5f, 0.5f, 0.5f));

        Assert.Equal(1f, fraction, 3);
    }

    [Fact]
    public void ReversedWinding_GivesTheSameFraction()
    {
        var quad = StripQuad(0f, 0f);
        var reversed = new[] { quad[3], quad[2], quad[1], quad[0] };

        var forward = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            quad, StripQuad(0.5f, 0.5f));
        var backward = PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            reversed, StripQuad(0.5f, 0.5f));

        Assert.Equal(forward, backward, 4);
    }

    [Fact]
    public void ArgumentOrder_DoesNotChangeTheFraction()
    {
        var large = StripQuad(0f, 0f, 2f);
        var small = StripQuad(0.5f, 0.5f, 0.5f);

        Assert.Equal(
            PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(large, small),
            PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(small, large),
            4);
    }

    [Fact]
    public void DegenerateFace_ReportsNoOverlapInsteadOfThrowing()
    {
        Vector3[] degenerate =
        [
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero
        ];

        Assert.Equal(0f, PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            StripQuad(0f, 0f), degenerate), 4);
    }

    [Fact]
    public void TriangleOverlappingQuad_MeasuresTheClippedArea()
    {
        // Right triangle with legs 1 (area 0.5) against the unit quad it sits
        // inside: the whole triangle is shared, so the smaller-face fraction is 1.
        Vector3[] triangle =
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f)
        ];

        Assert.Equal(1f, PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            triangle, StripQuad(0f, 0f)), 3);
    }

    [Fact]
    public void VerticalPlane_ProjectsOntoTheCorrectAxisPair()
    {
        // Faces on the x = 0 plane: the dominant-axis projection must drop X, not
        // a hard-coded axis, or every wall decal collapses to zero area.
        Vector3[] first =
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 1f, 1f)
        ];
        Vector3[] second =
        [
            new Vector3(0f, 0.5f, 0.5f),
            new Vector3(0f, 1.5f, 0.5f),
            new Vector3(0f, 0.5f, 1.5f),
            new Vector3(0f, 1.5f, 1.5f)
        ];

        Assert.Equal(0.25f, PsxCoplanarOverlayDetector.CoplanarSharedAreaFraction(
            first, second), 3);
    }
}
