using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Tests.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Vid1;

/// <summary>
///     Anchor-based seeking must be invisible to the output: seeking to any
///     ordinal and decoding forward has to reproduce the exact frames a linear
///     decode produced. atvi.vid is the smallest real GC fixture (1.8 MB).
/// </summary>
public sealed class Vid1SeekTests(TestPaths paths)
{
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const int LinearFrames = 40;

    [Fact]
    public void SeekAcrossLaterIntraWithBFrames_PreservesTheLinearPresentationTail()
    {
        var data = Vid1VideoTestBuilder.CreateVideoVid1(
            width: 16,
            height: 16,
            frames:
            [
                new Vid1SyntheticVideoFrameSpec(0x2107, PreambleClass: 0),
                new Vid1SyntheticVideoFrameSpec(0x4014, PreambleClass: 1),
                new Vid1SyntheticVideoFrameSpec(0x8046, PreambleClass: 2),
                new Vid1SyntheticVideoFrameSpec(0x2107, PreambleClass: 0),
                new Vid1SyntheticVideoFrameSpec(0x8046, PreambleClass: 2),
                new Vid1SyntheticVideoFrameSpec(0x4014, PreambleClass: 1)
            ]);
        Assert.True(Vid1VideoFile.TryParse(data, "synthetic.vid", out var file, out var error), error);

        var expected = DecodeRemaining(new Vid1BgraPresentationFrameProvider(file!, true), file);
        Assert.Equal([0, 2, 1, 4, 3, 5], expected.Select(static frame => frame.FrameIndex));

        foreach (var target in new[] { 3, 4, 5 })
        {
            var provider = new Vid1BgraPresentationFrameProvider(file, true);
            Assert.Equal(target, provider.SeekToEmissionOrdinal(target));

            var actual = DecodeRemaining(provider, file);
            Assert.Equal(expected.Count - target, actual.Count);
            for (var index = 0; index < actual.Count; index++)
            {
                Assert.Equal(expected[target + index].FrameIndex, actual[index].FrameIndex);
                Assert.Equal(expected[target + index].Bgra, actual[index].Bgra);
            }
        }
    }

    [CorpusFact]
    public void SeekToEmissionOrdinal_ReproducesLinearDecodeExactly()
    {
        var vidPath = paths.FindSampleFile(ThawGcBuild, "atvi.vid");
        Assert.SkipWhen(vidPath is null, "THAW GC atvi.vid sample not found");

        Assert.True(Vid1VideoFile.TryParse(vidPath!, out var file, out var error), error);

        var provider = new Vid1BgraPresentationFrameProvider(file!, true);
        var frameBytes = file!.Width * file.Height * 4;
        var buffer = new byte[frameBytes];

        // Linear pass: record a hash per presentation ordinal.
        var linearHashes = new List<byte[]>(LinearFrames);
        for (var i = 0; i < LinearFrames; i++)
        {
            Assert.True(provider.TryDecodeNextFrame(buffer, out _), $"linear decode ended early at {i}");
            linearHashes.Add(SHA256.HashData(buffer));
        }

        // Backward, forward, and to-start seeks must all land on identical pixels.
        foreach (var target in new[] { 10, 35, 0, 22 })
        {
            var resumed = provider.SeekToEmissionOrdinal(target);
            Assert.Equal(target, resumed);

            for (var i = target; i < Math.Min(target + 3, LinearFrames); i++)
            {
                Assert.True(provider.TryDecodeNextFrame(buffer, out _), $"decode after seek ended early at {i}");
                Assert.Equal(linearHashes[i], SHA256.HashData(buffer));
            }
        }
    }

    [CorpusFact]
    public void SeekViaAnchors_PastFirstCaptureOrdinal_ReproducesLinearDecodeExactly()
    {
        var vidPath = paths.FindSampleFile(ThawGcBuild, "atvi.vid");
        Assert.SkipWhen(vidPath is null, "THAW GC atvi.vid sample not found");

        Assert.True(Vid1VideoFile.TryParse(vidPath!, out var file, out var error), error);

        // Anchor captures are suppressed during the opening seconds (startup
        // stutter fix), so the 40-frame test above never builds one — decode
        // past the first capture (ordinal 90) plus a stride (30) so seeks here
        // exercise the anchor restore path.
        const int linearFrames = Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal + 50;
        Assert.SkipWhen(file!.FrameCount <= linearFrames, "atvi.vid shorter than expected");

        var provider = new Vid1BgraPresentationFrameProvider(file, true);
        var buffer = new byte[file.Width * file.Height * 4];

        var linearHashes = new List<byte[]>(linearFrames);
        for (var i = 0; i < linearFrames; i++)
        {
            Assert.True(provider.TryDecodeNextFrame(buffer, out _), $"linear decode ended early at {i}");
            linearHashes.Add(SHA256.HashData(buffer));
        }

        // 95 and 125 land just past the anchors at 90/120; 10 exercises the
        // below-first-anchor fallback (intra-frame restart / stream reset).
        foreach (var target in new[] { 95, 125, 10 })
        {
            var resumed = provider.SeekToEmissionOrdinal(target);
            Assert.Equal(target, resumed);

            for (var i = target; i < Math.Min(target + 3, linearFrames); i++)
            {
                Assert.True(provider.TryDecodeNextFrame(buffer, out _), $"decode after seek ended early at {i}");
                Assert.Equal(linearHashes[i], SHA256.HashData(buffer));
            }
        }
    }

    [CorpusFact]
    public void SeekPastEnd_ReportsEndOfStream()
    {
        var vidPath = paths.FindSampleFile(ThawGcBuild, "atvi.vid");
        Assert.SkipWhen(vidPath is null, "THAW GC atvi.vid sample not found");

        Assert.True(Vid1VideoFile.TryParse(vidPath!, out var file, out _));

        var provider = new Vid1BgraPresentationFrameProvider(file!, true);
        Assert.Equal(-1, provider.SeekToEmissionOrdinal(int.MaxValue));
    }

    private static List<(int FrameIndex, byte[] Bgra)> DecodeRemaining(
        Vid1BgraPresentationFrameProvider provider,
        Vid1VideoFile file)
    {
        var frames = new List<(int FrameIndex, byte[] Bgra)>();
        var buffer = new byte[file.Width * file.Height * 4];
        while (provider.TryDecodeNextFrame(buffer, out var frameIndex))
            frames.Add((frameIndex, buffer.ToArray()));

        return frames;
    }
}
