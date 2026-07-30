using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxGroundSnapTests
{
    // Native +Y = DOWN; this winding yields a floor-facing (ny < 0) triangle.
    private static (Vector3, Vector3, Vector3) Floor(float y) =>
        (new Vector3(0, y, 0), new Vector3(100, y, 0), new Vector3(0, y, 100));

    [Fact]
    public void SnapsPickupOriginToGroundMinusHover()
    {
        var terrain = PsxTerrainHeightField.Build([Floor(100f)]);
        // Origin above the floor (native +Y down => 50 is above 100). Divisor 1 so
        // world units == model units: origin rests 128 above the floor => Y 100-128.
        var snapped = PsxGroundSnap.SnapPickupToGround(
            terrain, new Vector3(10, 50, 10), translationDivisor: 1f, PsxGroundSnap.SpiderManHoverWorldUnits);

        Assert.Equal(100f - 128f, snapped.Y, 3);
        Assert.Equal(10f, snapped.X, 3); // XZ untouched
        Assert.Equal(10f, snapped.Z, 3);
    }

    [Fact]
    public void ZeroHoverRestsTheOriginOnTheFloor()
    {
        var terrain = PsxTerrainHeightField.Build([Floor(100f)]);
        var snapped = PsxGroundSnap.SnapPickupToGround(
            terrain, new Vector3(10, 50, 10), translationDivisor: 1f, hoverWorldUnits: 0f);

        Assert.Equal(100f, snapped.Y, 3); // hover 0 => origin on the floor
    }

    [Fact]
    public void HoverAndSearchWindowScaleByTheTranslationDivisor()
    {
        var terrain = PsxTerrainHeightField.Build([Floor(100f)]);
        // Divisor 2 => 128-world hover becomes 64 in model space.
        var snapped = PsxGroundSnap.SnapPickupToGround(
            terrain, new Vector3(10, 50, 10), translationDivisor: 2f, PsxGroundSnap.SpiderManHoverWorldUnits);

        Assert.Equal(100f - 64f, snapped.Y, 3);
    }

    [Fact]
    public void LeavesAPickupWithNoFloorBeneathItUnchanged()
    {
        var terrain = PsxTerrainHeightField.Build([Floor(100f)]);
        // (90,90) is outside the x+z <= 100 floor footprint — no resting floor.
        var pos = new Vector3(90, 50, 90);
        var snapped = PsxGroundSnap.SnapPickupToGround(
            terrain, pos, translationDivisor: 1f, PsxGroundSnap.SpiderManHoverWorldUnits);

        Assert.Equal(pos, snapped);
    }
}
