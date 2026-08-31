using NeversoftMultitool.Core.Formats.Rle;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

/// <summary>
///     Authoring TIFFs shipped on disc (PG Wii 2,099; 8,531 corpus-wide with
///     THAW GC and THAW PC). 1,487 of PG Wii's 2,003 non-empty files are MIP
///     CHAINS, which ImageSharp cannot load in one <c>Image</c> — differently
///     sized pages throw "Images with different sizes are not supported", with
///     the default options, with an explicit TiffDecoder, and with
///     <c>MaxFrames = 2</c> alike. <see cref="TiffMipChain" /> sidesteps that by
///     retargeting the header's IFD pointer at one level and cutting that
///     level's chain link; TIFF offsets are absolute, so nothing else moves.
/// </summary>
public class TiffMipChainTests(TestPaths paths)
{
    private const string PgWiiBuild = "Tony Hawk's Proving Ground (2007-10-16, Wii - Final)";

    [Fact]
    public void IsTiff_AcceptsBothByteOrdersAndRejectsOthers()
    {
        Assert.True(TiffMipChain.IsTiff([0x49, 0x49, 42, 0, 8, 0, 0, 0]));
        Assert.True(TiffMipChain.IsTiff([0x4D, 0x4D, 0, 42, 0, 0, 0, 8]));

        Assert.False(TiffMipChain.IsTiff([0x49, 0x49, 43, 0, 8, 0, 0, 0])); // wrong magic number
        Assert.False(TiffMipChain.IsTiff("BM\0\0\0\0\0\0"u8));
        Assert.False(TiffMipChain.IsTiff([0x49, 0x49]));
        Assert.False(TiffMipChain.IsTiff(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void GetLevelOffsets_NonTiffOrTruncated_IsEmpty()
    {
        // The corpus also ships 96 zero-byte .tif files.
        Assert.Empty(TiffMipChain.GetLevelOffsets([]));
        Assert.Empty(TiffMipChain.GetLevelOffsets(new byte[4]));
        Assert.Empty(TiffMipChain.GetLevelOffsets("not a tiff at all"u8.ToArray()));
    }

    [Fact]
    public void BitmapFile_RecognizesTiffAsSelfDescribedStandardBitmap()
    {
        Assert.True(BitmapFile.IsTiffExtension("art.tif"));
        Assert.True(BitmapFile.IsTiffExtension("art.TIFF"));
        Assert.True(BitmapFile.IsStandardExtension("art.tif"));
        Assert.True(BitmapFile.IsSupportedExtension("art.tif"));
        // TIFF carries its own dimensions, so the width override must not apply.
        Assert.True(BitmapFile.HasSelfDescribedDimensions("art.tif"));
        Assert.False(BitmapFile.IsTiffExtension("art.tga"));
    }

    /// <summary>
    ///     A known five-level banner: every level decodes and the chain halves
    ///     exactly. The top level is legible "nixon" artwork — checked by eye,
    ///     because dimension arithmetic alone cannot tell a correct decode from
    ///     a scrambled one.
    /// </summary>
    [CorpusFact]
    public void RealMipChain_DecodesEveryLevelAndHalvesExactly()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(PgWiiBuild, "Houses_Nixon.tif");
        Assert.SkipWhen(file == null, "Houses_Nixon.tif not present");

        var data = File.ReadAllBytes(file!);
        Assert.Equal(5, TiffMipChain.GetLevelCount(data));
        Assert.Equal(5, BitmapFile.GetStandardLevelCount(data, "Houses_Nixon.tif"));

        var expected = new[] { (128, 128), (64, 64), (32, 32), (16, 16), (8, 8) };
        for (var level = 0; level < expected.Length; level++)
        {
            using var image = BitmapFile.DecodeStandardLevel(data, "Houses_Nixon.tif", level);
            Assert.Equal(expected[level], (image.Width, image.Height));
        }
    }

    /// <summary>
    ///     A single-page TIFF must report exactly one level and hand back the
    ///     original bytes rather than a rewritten copy.
    /// </summary>
    [CorpusFact]
    public void SinglePageTiff_ReportsOneLevelAndIsReturnedUnchanged()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, PgWiiBuild);
        Assert.SkipWhen(!Directory.Exists(root), "PG Wii build not present");

        var single = Directory.EnumerateFiles(root, "*.tif", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes)
            .FirstOrDefault(data => TiffMipChain.GetLevelCount(data) == 1);
        Assert.SkipWhen(single == null, "No single-page TIFF present");

        Assert.Same(single, TiffMipChain.ExtractLevel(single!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TiffMipChain.ExtractLevel(single!, 1));
    }

    /// <summary>
    ///     Whole-build sweep: every stored level of every shipped TIFF decodes,
    ///     and every multi-page chain is an exact floor-halved mip chain — which
    ///     is what justifies exporting the extra pages as <c>_mipN.png</c>
    ///     rather than as unrelated pages.
    /// </summary>
    [CorpusFact]
    public void PgWiiTiffCorpus_EveryLevelDecodesAndEveryChainIsAnExactMipChain()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, PgWiiBuild);
        Assert.SkipWhen(!Directory.Exists(root), "PG Wii build not present");

        var files = Directory.EnumerateFiles(root, "*.tif", SearchOption.AllDirectories)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2099, files.Count);

        var withIfds = 0;
        var multiPage = 0;
        var framesDecoded = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var levels = TiffMipChain.GetLevelCount(data);
            if (levels == 0) continue; // 96 zero-byte files
            withIfds++;
            if (levels > 1) multiPage++;

            var previous = (Width: 0, Height: 0);
            for (var level = 0; level < levels; level++)
            {
                try
                {
                    using var stream = new MemoryStream(TiffMipChain.ExtractLevel(data, level), false);
                    using var image = TiffDecoder.Instance.Decode<Rgba32>(
                        new DecoderOptions { MaxFrames = 1 }, stream);
                    framesDecoded++;

                    if (level > 0)
                    {
                        Assert.Equal(Math.Max(1, previous.Width / 2), image.Width);
                        Assert.Equal(Math.Max(1, previous.Height / 2), image.Height);
                    }

                    previous = (image.Width, image.Height);
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)} level {level}: {ex.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} level failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(2003, withIfds);
        Assert.Equal(1487, multiPage);
        Assert.Equal(7308, framesDecoded);
    }
}
