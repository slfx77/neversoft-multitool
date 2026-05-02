using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public class PsxAnimDecoderTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";

    [Fact]
    public void Decode_CarnageAnim1_MatchesDiagnosticByteBudget()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "carnage.psx");
        Assert.SkipWhen(path == null, "carnage.psx not found in sample builds");

        var data = File.ReadAllBytes(path!);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        Assert.NotNull(animFile);

        // Anim 1 is the byte-perfect 1199-byte case from the diagnostic
        // (1370 ≈ 1378 8-byte alignment). 30 frames × 19 bones × 6 channels.
        var entry = animFile.Entries[1];
        Assert.Equal(30, entry.FrameCount);

        var slice = animFile.Pool.Span[entry.PoolOffset..];
        var animation = PsxAnimDecoder.Decode(slice, psxFile.Objects.Count, entry.FrameCount, out var consumed);

        Assert.Equal(30, animation.FrameCount);
        Assert.Equal(19, animation.BoneCount);
        Assert.True(consumed > 0);
        // The codec uses 1199 bytes for this anim per the diagnostic.
        Assert.InRange(consumed, 1100, 1400);
    }

    [Fact]
    public void Decode_RejectsExhaustedStream()
    {
        // Empty stream: should throw because we can't even read the first header byte.
        Assert.Throws<InvalidDataException>(
            () => PsxAnimDecoder.Decode([], boneCount: 1, frameCount: 1));
    }

    [Fact]
    public void IsRotationAnimated_ReturnsTrueForNonZeroChannels()
    {
        var channels = new short[1, 6, 4];
        channels[0, 1, 2] = 100; // Ry has a non-zero sample
        var anim = new PsxAnimation { FrameCount = 4, BoneCount = 1, Channels = channels };

        Assert.True(anim.IsRotationAnimated(0));
        Assert.False(anim.IsTranslationAnimated(0));
    }

    [Fact]
    public void GetBoneTranslation_AppliesS16Over4096Scale()
    {
        var channels = new short[1, 6, 1];
        channels[0, 3, 0] = 4096; // Tx = 1.0 PSX world units
        channels[0, 4, 0] = -2048; // Ty = -0.5
        channels[0, 5, 0] = 8192;  // Tz = 2.0
        var anim = new PsxAnimation { FrameCount = 1, BoneCount = 1, Channels = channels };

        var t = anim.GetBoneTranslation(0, 0);
        Assert.Equal(1.0f, t.X, 4);
        Assert.Equal(-0.5f, t.Y, 4);
        Assert.Equal(2.0f, t.Z, 4);
    }
}
