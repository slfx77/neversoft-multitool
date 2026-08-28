using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

/// <summary>
///     Pins the projection/direction choices a level export offers, and the angles
///     each one resolves to.
/// </summary>
public sealed class LevelExportProjectionTests
{
    [Fact]
    public void EveryProjectionOffersFourDistinctCompassDirections()
    {
        foreach (var projection in Enum.GetValues<LevelExportProjection>())
        {
            var views = LevelExportProjections.Directions(projection);
            Assert.Equal(4, views.Count);
            Assert.Equal(4, views.Select(v => v.Azimuth).Distinct().Count());
            Assert.Equal(4, views.Select(v => v.Label).Distinct().Count());

            // Stem suffixes keep four shots of one level from overwriting each other.
            Assert.Equal(4, views.Select(v => v.StemSuffix).Distinct().Count());
            Assert.All(views, v => Assert.StartsWith("_", v.StemSuffix, StringComparison.Ordinal));

            // One elevation per projection: the direction turns the camera, it does
            // not tilt it.
            Assert.Single(views.Select(v => v.Elevation).Distinct());
        }
    }

    [Fact]
    public void TheThreeProjectionsAreThreeDifferentElevations()
    {
        Assert.Equal(90f, Elevation(LevelExportProjection.Orthographic));
        Assert.Equal(30f, Elevation(LevelExportProjection.Isometric));

        // Bethesda Multitool's own trimetric constant, so the two tools' trimetric
        // shots are the same shot.
        Assert.Equal(25.65891f, Elevation(LevelExportProjection.Trimetric));

        static float Elevation(LevelExportProjection p) =>
            LevelExportProjections.Directions(p)[0].Elevation;
    }

    [Fact]
    public void TopDownNamesWhichWayIsUpAndTheOthersNameWhereYouStand()
    {
        Assert.Equal(
            ["North at top", "East at top", "South at top", "West at top"],
            LevelExportProjections.Directions(LevelExportProjection.Orthographic)
                .Select(v => v.Label));

        // A tilted view has a horizon, so "north at top" is meaningless; it is named
        // by where the camera is instead.
        Assert.All(
            LevelExportProjections.Directions(LevelExportProjection.Isometric),
            v => Assert.StartsWith("From the ", v.Label, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The cap is not cosmetic: the software rasterizer supersamples 2x, so a
    ///     4096 long edge already allocates on the order of 360 MB.
    /// </summary>
    [Fact]
    public void TheLongEdgeIsCappedWhereTheRasterizerStopsBeingReasonable()
    {
        Assert.Equal(4096, LevelExportProjections.MaxLongEdge);
    }
}
