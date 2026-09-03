using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class BlendPackageManifestCollisionMetadataTests
{
    [Fact]
    public void ToDictionary_SerializesCollisionMetadata()
    {
        var dictionary = BlendPackageManifest.ToDictionary(
            new CollisionRenderMetadata(ObjectCount: 17));

        Assert.Equal(["kind", "objectCount"], dictionary.Keys.ToArray());
        Assert.Equal("collision", dictionary["kind"]);
        Assert.Equal(17, dictionary["objectCount"]);
    }

    [Fact]
    public void ToDictionary_SerializesCollisionOverlayMetadata()
    {
        var dictionary = BlendPackageManifest.ToDictionary(
            new CollisionOverlayRenderMetadata(
                CompanionName: "warehouse.col.xbx",
                ObjectCount: 23,
                TriangleCount: 456));

        Assert.Equal(
            ["kind", "companionName", "objectCount", "triangleCount"],
            dictionary.Keys.ToArray());
        Assert.Equal("collision-overlay", dictionary["kind"]);
        Assert.Equal("warehouse.col.xbx", dictionary["companionName"]);
        Assert.Equal(23, dictionary["objectCount"]);
        Assert.Equal(456, dictionary["triangleCount"]);
    }

    [Fact]
    public void ToDictionary_SerializesRwBspCollisionFlagsMetadata()
    {
        var dictionary = BlendPackageManifest.ToDictionary(
            new RwBspCollisionFlagsRenderMetadata(CollisionFlags: 0xBEEF));

        Assert.Equal(["kind", "collisionFlags"], dictionary.Keys.ToArray());
        Assert.Equal("rw_bsp_collision_flags", dictionary["kind"]);
        Assert.Equal((ushort)0xBEEF, dictionary["collisionFlags"]);
    }

    [Fact]
    public void ToDictionary_SerializesPsxCollisionFlagsMetadata()
    {
        var dictionary = BlendPackageManifest.ToDictionary(
            new PsxCollisionFlagsRenderMetadata(
                CollisionFlags: 0x1234,
                LoaderInvisible: true));

        Assert.Equal(
            ["kind", "collisionFlags", "loaderInvisible"],
            dictionary.Keys.ToArray());
        Assert.Equal("psx_collision_flags", dictionary["kind"]);
        Assert.Equal((ushort)0x1234, dictionary["collisionFlags"]);
        Assert.True(Assert.IsType<bool>(dictionary["loaderInvisible"]));
    }

    [Fact]
    public void ToDictionary_SerializesNgcCollisionMetadata()
    {
        var dictionary = BlendPackageManifest.ToDictionary(
            new NgcCollisionRenderMetadata(
                CompanionName: "school2.col.dat",
                PositionPoolKind: "scene-positions",
                ObjectCount: 31,
                TriangleCount: 789));

        Assert.Equal(
            ["kind", "companionName", "positionPoolKind", "objectCount", "triangleCount"],
            dictionary.Keys.ToArray());
        Assert.Equal("ngc-collision-binding", dictionary["kind"]);
        Assert.Equal("school2.col.dat", dictionary["companionName"]);
        Assert.Equal("scene-positions", dictionary["positionPoolKind"]);
        Assert.Equal(31, dictionary["objectCount"]);
        Assert.Equal(789, dictionary["triangleCount"]);
    }
}
