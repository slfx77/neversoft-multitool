using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

public class Ps2GsVramTests
{
    [Theory]
    [InlineData(0u, 5u, 320, 256, 0x01u)]   // THAW dump 0290: FBP=13632, FBW=5, PSMCT24 framebuffer.
    [InlineData(0u, 10u, 640, 448, 0x00u)]  // Full-frame PSMCT32 main framebuffer.
    [InlineData(0u, 1u, 4, 4, 0x31u)]       // Tiny PSMZ24 region (matches the depth test).
    public void WritePixelThenReadRectPSMCT32_RoundTrips(uint fbp, uint fbw, int width, int height, uint psm)
    {
        // Regression: when the game writes a framebuffer via per-pixel WritePixel calls (the
        // path WriteFramebufferPixel uses) and then samples it back via ReadRectPSMCT32 (the
        // path ThawZoneTexVramSupport.DecodeFromTex0 / ReadFramebufferRgba use for sampled
        // textures with framebuffer provenance), the GS page-block-column swizzle must be
        // symmetric: every (x, y) must round-trip exactly. A skew here would manifest as
        // "ghost silhouette" scramble in framebuffer-feedback dumps even when classifier
        // FBW == TBW. THAW dump 0290 hits exactly this case at FBW=5 PSMCT24.
        var vram = new Ps2GsVram();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (uint)((y * 31u + x * 7u + 1u) & 0x00FFFFFFu); // 24-bit pattern fits PSMCT24/Z24.
                vram.WritePixel(fbp, fbw, psm, x, y, (byte)v, (byte)(v >> 8), (byte)(v >> 16), 0x40);
            }
        }

        var rgba = vram.ReadRectPSMCT32(fbp, fbw, width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var expected = (uint)((y * 31u + x * 7u + 1u) & 0x00FFFFFFu);
                Assert.Equal((byte)expected, rgba[i]);
                Assert.Equal((byte)(expected >> 8), rgba[i + 1]);
                Assert.Equal((byte)(expected >> 16), rgba[i + 2]);
            }
        }
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(128, 128)]
    public void WritePsmct32ThenReadPsmt4_MatchesVerifiedConv4to32Mapping(int width, int height)
    {
        var linear = BuildLinearPsmt4(width, height);
        var swizzled = SwizzlePsmt4(linear, width, height, "BuildConv4to32Mapping");

        var vram = new Ps2GsVram();
        vram.WriteRectPSMCT32(0, (uint)Math.Max(width / 128, 1), width / 2, height / 4, swizzled);

        var decoded = vram.ReadTexturePSMT4(0, (uint)Math.Max(width / 64, 1), width, height);

        Assert.Equal(linear, decoded);
    }

    [Theory]
    [InlineData(32, 64)]
    [InlineData(128, 32)]
    public void WritePsmct16ThenReadPsmt4_MatchesVerifiedConv4to16Mapping(int width, int height)
    {
        var linear = BuildLinearPsmt4(width, height);
        var swizzled = SwizzlePsmt4(linear, width, height, "BuildConv4to16Mapping");

        var vram = new Ps2GsVram();
        vram.WriteRectPSMCT16(0, (uint)Math.Max(width / 128, 1), width / 2, height / 2, swizzled);

        var decoded = vram.ReadTexturePSMT4(0, (uint)Math.Max(width / 64, 1), width, height);

        Assert.Equal(linear, decoded);
    }

    [Theory]
    [InlineData(128, 64)]
    [InlineData(256, 256)]
    public void WritePsmct32ThenReadPsmt8_MatchesVerifiedConv8to32Mapping(int width, int height)
    {
        var linear = BuildLinearPsmt8(width, height);
        var swizzled = SwizzlePsmt8(linear, width, height);

        var vram = new Ps2GsVram();
        vram.WriteRectPSMCT32(0, (uint)Math.Max(width / 128, 1), width / 2, height / 2, swizzled);

        var decoded = vram.ReadTexturePSMT8(0, (uint)Math.Max(width / 64, 1), width, height);

        Assert.Equal(linear, decoded);
    }

    [Theory]
    [InlineData(64, 64, "2031")]
    [InlineData(128, 128, "2301")]
    public void WritePsmct32ThenReadPsmt4_WithGifQwordOrder_MatchesExplicitPermutation(
        int width, int height, string gifQwordOrderText)
    {
        Assert.True(Ps2GifQwordWordOrder.TryParse(gifQwordOrderText, out var gifQwordWordOrder));

        var linear = BuildLinearPsmt4(width, height);
        var swizzled = SwizzlePsmt4(linear, width, height, "BuildConv4to32Mapping");
        var expectedPayload = PermuteQwordWords(swizzled, gifQwordWordOrder);

        var expectedVram = new Ps2GsVram();
        expectedVram.WriteRectPSMCT32(0, (uint)Math.Max(width / 128, 1), width / 2, height / 4, expectedPayload);

        var actualVram = new Ps2GsVram(gifQwordWordOrder);
        actualVram.WriteRectPSMCT32(0, (uint)Math.Max(width / 128, 1), width / 2, height / 4, swizzled);

        var expected = expectedVram.ReadTexturePSMT4(0, (uint)Math.Max(width / 64, 1), width, height);
        var actual = actualVram.ReadTexturePSMT4(0, (uint)Math.Max(width / 64, 1), width, height);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(32, 64, "2031")]
    [InlineData(128, 32, "2301")]
    public void WritePsmct16ThenReadPsmt4_WithGifQwordOrder_MatchesExplicitPermutation(
        int width, int height, string gifQwordOrderText)
    {
        Assert.True(Ps2GifQwordWordOrder.TryParse(gifQwordOrderText, out var gifQwordWordOrder));

        var linear = BuildLinearPsmt4(width, height);
        var swizzled = SwizzlePsmt4(linear, width, height, "BuildConv4to16Mapping");
        var expectedPayload = PermuteQwordWords(swizzled, gifQwordWordOrder);

        var expectedVram = new Ps2GsVram();
        expectedVram.WriteRectPSMCT16(0, (uint)Math.Max(width / 128, 1), width / 2, height / 2, expectedPayload);

        var actualVram = new Ps2GsVram(gifQwordWordOrder);
        actualVram.WriteRectPSMCT16(0, (uint)Math.Max(width / 128, 1), width / 2, height / 2, swizzled);

        var expected = expectedVram.ReadTexturePSMT4(0, (uint)Math.Max(width / 64, 1), width, height);
        var actual = actualVram.ReadTexturePSMT4(0, (uint)Math.Max(width / 64, 1), width, height);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WritePsmt4_UsesCanonicalColumnGroupOffsets()
    {
        var linear = new byte[32 * 16 / 2];
        linear[4 * 32 / 2] = 0x0A;

        var vram = new Ps2GsVram();
        vram.WriteRect(0, 2, Ps2GsVram.PSMT4, 32, 16, linear);

        var words = vram.ReadRawBlockWords(0, 64);

        Assert.Equal(0xAu, words[24] & 0xF);
        Assert.Equal(0u, words[8]);
    }

    [Fact]
    public void WritePsmt8_UsesCanonicalColumnGroupOffsets()
    {
        var linear = new byte[16 * 16];
        linear[4 * 16] = 0xAB;

        var vram = new Ps2GsVram();
        vram.WriteRect(0, 2, Ps2GsVram.PSMT8, 16, 16, linear);

        var words = vram.ReadRawBlockWords(0, 64);

        Assert.Equal(0xABu, words[24] & 0xFF);
        Assert.Equal(0u, words[8]);
    }

    [Fact]
    public void ReadPixelRgba_Psmct16_ExpandsAlphaViaTexa()
    {
        // Regression: PSMCT16/16S stores only 1 bit of alpha. PS2 GS spec says framebuffer
        // Cd reads expand that bit via TEXA — TA1 when alpha-bit=1, TA0 when alpha-bit=0,
        // with AEM=1 forcing alpha=0 for fully-black non-alpha pixels. Without this, the
        // ABE blend path sees binary 0/255 alpha for PSMCT16 framebuffers (HUD scanout
        // at FBP=4480, post-fx layers), causing wrong blend factors. The texa=0 fallback
        // preserves the old 0/255 behaviour for callers that don't know about TEXA.
        var vram = new Ps2GsVram();

        // TEXA register layout: TA0 at bits 0..7, AEM at bit 15, TA1 at bits 32..39.
        const ulong texaTa0_00_Ta1_80 = 0x80UL << 32;                       // TA0=0x00, TA1=0x80
        const ulong texaTa0_80_Ta1_FF_AemSet = (0xFFUL << 32) | (1UL << 15) | 0x80UL;

        // Alpha-bit=1 (a >= 128) -> read with TEXA -> alpha = TA1.
        vram.WritePixel(0u, 5u, Ps2GsVram.PSMCT16, 7, 11, 0x40, 0x20, 0x10, 0xFF);
        var pBit1 = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT16, 7, 11, texaTa0_00_Ta1_80);
        Assert.Equal((byte)0x80, pBit1.A);

        // Alpha-bit=0 (a < 128) -> read with same TEXA -> alpha = TA0 = 0.
        vram.WritePixel(0u, 5u, Ps2GsVram.PSMCT16, 7, 12, 0x40, 0x20, 0x10, 0x00);
        var pBit0 = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT16, 7, 12, texaTa0_00_Ta1_80);
        Assert.Equal((byte)0x00, pBit0.A);

        // AEM rule: alpha-bit=0 AND RGB=0 -> alpha forced to 0 regardless of TA0.
        vram.WritePixel(0u, 5u, Ps2GsVram.PSMCT16, 7, 13, 0x00, 0x00, 0x00, 0x00);
        var pAem = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT16, 7, 13, texaTa0_80_Ta1_FF_AemSet);
        Assert.Equal((byte)0x00, pAem.A);

        // AEM rule does NOT fire when alpha-bit=1, even with RGB=0 -> alpha = TA1.
        vram.WritePixel(0u, 5u, Ps2GsVram.PSMCT16, 7, 14, 0x00, 0x00, 0x00, 0xFF);
        var pAemBit1 = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT16, 7, 14, texaTa0_80_Ta1_FF_AemSet);
        Assert.Equal((byte)0xFF, pAemBit1.A);

        // texa=0 fallback: behaviour matches the pre-fix 0/255 contract for non-blend callers.
        var pFallbackBit1 = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT16, 7, 11);
        Assert.Equal((byte)0xFF, pFallbackBit1.A);
        var pFallbackBit0 = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT16, 7, 12);
        Assert.Equal((byte)0x00, pFallbackBit0.A);
    }

    [Fact]
    public void ReadPixelRgba_Psmct24_ReturnsPreviousPsmct32Alpha()
    {
        // Regression: PSMCT24 writes mask the alpha byte (fbmsk | 0xFF000000u), preserving
        // whatever a prior PSMCT32 write left in VRAM. Bloom-feedback passes in THAW alias
        // the same FBP between PSMCT32 and PSMCT24 (e.g. FBP=13632 in the canonical capture)
        // and depend on subsequent Cd reads returning the real mid-tone alpha, not a hardcoded
        // 128. Hardcoded 128 collapsed our FBP=0 alpha distribution to bimodal 0/128 and
        // over-amplified the bloom downsample's `Cs * Cs.A` math by up to 2x.
        var vram = new Ps2GsVram();

        // Seed: write PSMCT32 with alpha=0x40 (a mid-tone the bloom math depends on).
        vram.WritePixel(0u, 5u, Ps2GsVram.PSMCT32, 17, 23, 0x11, 0x22, 0x33, 0x40);

        // Overwrite RGB via PSMCT24 — the WritePixel PSMCT24 case ORs 0xFF000000 into the
        // mask so the supplied alpha argument is ignored; 0x40 stays in VRAM.
        vram.WritePixel(0u, 5u, 0x01u, 17, 23, 0x44, 0x55, 0x66, 0xFF);

        // Cd read from the PSMCT24 view must surface the preserved 0x40, not 0x80.
        var psmct24 = vram.ReadPixelRgba(0u, 5u, 0x01u, 17, 23);
        Assert.Equal((byte)0x44, psmct24.R);
        Assert.Equal((byte)0x55, psmct24.G);
        Assert.Equal((byte)0x66, psmct24.B);
        Assert.Equal((byte)0x40, psmct24.A);

        // PSMCT32 sees the same word — sanity-check the RGB update landed.
        var psmct32 = vram.ReadPixelRgba(0u, 5u, Ps2GsVram.PSMCT32, 17, 23);
        Assert.Equal((byte)0x40, psmct32.A);
    }

    private static byte[] BuildLinearPsmt4(int width, int height)
    {
        var totalNibbles = width * height;
        var linear = new byte[totalNibbles / 2];

        for (var nibbleIndex = 0; nibbleIndex < totalNibbles; nibbleIndex++)
        {
            var byteIndex = nibbleIndex / 2;
            var shift = (nibbleIndex & 1) * 4;
            linear[byteIndex] = (byte)((linear[byteIndex] & ~(0xF << shift)) | ((nibbleIndex & 0xF) << shift));
        }

        return linear;
    }

    private static byte[] BuildLinearPsmt8(int width, int height)
    {
        var linear = new byte[width * height];
        for (var i = 0; i < linear.Length; i++)
            linear[i] = (byte)(i & 0xFF);
        return linear;
    }

    private static byte[] SwizzlePsmt4(byte[] linear, int width, int height, string mappingMethod)
    {
        var totalNibbles = width * height;
        var mapping = GetPrivateMapping(mappingMethod, width, height);
        var swizzled = new byte[totalNibbles / 2];

        for (var outputNibble = 0; outputNibble < mapping.Length; outputNibble++)
        {
            var linearNibble = mapping[outputNibble];
            if (linearNibble < 0 || linearNibble >= totalNibbles)
                continue;

            var srcByte = linearNibble / 2;
            var srcShift = (linearNibble & 1) * 4;
            var value = (linear[srcByte] >> srcShift) & 0xF;

            var dstByte = outputNibble / 2;
            var dstShift = (outputNibble & 1) * 4;
            swizzled[dstByte] = (byte)((swizzled[dstByte] & ~(0xF << dstShift)) | (value << dstShift));
        }

        return swizzled;
    }

    private static byte[] SwizzlePsmt8(byte[] linear, int width, int height)
    {
        var mapping = GetPrivateMapping("BuildConv8to32Mapping", width, height);
        var swizzled = new byte[linear.Length];

        for (var outputByte = 0; outputByte < mapping.Length; outputByte++)
        {
            var linearByte = mapping[outputByte];
            if (linearByte < 0 || linearByte >= linear.Length)
                continue;

            swizzled[outputByte] = linear[linearByte];
        }

        return swizzled;
    }

    private static byte[] PermuteQwordWords(byte[] data, Ps2GifQwordWordOrder gifQwordWordOrder)
    {
        if (gifQwordWordOrder.IsIdentity)
            return data.ToArray();

        var permuted = new byte[data.Length];
        var limit = data.Length - data.Length % 16;

        for (var qwordBase = 0; qwordBase < limit; qwordBase += 16)
        {
            for (var destinationWord = 0; destinationWord < 4; destinationWord++)
            {
                var sourceWord = gifQwordWordOrder.MapWord(destinationWord);
                Buffer.BlockCopy(
                    data, qwordBase + sourceWord * 4,
                    permuted, qwordBase + destinationWord * 4,
                    4);
            }
        }

        Array.Copy(data, limit, permuted, limit, data.Length - limit);
        return permuted;
    }

    private static int[] GetPrivateMapping(string methodName, int width, int height)
    {
        return methodName switch
        {
            "BuildConv4to32Mapping" => Ps2TexSwizzlePageMappingBuilder.BuildConv4to32Mapping(width, height),
            "BuildConv4to16Mapping" => Ps2TexSwizzleVramMappingBuilder.BuildConv4to16Mapping(width, height),
            "BuildConv8to32Mapping" => Ps2TexSwizzlePageMappingBuilder.BuildConv8to32Mapping(width, height),
            _ => throw new InvalidOperationException($"Unsupported mapping method: {methodName}")
        };
    }
}