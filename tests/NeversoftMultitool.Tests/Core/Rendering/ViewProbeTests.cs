using System.Numerics;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class ViewProbeTests
{
    [Fact]
    public void Cast_TwoCoplanarSheetsAQuarterUnitApart_ReportsBothInOrder()
    {
        // The z-fighting shape the probe exists to name: a decal lifted 0.25 in front
        // of the surface it decorates. Reporting only the nearest would hide the pair.
        var scene = new RenderScene();
        scene.Submeshes.Add(Quad("dt_pawnsign01__overlay32", -9.75f));
        scene.Submeshes.Add(Quad("ajc_bldg01", -10f));

        var hits = ViewProbe.Cast(scene, Vector3.Zero, new Vector3(0f, 0f, -1f));

        Assert.Equal(2, hits.Count);
        Assert.Equal("dt_pawnsign01__overlay32", hits[0].NodeName);
        Assert.Equal(9.75f, hits[0].Distance, 4);
        Assert.Equal("ajc_bldg01", hits[1].NodeName);
        Assert.Equal(10f, hits[1].Distance, 4);
        Assert.Equal(0.25f, hits[1].Distance - hits[0].Distance, 4);
    }

    [Fact]
    public void Cast_MissingTheGeometry_ReportsNothing()
    {
        var scene = new RenderScene();
        scene.Submeshes.Add(Quad("wall", -10f));

        Assert.Empty(ViewProbe.Cast(scene, Vector3.Zero, new Vector3(0f, 1f, 0f)));
    }

    [Fact]
    public void Cast_GeometryBehindTheEye_IsNotReported()
    {
        var scene = new RenderScene();
        scene.Submeshes.Add(Quad("behind", 10f));

        Assert.Empty(ViewProbe.Cast(scene, Vector3.Zero, new Vector3(0f, 0f, -1f)));
    }

    [Fact]
    public void Cast_SurfaceFacingAway_IsStillReportedAndFlagged()
    {
        // Standing inside a room, the walls that matter face away. Culling them
        // would suppress exactly the surfaces being asked about.
        var scene = new RenderScene();
        scene.Submeshes.Add(Quad("inside_wall", -10f, reversed: true));

        var hit = Assert.Single(ViewProbe.Cast(scene, Vector3.Zero, new Vector3(0f, 0f, -1f)));

        Assert.True(hit.IsBackFacing);
        Assert.Equal("inside_wall", hit.NodeName);
    }

    [Fact]
    public void Cast_CarriesMaterialFactsNeededToReadTheResult()
    {
        var scene = new RenderScene();
        scene.Submeshes.Add(new RenderSubmesh
        {
            Positions = [-1f, -1f, -5f, 1f, -1f, -5f, 0f, 1f, -5f],
            Triangles = [0, 1, 2],
            NodeName = "glass",
            MeshName = "glass_mesh",
            AlphaMode = 1,
            IsDoubleSided = true,
            TextureData = new byte[4],
            TextureWidth = 1,
            TextureHeight = 1
        });

        var hit = Assert.Single(ViewProbe.Cast(scene, Vector3.Zero, new Vector3(0f, 0f, -1f)));

        Assert.Equal(1, hit.AlphaMode);
        Assert.True(hit.IsDoubleSided);
        Assert.True(hit.HasTexture);
        Assert.Equal("glass_mesh", hit.MeshName);
        Assert.Equal(0, hit.SubmeshIndex);
        Assert.Equal(0, hit.TriangleIndex);
    }

    [Fact]
    public void Cast_ReportsTheHitPointOnTheSurface()
    {
        var scene = new RenderScene();
        scene.Submeshes.Add(Quad("wall", -10f));

        var hit = ViewProbe.Cast(scene, Vector3.Zero, new Vector3(0f, 0f, -1f))[0];

        Assert.Equal(0f, hit.Point.X, 4);
        Assert.Equal(0f, hit.Point.Y, 4);
        Assert.Equal(-10f, hit.Point.Z, 4);
    }

    [Fact]
    public void RayDirection_CentreOfTheFrame_LooksAlongTheViewDirection()
    {
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 101, 101);

        var direction = ViewProbe.RayDirection(pose, 50, 50);

        // Odd dimensions have a true centre pixel, so this is exact.
        Assert.Equal(0f, direction.X, 4);
        Assert.Equal(0f, direction.Y, 4);
        Assert.Equal(-1f, direction.Z, 4);
    }

    [Fact]
    public void RayDirection_UpperRowsLookUpward()
    {
        // Pixel rows count downward from the top, so row 0 must aim above the axis.
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 101, 101);

        Assert.True(ViewProbe.RayDirection(pose, 50, 0).Y > 0f);
        Assert.True(ViewProbe.RayDirection(pose, 50, 100).Y < 0f);
        Assert.True(ViewProbe.RayDirection(pose, 100, 50).X > 0f);
    }

    /// <summary>
    ///     A single sheet facing +Z at the given depth, with the view axis strictly
    ///     inside it.
    /// </summary>
    /// <remarks>
    ///     One triangle rather than a quad on purpose: a quad's two halves share a
    ///     diagonal, and a ray straight down the middle crosses it, so every hit would
    ///     legitimately be reported twice and obscure what the test is measuring.
    /// </remarks>
    private static RenderSubmesh Quad(string name, float z, bool reversed = false)
    {
        return new RenderSubmesh
        {
            Positions = [-4f, -3f, z, 4f, -3f, z, 0f, 5f, z],
            Triangles = reversed ? [2, 1, 0] : [0, 1, 2],
            NodeName = name
        };
    }
}
