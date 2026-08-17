using System.Numerics;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class PerspectiveProjectorTests
{
    private const float Near = 1f;

    [Fact]
    public void ClipToNearPlane_TriangleFullyInFront_PassesThroughUnchanged()
    {
        Span<PerspectiveProjector.ClipVertex> output = stackalloc PerspectiveProjector.ClipVertex[4];

        var count = PerspectiveProjector.ClipToNearPlane(
            Corners(10f, 10f, 10f), Near, output);

        Assert.Equal(3, count);
        Assert.Equal(10f, output[0].Depth);
        Assert.Equal(10f, output[1].Depth);
        Assert.Equal(10f, output[2].Depth);
    }

    [Fact]
    public void ClipToNearPlane_TriangleFullyBehind_IsDropped()
    {
        Span<PerspectiveProjector.ClipVertex> output = stackalloc PerspectiveProjector.ClipVertex[4];

        var count = PerspectiveProjector.ClipToNearPlane(
            Corners(-5f, -1f, 0.5f), Near, output);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ClipToNearPlane_OneCornerInFront_YieldsOneTriangle()
    {
        Span<PerspectiveProjector.ClipVertex> output = stackalloc PerspectiveProjector.ClipVertex[4];

        var count = PerspectiveProjector.ClipToNearPlane(
            Corners(3f, -1f, -1f), Near, output);

        Assert.Equal(3, count);
        Assert.All(
            output[..count].ToArray(),
            vertex => Assert.True(vertex.Depth >= Near - 1e-4f));
    }

    [Fact]
    public void ClipToNearPlane_TwoCornersInFront_YieldsAQuad()
    {
        Span<PerspectiveProjector.ClipVertex> output = stackalloc PerspectiveProjector.ClipVertex[4];

        var count = PerspectiveProjector.ClipToNearPlane(
            Corners(3f, 3f, -1f), Near, output);

        Assert.Equal(4, count);
        Assert.All(
            output[..count].ToArray(),
            vertex => Assert.True(vertex.Depth >= Near - 1e-4f));
    }

    [Fact]
    public void ClipToNearPlane_InterpolatesAttributesAtTheCrossing()
    {
        // Corner 0 sits at depth 3 with U=0; corner 1 at depth -1 with U=1. The plane
        // at depth 1 is exactly half way, so the new corner must carry U=0.5.
        Span<PerspectiveProjector.ClipVertex> corners = stackalloc PerspectiveProjector.ClipVertex[3];
        corners[0] = new PerspectiveProjector.ClipVertex { Depth = 3f, U = 0f, X = 0f };
        corners[1] = new PerspectiveProjector.ClipVertex { Depth = -1f, U = 1f, X = 4f };
        corners[2] = new PerspectiveProjector.ClipVertex { Depth = 3f, U = 0f, X = 0f };

        Span<PerspectiveProjector.ClipVertex> output = stackalloc PerspectiveProjector.ClipVertex[4];
        var count = PerspectiveProjector.ClipToNearPlane(corners, Near, output);

        Assert.Equal(4, count);
        Assert.Equal(0.5f, output[1].U, 4);
        Assert.Equal(2f, output[1].X, 4);
        Assert.Equal(Near, output[1].Depth, 4);
    }

    [Fact]
    public void ClipToNearPlane_CornerExactlyOnThePlane_CountsAsInFront()
    {
        Span<PerspectiveProjector.ClipVertex> output = stackalloc PerspectiveProjector.ClipVertex[4];

        var count = PerspectiveProjector.ClipToNearPlane(
            Corners(Near, 5f, 5f), Near, output);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Project_CentredTriangle_LandsOnTheCanvasCentre()
    {
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 100, 100);
        var triangles = new List<RenderTriangle> { WorldTriangle(-1f, 1f, -10f, 1f, 1f, -10f, 0f, -1f, -10f) };

        var projected = PerspectiveProjector.Project(triangles, pose, 100, 100, 0.1f);

        var tri = Assert.Single(projected);
        // The apex sits directly below the eye axis, so its X projects to the centre.
        Assert.Equal(50f, tri.Sx2, 3);
        // 1/distance, so nearer is larger — what the rasterizer's depth test wants.
        Assert.Equal(0.1f, tri.Z0, 5);
    }

    [Fact]
    public void Project_NearerGeometryGetsLargerDepth()
    {
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 64, 64);
        var triangles = new List<RenderTriangle>
        {
            WorldTriangle(-1f, 1f, -5f, 1f, 1f, -5f, 0f, -1f, -5f),
            WorldTriangle(-1f, 1f, -50f, 1f, 1f, -50f, 0f, -1f, -50f)
        };

        var projected = PerspectiveProjector.Project(triangles, pose, 64, 64, 0.1f);

        Assert.Equal(2, projected.Count);
        Assert.True(projected[0].Z0 > projected[1].Z0);
    }

    [Fact]
    public void Project_GeometryBehindTheEye_IsRemovedEntirely()
    {
        // Without near clipping these corners divide by a negative depth and smear a
        // mirrored triangle across the frame — the failure this guard exists for.
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 64, 64);
        var triangles = new List<RenderTriangle> { WorldTriangle(-1f, 1f, 10f, 1f, 1f, 10f, 0f, -1f, 10f) };

        Assert.Empty(PerspectiveProjector.Project(triangles, pose, 64, 64, 0.1f));
    }

    [Fact]
    public void Project_TriangleStraddlingTheEye_SplitsIntoTwoOnScreenTriangles()
    {
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 64, 64);
        var triangles = new List<RenderTriangle> { WorldTriangle(-5f, 1f, -10f, 5f, 1f, -10f, 0f, -1f, 5f) };

        var projected = PerspectiveProjector.Project(triangles, pose, 64, 64, 0.1f);

        Assert.Equal(2, projected.Count);
        Assert.All(projected, tri =>
        {
            Assert.True(float.IsFinite(tri.Sx0) && float.IsFinite(tri.Sy0));
            Assert.True(float.IsFinite(tri.Sx1) && float.IsFinite(tri.Sy1));
            Assert.True(float.IsFinite(tri.Sx2) && float.IsFinite(tri.Sy2));
            Assert.True(tri.Z0 > 0f && tri.Z1 > 0f && tri.Z2 > 0f);
        });
    }

    [Fact]
    public void Project_MovingTheEyeCloser_EnlargesTheProjection()
    {
        var far = new ViewPose(new Vector3(0f, 0f, 0f), 0f, 0f, 90f, 64, 64);
        var near = new ViewPose(new Vector3(0f, 0f, -5f), 0f, 0f, 90f, 64, 64);
        var triangles = () => new List<RenderTriangle>
        {
            WorldTriangle(-1f, 1f, -10f, 1f, 1f, -10f, 0f, -1f, -10f)
        };

        var fromFar = PerspectiveProjector.Project(triangles(), far, 64, 64, 0.1f)[0];
        var fromNear = PerspectiveProjector.Project(triangles(), near, 64, 64, 0.1f)[0];

        Assert.True(fromNear.Sx1 - fromNear.Sx0 > fromFar.Sx1 - fromFar.Sx0);
    }

    private static PerspectiveProjector.ClipVertex[] Corners(float d0, float d1, float d2)
    {
        return
        [
            new PerspectiveProjector.ClipVertex { Depth = d0 },
            new PerspectiveProjector.ClipVertex { Depth = d1 },
            new PerspectiveProjector.ClipVertex { Depth = d2 }
        ];
    }

    private static RenderTriangle WorldTriangle(
        float x0, float y0, float z0,
        float x1, float y1, float z1,
        float x2, float y2, float z2)
    {
        return new RenderTriangle
        {
            Sx0 = x0, Sy0 = y0, Z0 = z0,
            Sx1 = x1, Sy1 = y1, Z1 = z1,
            Sx2 = x2, Sy2 = y2, Z2 = z2
        };
    }
}
