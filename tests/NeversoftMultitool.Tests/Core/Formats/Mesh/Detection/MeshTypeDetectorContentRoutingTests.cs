using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Detection;

/// <summary>
///     Content-driven routing: the three newly-routed extensions, and the
///     ambiguity that decides the bare-.skin ladder order.
/// </summary>
public class MeshTypeDetectorContentRoutingTests
{
    /// <summary>
    ///     A THAW PS2 skin whose object/mesh counts are all 1 — which makes its
    ///     first three u32s (1,1,1), the ENTIRE signature XbxSceneFile.IsXbxScene
    ///     tests. ~32% of PS2-build bare .skin files look like this, so a ladder
    ///     that asks IsXbxScene before IsThawPs2Skin misroutes them wholesale.
    /// </summary>
    private static byte[] BuildAmbiguousThawPs2Skin()
    {
        // 32B header + 1 object (8B) + 1 entry (64B) = 104 bytes minimum.
        var data = new byte[512];
        BitConverter.GetBytes(1u).CopyTo(data, 0); // numObjects   (also version triple [0])
        BitConverter.GetBytes(1u).CopyTo(data, 4); // totalMeshes1 (also version triple [1])
        BitConverter.GetBytes(1u).CopyTo(data, 8); // totalMeshes2 (also version triple [2])
        BitConverter.GetBytes(64u).CopyTo(data, 12); // dataSize, must satisfy dataSize + 16 <= fileSize
        BitConverter.GetBytes(1.0f).CopyTo(data, 0x1C); // bounding-sphere radius, must be > 0
        BitConverter.GetBytes(0xAABBCCDDu).CopyTo(data, 0x20); // object[0] checksum
        BitConverter.GetBytes(0u).CopyTo(data, 0x28 + 44); // entry[0] owner = 0 (unowned, valid)
        return data;
    }

    [Fact]
    public void BareSkin_AmbiguousThawPs2Skin_IsGenuinelyAmbiguous()
    {
        var data = BuildAmbiguousThawPs2Skin();

        // Both predicates fire — this is the premise of the ordering rule, so pin
        // it. If a future change makes IsXbxScene stricter, this test tells us the
        // ordering constraint has been relaxed rather than silently rotting.
        Assert.True(XbxSceneFile.IsXbxScene(data));
        Assert.True(ThawPs2SkinFile.IsThawPs2Skin(data, data.Length));
    }

    [Fact]
    public void BareSkin_AmbiguousThawPs2Skin_RoutesToPs2Scene_NotXbxScene()
    {
        var route = MeshTypeDetector.DetectFromBytes("skater.skin", BuildAmbiguousThawPs2Skin(), 512);

        Assert.Equal(MeshFileKind.Ps2Scene, route.Kind);
        Assert.Equal(Ps2SceneSubFormat.ThawSkin, route.Ps2SubFormat);
    }

    [Fact]
    public void SuffixedSkinPs2_ResolvesASubFormat_NeverNone()
    {
        // MeshModelParser.ParsePs2Scene hard-dispatches on the sub-format and
        // throws on None, so a supported Ps2Scene route must always carry one.
        var route = MeshTypeDetector.DetectFromBytes("skater.skin.ps2", BuildAmbiguousThawPs2Skin(), 512);

        Assert.Equal(MeshFileKind.Ps2Scene, route.Kind);
        Assert.NotEqual(Ps2SceneSubFormat.None, route.Ps2SubFormat);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void BareCol_SupportedVersion_RoutesToCollision(int version)
    {
        var data = new byte[32];
        BitConverter.GetBytes(version).CopyTo(data, 0);

        var route = MeshTypeDetector.DetectFromBytes("6F980DC3.col", data, data.Length);

        Assert.Equal(MeshFileKind.Collision, route.Kind);
        Assert.True(route.IsSupported);
        Assert.Equal($"COL Collision (v{version})", route.DisplayFormat);
    }

    [Fact]
    public void BareCol_UnsupportedVersion_IsRejectedWithTheVersionInTheReason()
    {
        var data = new byte[32];
        BitConverter.GetBytes(99).CopyTo(data, 0);

        var route = MeshTypeDetector.DetectFromBytes("mission.col", data, data.Length);

        Assert.False(route.IsSupported);
        Assert.Contains("99", route.UnsupportedReason);
    }

    [Fact]
    public void Dff_ClumpChunk_RoutesToRenderWareDff()
    {
        var data = new byte[16];
        BitConverter.GetBytes(0x0010u).CopyTo(data, 0);

        var route = MeshTypeDetector.DetectFromBytes("Hawk.dff", data, data.Length);

        Assert.Equal(MeshFileKind.RenderWareDff, route.Kind);
        Assert.True(route.IsSupported);
    }

    [Fact]
    public void Dff_WrongChunk_IsRejected()
    {
        var data = new byte[16];
        BitConverter.GetBytes(0x1234u).CopyTo(data, 0);

        var route = MeshTypeDetector.DetectFromBytes("Hawk.dff", data, data.Length);

        Assert.False(route.IsSupported);
    }

    [Fact]
    public void Skn_AndDff_RouteIdentically()
    {
        var data = new byte[16];
        BitConverter.GetBytes(0x0010u).CopyTo(data, 0);

        Assert.Equal(
            MeshTypeDetector.DetectFromBytes("Hawk.skn", data, data.Length).Kind,
            MeshTypeDetector.DetectFromBytes("Hawk.dff", data, data.Length).Kind);
    }

    [Fact]
    public void DetectFromBytes_TruncatedBuffer_NeverYieldsAWrongKind()
    {
        // A short read may downgrade to None, but must never claim a kind the
        // full file would not support.
        foreach (var length in new[] { 0, 1, 3, 8, 11 })
        {
            var route = MeshTypeDetector.DetectFromBytes("skater.skin", new byte[length], length);
            Assert.False(route.IsSupported);
        }
    }

    [Fact]
    public void UnrelatedExtension_IsNotAMesh()
    {
        var route = MeshTypeDetector.DetectByName("song.wav");

        Assert.Equal(MeshFileKind.None, route.Kind);
        Assert.False(route.RequiresContentProbe);
        Assert.Contains(".wav", route.UnsupportedReason);
    }
}
