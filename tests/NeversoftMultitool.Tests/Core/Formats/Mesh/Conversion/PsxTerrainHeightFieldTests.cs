using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxTerrainHeightFieldTests
{
    // Native engine space: +Y = DOWN, so a floor's up-normal has a NEGATIVE Y.
    // This winding (a -> b -> c) yields cross().Y < 0 = floor-facing.
    private static (Vector3, Vector3, Vector3) Floor(float y) =>
        (new Vector3(0, y, 0), new Vector3(100, y, 0), new Vector3(0, y, 100));

    [Fact]
    public void ReturnsFloorHeightUnderAPoint()
    {
        var field = PsxTerrainHeightField.Build([Floor(100f)]);

        Assert.False(field.IsEmpty);
        Assert.True(field.TryGetGroundY(10f, 10f, nearNativeY: 90f, out var y));
        Assert.Equal(100f, y, 3);
    }

    [Fact]
    public void ReturnsFalseOutsideTheFootprint()
    {
        var field = PsxTerrainHeightField.Build([Floor(100f)]);

        // (90,90) is outside the x+z <= 100 triangle footprint.
        Assert.False(field.TryGetGroundY(90f, 90f, nearNativeY: 90f, out _));
    }

    [Fact]
    public void RejectsWalls()
    {
        // Vertical triangle in the x=0 plane: normal is horizontal (ny = 0).
        var wall = (new Vector3(0, 0, 0), new Vector3(0, 100, 0), new Vector3(0, 0, 100));
        var field = PsxTerrainHeightField.Build([wall]);

        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void RejectsCeilings()
    {
        // Reversed winding => normal points down (native +Y, ny > 0) = ceiling.
        var ceiling = (new Vector3(0, 100, 0), new Vector3(0, 100, 100), new Vector3(100, 100, 0));
        var field = PsxTerrainHeightField.Build([ceiling]);

        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void PicksTheFloorNearestTheObjectRestLevel()
    {
        // Two decks stacked in native space (larger Y = lower deck).
        var field = PsxTerrainHeightField.Build([Floor(50f), Floor(200f)]);

        Assert.True(field.TryGetGroundY(10f, 10f, nearNativeY: 60f, out var upper));
        Assert.Equal(50f, upper, 3);

        Assert.True(field.TryGetGroundY(10f, 10f, nearNativeY: 190f, out var lower));
        Assert.Equal(200f, lower, 3);
    }

    [Fact]
    public void RestingFloorFindsTheFloorBelowAFloatingObject()
    {
        var field = PsxTerrainHeightField.Build([Floor(100f)]);
        // base at native Y 70 => the floor (100) sits below it (native +Y down).
        Assert.True(field.TryGetRestingFloor(10f, 10f, baseNativeY: 70f, overhang: 8f, maxDrop: 4096f, out var y));
        Assert.Equal(100f, y, 3);
    }

    [Fact]
    public void RestingFloorReturnsFalseOverAHole()
    {
        var field = PsxTerrainHeightField.Build([Floor(100f)]);
        Assert.False(field.TryGetRestingFloor(90f, 90f, 70f, 8f, 4096f, out _)); // outside footprint
    }

    [Fact]
    public void RestingFloorPicksTheNearestFloorBelow()
    {
        var field = PsxTerrainHeightField.Build([Floor(100f), Floor(300f)]);
        Assert.True(field.TryGetRestingFloor(10f, 10f, baseNativeY: 50f, overhang: 8f, maxDrop: 4096f, out var y));
        Assert.Equal(100f, y, 3);
    }

    [Fact]
    public void RestingFloorRespectsMaxDrop()
    {
        var field = PsxTerrainHeightField.Build([Floor(100f)]);
        // Floor is 90 below the base but maxDrop is only 50 => not reachable.
        Assert.False(field.TryGetRestingFloor(10f, 10f, baseNativeY: 10f, overhang: 8f, maxDrop: 50f, out _));
    }

    [Fact]
    public void BuildFromLevelKeepsFloorsAndRejectsCeilings()
    {
        // A floor-facing quad (native-up ny<0) is kept and queryable...
        var floorField = PsxTerrainHeightField.BuildFromLevel(SingleQuadLevel(100f, reversed: false));
        Assert.False(floorField.IsEmpty);
        Assert.True(floorField.TryGetRestingFloor(5f, 5f, baseNativeY: 50f, overhang: 8f, maxDrop: 4096f, out var y));
        Assert.Equal(100f, y, 3);

        // ...but the exact reverse winding is a ceiling (ny>0) and must be dropped,
        // so a grounded pickup can never snap up to an overhang above it. (This is
        // the production filter the old winding-agnostic version accepted.)
        var ceilingField = PsxTerrainHeightField.BuildFromLevel(SingleQuadLevel(100f, reversed: true));
        Assert.True(ceilingField.IsEmpty);
    }

    // A one-object level whose single horizontal quad at native Y uses PSX STRIP
    // vertex order (0=TL,1=TR,2=BL,3=BR); the default winding is floor-facing
    // (BuildFromLevel's (0,2,1)+(1,2,3) triangulation => ny<0), 'reversed' flips it
    // to a ceiling.
    private static PsxMeshFile SingleQuadLevel(float y, bool reversed)
    {
        var xz = reversed
            ? new[] { (0f, 0f), (10f, 0f), (0f, 10f), (10f, 10f) }
            : new[] { (0f, 10f), (10f, 10f), (0f, 0f), (10f, 0f) };
        var mesh = new PsxMesh
        {
            Vertices = xz.Select(p => new PsxVertex { X = p.Item1, Y = y, Z = p.Item2 }).ToList(),
            Normals = [],
            Faces = [new PsxFace { Index0 = 0, Index1 = 1, Index2 = 2, Index3 = 3, IsQuad = true }],
        };
        return new PsxMeshFile
        {
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes = [mesh],
            MeshNameHashes = [0u],
            TextureHashes = [],
            Version = 4,
            TranslationDivisor = 1f,
        };
    }
}
