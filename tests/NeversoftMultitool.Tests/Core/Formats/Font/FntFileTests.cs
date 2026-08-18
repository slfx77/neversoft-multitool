using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Font;

namespace NeversoftMultitool.Tests.Core.Formats.Font;

/// <summary>
///     Pins the PS1-era Neversoft <c>.fnt</c> bitmap-font reader against the matched THPS2 PSX
///     decomp (<c>src/FONTTOOLS.cpp</c> <c>Font::Font(unsigned char *)</c>) and the corpus.
/// </summary>
/// <remarks>
///     Fixtures and what each guards:
///     <list type="bullet">
///         <item>
///             THPS2 retail <c>mainf.fnt</c> — the canonical paletted layout, 59 glyphs.
///             Its atlas SHA guards the whole decode chain: nibble order, row stride, the
///             odd-size pad, CLUT expansion and the <c>0x0000</c> transparency rule.
///         </item>
///         <item>
///             THPS2 prototype <c>player/s2bio.fnt</c> — exactly 74 glyphs, the full
///             <c>CharMap 2</c> set, so glyph 48 is <c>a</c> as decompiled.
///         </item>
///         <item>
///             THPS2 retail <c>s2bio.fnt</c> — 94 glyphs, where retail put <c>_</c> at 48 and
///             shifted lowercase to 49. Together with the prototype it is the evidence that the
///             character map is build-specific and must never be inferred.
///         </item>
///         <item>
///             THPS2 Dreamcast <c>LEVSEL.FNT</c> — the sole palette-less 12-byte-record file,
///             and the only one whose 4bpp pixels are HIGH nibble first. It gets its own atlas
///             SHA because pinning counts and offsets alone is what let a wrong nibble order
///             ship: swapping horizontal pixel pairs leaves every glyph readable as its letter,
///             so a visual check passes while each stroke is riddled with holes.
///         </item>
///     </list>
/// </remarks>
public sealed class FntFileTests(TestPaths paths)
{
    private const string Thps2Retail = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string Thps2Proto = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";
    private const string Thps2Dreamcast = "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)";

    [Fact]
    public void SyntheticPalettedFont_ParsesEveryStoredField()
    {
        var font = FntFile.Parse(BuildPalettedFont());

        Assert.Equal(FntLayout.PalettedWithAdvance, font.Layout);
        Assert.True(font.HasPalette);
        Assert.Equal(FntFile.PaletteEntryCount, font.Palette.Length);
        Assert.Equal(2, font.Glyphs.Length);

        var first = font.Glyphs[0];
        Assert.Equal(0, first.Index);
        Assert.Equal(8, first.Width);
        Assert.Equal(2, first.WidthUnits);
        Assert.Equal(3, first.Height);
        Assert.Equal(2, first.Baseline);
        Assert.Equal(7, first.AdvanceWidth);
        Assert.Equal(4 + 32 + 32, first.PixelDataOffset);
        Assert.Equal(12, first.PixelDataLength);

        // A negative baseline places the whole glyph below the text baseline; the corpus
        // contains -1 and -2, so the field must be read signed.
        Assert.Equal(-2, font.Glyphs[1].Baseline);
    }

    [Fact]
    public void TheTwoLayoutsUseOppositeNibbleOrders()
    {
        // Not a shared convention — each order is measured for its own layout, so pin both.
        // Row 0 of the synthetic glyph is bytes 0x10, 0x32.
        var paletted = FntFile.Parse(BuildPalettedFont());
        Assert.Equal([0, 1, 2, 3], paletted.Glyphs[0].Pixels.Take(4));

        var compact = FntFile.Parse(BuildFont(FntLayout.CompactWithoutPalette, [(2, 3, 2, 0)]));
        Assert.Equal([1, 0, 3, 2], compact.Glyphs[0].Pixels.Take(4));
    }

    [Fact]
    public void OddPixelCount_ConsumesTheLoadersTwoBytePad()
    {
        // widthUnits 1 x height 1 = 1, which is odd, so the loader advances 2*1 + 2 = 4 bytes.
        var data = BuildFont(FntLayout.PalettedWithAdvance, [(1, 1, 0, 4)]);

        var font = FntFile.Parse(data);

        Assert.Equal(4, font.Glyphs[0].PixelDataLength);
        Assert.Equal(data.Length, font.SerializedSize);
    }

    [Fact]
    public void SyntheticCompactFont_ParsesWithoutPaletteOrAdvance()
    {
        var font = FntFile.Parse(BuildFont(FntLayout.CompactWithoutPalette, [(2, 3, 2, 0)]));

        Assert.Equal(FntLayout.CompactWithoutPalette, font.Layout);
        Assert.False(font.HasPalette);
        Assert.Empty(font.Palette);
        Assert.Null(font.Glyphs[0].AdvanceWidth);
        Assert.Equal(4 + 12, font.Glyphs[0].PixelDataOffset);
    }

    [Fact]
    public void TheTwoLayoutsAreMutuallyExclusive()
    {
        // Exact end-of-file is what separates them: neither file can be read as the other.
        Assert.True(FntFile.TryParse(BuildPalettedFont(), out var paletted));
        Assert.Equal(FntLayout.PalettedWithAdvance, paletted.Layout);

        var intensity = BuildFont(FntLayout.CompactWithoutPalette, [(2, 3, 2, 0), (1, 5, -2, 0)]);
        Assert.True(FntFile.TryParse(intensity, out var parsed));
        Assert.Equal(FntLayout.CompactWithoutPalette, parsed.Layout);
    }

    [Fact]
    public void NoTruncation_CanBeMistakenForTheOriginalFont()
    {
        var data = BuildPalettedFont();
        var original = FntFile.Parse(data);

        for (var length = 0; length < data.Length; length++)
        {
            if (!FntFile.TryParse(data.AsSpan(0, length), out var truncated))
                continue;

            // Exact end-of-file means a short read can never reproduce the real glyph table.
            Assert.False(
                truncated.Layout == original.Layout
                && truncated.Glyphs.Length == original.Glyphs.Length
                && truncated.Glyphs.Zip(original.Glyphs).All(pair =>
                    pair.First.Width == pair.Second.Width
                    && pair.First.Height == pair.Second.Height),
                $"a {length}-byte truncation reproduced the original font");
        }
    }

    [Fact]
    public void ATruncatedPalettedFontCanCoincidentallySatisfyTheCompactLayout()
    {
        // Pinning a real limit rather than overclaiming. Exact end-of-file makes the two
        // layouts unambiguous across the whole shipped corpus (0 of 443 files satisfy both),
        // but it is not a proof for arbitrary bytes: truncate this synthetic paletted font to
        // 56 bytes and the 12-byte-record reading happens to land exactly on EOF. The paletted
        // layout is therefore always tried first, so an intact font is never misread; only an
        // already-damaged one can fall through.
        var truncated = BuildPalettedFont().AsSpan(0, 56).ToArray();

        Assert.True(FntFile.TryParse(truncated, out var font));
        Assert.Equal(FntLayout.CompactWithoutPalette, font.Layout);
    }

    [Fact]
    public void TrailingBytes_AreRejected()
    {
        var data = BuildPalettedFont();

        Assert.False(FntFile.TryParse([.. data, 0], out _));
        Assert.False(FntFile.TryParse([.. data, 0, 0, 0, 0], out _));
    }

    [Fact]
    public void AbsurdGlyphCount_IsRejectedWithoutAllocating()
    {
        // A four-byte file claiming 0xFFFFFFFF glyphs must fail on the bound, not on a huge array.
        Assert.False(FntFile.TryParse([0xFF, 0xFF, 0xFF, 0xFF], out _));
        Assert.False(FntFile.TryParse([0x00, 0x00, 0x00, 0x00], out _));
    }

    [Fact]
    public void NonPositiveGlyphDimensions_AreRejected()
    {
        Assert.False(FntFile.TryParse(BuildFont(FntLayout.PalettedWithAdvance, [(0, 3, 2, 7)]), out _));
        Assert.False(FntFile.TryParse(BuildFont(FntLayout.PalettedWithAdvance, [(2, 0, 2, 7)]), out _));
        Assert.False(FntFile.TryParse(BuildFont(FntLayout.PalettedWithAdvance, [(2, -3, 2, 7)]), out _));
    }

    [Fact]
    public void Parse_ThrowsOnDataThatIsNotAFont()
    {
        Assert.Throws<InvalidDataException>(() => FntFile.Parse("not a font at all"u8));
    }

    [Fact]
    public void TransparencyComesFromTheZeroClutEntryOnly()
    {
        var font = FntFile.Parse(BuildPalettedFont());
        Span<byte> rgba = stackalloc byte[4];

        // Entry 15 is 0x0000 in the synthetic palette: the PS1 GPU's "not drawn" texel.
        FntAtlasBuilder.ToRgba(font, 15, rgba);
        Assert.Equal([0, 0, 0, 0], rgba.ToArray());

        // Entry 0 is 0x8000: black with STP set. STP is not transparency, so it stays opaque.
        FntAtlasBuilder.ToRgba(font, 0, rgba);
        Assert.Equal([0, 0, 0, 255], rgba.ToArray());
    }

    [Fact]
    public void UnpalettedPixelsRenderAsCoverageOnWhite()
    {
        var font = FntFile.Parse(BuildFont(FntLayout.CompactWithoutPalette, [(2, 3, 2, 0)]));
        Span<byte> rgba = stackalloc byte[4];

        FntAtlasBuilder.ToRgba(font, 0, rgba);
        Assert.Equal([255, 255, 255, 0], rgba.ToArray());

        FntAtlasBuilder.ToRgba(font, 15, rgba);
        Assert.Equal([255, 255, 255, 255], rgba.ToArray());
    }

    [Fact]
    public void AtlasPacksEveryGlyphWithoutOverlapAndInIndexOrder()
    {
        var font = FntFile.Parse(BuildPalettedFont());

        var atlas = FntAtlasBuilder.Build(font, maxWidth: 32);

        Assert.Equal(font.Glyphs.Length, atlas.Cells.Length);
        for (var i = 0; i < atlas.Cells.Length; i++)
        {
            Assert.Equal(i, atlas.Cells[i].GlyphIndex);
            Assert.Equal(font.Glyphs[i].Width, atlas.Cells[i].Width);
            Assert.Equal(font.Glyphs[i].Height, atlas.Cells[i].Height);
            Assert.InRange(atlas.Cells[i].X + atlas.Cells[i].Width, 0, atlas.Width);
            Assert.InRange(atlas.Cells[i].Y + atlas.Cells[i].Height, 0, atlas.Height);
        }

        Assert.Equal(atlas.Width * atlas.Height * 4, atlas.Rgba.Length);
    }

    [Fact]
    public void AtlasWidensWhenASingleGlyphExceedsTheRequestedWrap()
    {
        var font = FntFile.Parse(BuildFont(FntLayout.PalettedWithAdvance, [(8, 2, 1, 32)]));

        var atlas = FntAtlasBuilder.Build(font, maxWidth: 4);

        Assert.Equal(32 + 2 * FntAtlasBuilder.DefaultPadding, atlas.Width);
    }

    [Fact]
    public void ManifestIsDeterministicAndReportsUnappliedCharacterMapByDefault()
    {
        var font = FntFile.Parse(BuildPalettedFont());
        var atlas = FntAtlasBuilder.Build(font);

        var first = FntJsonExporter.Serialize("synthetic.fnt", font, atlas, "synthetic.fnt.png");
        var second = FntJsonExporter.Serialize("synthetic.fnt", font, atlas, "synthetic.fnt.png");

        Assert.Equal(first, second);
        Assert.Contains("\"characterMapStatus\": \"notApplied\"", first, StringComparison.Ordinal);
        Assert.Contains("\"characterMapMode\": null", first, StringComparison.Ordinal);
        Assert.Contains("\"character\": null", first, StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"neversoft.bitmapFont\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestLabelsGlyphsOnlyWhenTheCallerStatesAMode()
    {
        var font = FntFile.Parse(BuildPalettedFont());
        var atlas = FntAtlasBuilder.Build(font);

        var json = FntJsonExporter.Serialize(
            "synthetic.fnt", font, atlas, "synthetic.fnt.png", FntCharacterMap.Mode.Standard);

        Assert.Contains("\"characterMapStatus\": \"appliedFromCallerArgument\"", json, StringComparison.Ordinal);
        Assert.Contains("\"character\": \"A\"", json, StringComparison.Ordinal);
        Assert.Contains("\"character\": \"B\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterMapTablesMatchTheDecompiledGetCharIndex()
    {
        Assert.Equal("A", FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 0));
        Assert.Equal("Z", FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 25));
        Assert.Equal("0", FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 26));
        Assert.Equal("9", FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 35));
        Assert.Equal("?", FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 36));
        Assert.Equal("$", FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 47));
        Assert.Null(FntCharacterMap.Character(FntCharacterMap.Mode.Standard, 48));

        Assert.Equal("0", FntCharacterMap.Character(FntCharacterMap.Mode.Numeric, 0));
        Assert.Equal(":", FntCharacterMap.Character(FntCharacterMap.Mode.Numeric, 10));
        Assert.Null(FntCharacterMap.Character(FntCharacterMap.Mode.Numeric, 11));

        // Mode 2 keeps mode 0's head and adds lowercase at 48..73.
        Assert.Equal("A", FntCharacterMap.Character(FntCharacterMap.Mode.LowercaseFirst, 0));
        Assert.Equal("$", FntCharacterMap.Character(FntCharacterMap.Mode.LowercaseFirst, 47));
        Assert.Equal("a", FntCharacterMap.Character(FntCharacterMap.Mode.LowercaseFirst, 48));
        Assert.Equal("z", FntCharacterMap.Character(FntCharacterMap.Mode.LowercaseFirst, 73));
        Assert.Null(FntCharacterMap.Character(FntCharacterMap.Mode.LowercaseFirst, 74));

        Assert.True(FntCharacterMap.MapsEveryGlyph(FntCharacterMap.Mode.Numeric, 11));
        Assert.False(FntCharacterMap.MapsEveryGlyph(FntCharacterMap.Mode.Numeric, 12));
    }

    [Fact]
    public void Thps2RetailMainFont_DecodesToAPinnedAtlas()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(Thps2Retail, "mainf.fnt");
        Assert.SkipWhen(file is null, "THPS2 retail mainf.fnt not available");

        var font = FntFile.Parse(File.ReadAllBytes(file));

        Assert.Equal(FntLayout.PalettedWithAdvance, font.Layout);
        Assert.Equal(59, font.Glyphs.Length);
        Assert.Equal(12808, font.SerializedSize);
        Assert.Equal(24, font.Glyphs[0].Width);
        Assert.Equal(18, font.Glyphs[0].Height);
        Assert.Equal(18, font.Glyphs[0].Baseline);
        Assert.Equal(22, font.Glyphs[0].AdvanceWidth);
        // Glyph 8 is 'I' under every candidate map: the narrow one, 12px instead of 24px.
        Assert.Equal(12, font.Glyphs[8].Width);
        // Heights are per glyph, not per font: the letters are 18 tall, punctuation is shorter
        // and the accented glyphs past index 48 are taller to make room for the accent.
        Assert.Equal(43, font.Glyphs.Count(glyph => glyph.Height == 18));
        Assert.Equal(5, font.Glyphs.Min(glyph => glyph.Height));
        Assert.Equal(22, font.Glyphs.Max(glyph => glyph.Height));

        var atlas = FntAtlasBuilder.Build(font);
        Assert.Equal(512, atlas.Width);
        Assert.Equal(62, atlas.Height);

        const string expected = "b39b6a3434d6e84b904d09a4dac929e27f80bc7ce2ba00e2232461ae2f815626";
        var actual = Convert.ToHexStringLower(SHA256.HashData(atlas.Rgba));
        Assert.True(expected == actual, $"mainf.fnt atlas RGBA sha mismatch: actual {actual}");
    }

    [Fact]
    public void Thps2PrototypeBioFont_IsExactlyTheDecompiledLowercaseFirstSet()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(Thps2Proto, "s2bio.fnt");
        Assert.SkipWhen(file is null, "THPS2 prototype s2bio.fnt not available");

        var font = FntFile.Parse(File.ReadAllBytes(file));

        // 48 standard glyphs plus 26 lowercase: the full CharMap 2 set, rendering-verified as
        // A..Z 0..9 ? ! : . , - % + ' # / $ a..z.
        Assert.Equal(74, font.Glyphs.Length);
        Assert.True(FntCharacterMap.MapsEveryGlyph(FntCharacterMap.Mode.LowercaseFirst, font.Glyphs.Length));
        Assert.Equal(73, FntCharacterMap.HighestMappedIndex(FntCharacterMap.Mode.LowercaseFirst));
    }

    [Fact]
    public void Thps2RetailBioFont_HasMoreGlyphsThanAnyDecompiledMapCovers()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(Thps2Retail, "s2bio.fnt");
        Assert.SkipWhen(file is null, "THPS2 retail s2bio.fnt not available");

        var font = FntFile.Parse(File.ReadAllBytes(file));

        // Retail inserted '_' at 48, pushed lowercase to 49..74, and appended 19 accented
        // glyphs. Same game and filename as the prototype above, different ordering — which is
        // why no mapping may be inferred from a file.
        Assert.Equal(94, font.Glyphs.Length);
        Assert.False(FntCharacterMap.MapsEveryGlyph(FntCharacterMap.Mode.Standard, font.Glyphs.Length));
        Assert.False(FntCharacterMap.MapsEveryGlyph(FntCharacterMap.Mode.LowercaseFirst, font.Glyphs.Length));
    }

    [Fact]
    public void Thps2DreamcastLevelSelect_IsThePaletteLessVariant()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(Thps2Dreamcast, "LEVSEL.FNT");
        Assert.SkipWhen(file is null, "THPS2 Dreamcast LEVSEL.FNT not available");

        var font = FntFile.Parse(File.ReadAllBytes(file));

        Assert.Equal(FntLayout.CompactWithoutPalette, font.Layout);
        Assert.Equal(39, font.Glyphs.Length);
        Assert.Equal(12532, font.SerializedSize);
        Assert.Empty(font.Palette);
        Assert.All(font.Glyphs, glyph => Assert.Null(glyph.AdvanceWidth));
        // Pixel data starts immediately after the 12-byte record table, with no CLUT between.
        Assert.Equal(4 + 12 * 39, font.Glyphs[0].PixelDataOffset);

        // The pixel pin this file originally lacked. Asserting only counts and offsets let a
        // wrong nibble order ship: swapping horizontal pixel PAIRS leaves every glyph readable
        // as its letter while punching holes through each stroke, so an eyeball check passes.
        var atlas = FntAtlasBuilder.Build(font);
        const string expected = "f0c34d140006e298e8aff52484da6b9216ad9350ca5b6ebe5ea59279b3ab2bd7";
        var actual = Convert.ToHexStringLower(SHA256.HashData(atlas.Rgba));
        Assert.True(expected == actual, $"LEVSEL.FNT atlas RGBA sha mismatch: actual {actual}");
    }

    [CorpusFact]
    public void EachLayoutsNibbleOrderIsTheOneThatKeepsStrokesContinuous()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // The orders differ per layout, so neither is assumed. This re-derives both from the
        // corpus: decoding a glyph with the wrong nibble order shatters solid strokes into
        // alternating bright/dim columns, which shows up as more contiguous bright runs.
        var files = Directory
            .EnumerateFiles(paths.SampleBuildsDir!, "*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file).Equals(".fnt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var palettedAgree = 0;
        var palettedDisagree = 0;
        var compactAgree = 0;
        var compactDisagree = 0;
        var noEvidence = 0;

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!FntFile.TryParse(data, out var font))
                continue;

            var asShipped = HorizontalRoughness(font, swapNibbles: false);
            var swapped = HorizontalRoughness(font, swapNibbles: true);
            if (asShipped == swapped)
            {
                // Symmetric art scores identically either way and votes for neither order.
                noEvidence++;
                continue;
            }

            var shippedIsSmoother = asShipped < swapped;
            if (font.Layout == FntLayout.PalettedWithAdvance)
            {
                if (shippedIsSmoother) palettedAgree++;
                else palettedDisagree++;
            }
            else
            {
                if (shippedIsSmoother) compactAgree++;
                else compactDisagree++;
            }
        }

        // Unanimous on both sides: every file that carries evidence is decoded in the order
        // that keeps its strokes whole, and the two layouts land on opposite orders.
        Assert.Equal(0, palettedDisagree);
        Assert.Equal(0, compactDisagree);
        Assert.Equal(382, palettedAgree);
        Assert.Equal(1, compactAgree);
        Assert.Equal(0, noEvidence);
    }

    /// <summary>
    ///     Total absolute luminance change between horizontally adjacent pixels. Reading the
    ///     wrong nibble first swaps every horizontal pixel pair, which turns smooth strokes into
    ///     alternating bright/dim columns and raises this score.
    /// </summary>
    private static long HorizontalRoughness(FntFile font, bool swapNibbles)
    {
        Span<byte> rgba = stackalloc byte[4];
        long roughness = 0;
        foreach (var glyph in font.Glyphs)
        {
            for (var y = 0; y < glyph.Height; y++)
            {
                var previous = -1;
                for (var x = 0; x < glyph.Width; x++)
                {
                    // Swapping adjacent pixels within each pair is exactly what reading the
                    // other nibble first produces.
                    var sourceX = swapNibbles ? x ^ 1 : x;
                    FntAtlasBuilder.ToRgba(font, glyph.Pixels[y * glyph.Width + sourceX], rgba);
                    // Premultiply by alpha so a transparent texel reads as empty, not as its
                    // (meaningless) colour.
                    var luminance = (rgba[0] * 30 + rgba[1] * 59 + rgba[2] * 11) / 100 * rgba[3] / 255;
                    if (previous >= 0)
                        roughness += Math.Abs(luminance - previous);

                    previous = luminance;
                }
            }
        }

        return roughness;
    }

    [CorpusFact]
    public void EveryCorpusFntFile_EitherParsesExactlyOrIsRejectedAsAnotherFormat()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // Enumerate everything and filter: a "*.fnt" pattern also matches longer extensions
        // on Windows, which would silently widen the corpus this test pins.
        var files = Directory
            .EnumerateFiles(paths.SampleBuildsDir!, "*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file).Equals(".fnt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var paletted = 0;
        var intensity = 0;
        var rejected = 0;
        var glyphs = 0;
        var negativeBaselines = 0;
        var unreferencedMagentaEntries = 0;

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!FntFile.TryParse(data, out var font))
            {
                rejected++;
                continue;
            }

            // Parsing is only accepted on an exact fit, so this must hold for every file.
            Assert.Equal(data.Length, font.SerializedSize);

            if (font.Layout == FntLayout.PalettedWithAdvance)
            {
                paletted++;
                // Transparency is by CLUT value, and every paletted font carries the entry.
                Assert.Contains(font.Palette, entry => entry == 0);

                // The PSX texture path keys on magenta; these fonts never do. Two files carry a
                // magenta CLUT entry, but no glyph in the corpus references one, so the entry is
                // dead data and disabling the magenta key cannot change any rendered pixel.
                var referenced = font.Glyphs.SelectMany(static glyph => glyph.Pixels).ToHashSet();
                for (var index = 0; index < font.Palette.Length; index++)
                {
                    if ((font.Palette[index] & 0x7FFF) != 0x7C1F)
                        continue;

                    Assert.DoesNotContain((byte)index, referenced);
                    unreferencedMagentaEntries++;
                }
            }
            else
            {
                intensity++;
            }

            glyphs += font.Glyphs.Length;
            negativeBaselines += font.Glyphs.Count(glyph => glyph.Baseline < 0);
            Assert.All(font.Glyphs, glyph =>
            {
                Assert.Equal(glyph.WidthUnits * 4, glyph.Width);
                Assert.Equal(glyph.Width * glyph.Height, glyph.Pixels.Length);
                Assert.All(glyph.Pixels, value => Assert.InRange(value, 0, 15));
            });
        }

        Assert.Equal(443, files.Length);
        Assert.Equal(382, paletted);
        Assert.Equal(1, intensity);
        // 48 THAW plus 12 THPS3-PS2 files share the extension and are genuinely other formats.
        Assert.Equal(60, rejected);
        Assert.Equal(19777, glyphs);
        // Baseline is signed: 18 glyphs sit entirely below the text baseline.
        Assert.Equal(18, negativeBaselines);
        // Both are Spider-Man sp_fnt01 copies (DC prototype and PC final).
        Assert.Equal(2, unreferencedMagentaEntries);
    }

    /// <summary>
    ///     Two paletted glyphs: 8x3 with baseline 2 and advance 7, then 4x2 with baseline -2.
    /// </summary>
    private static byte[] BuildPalettedFont()
    {
        return BuildFont(FntLayout.PalettedWithAdvance, [(2, 3, 2, 7), (1, 2, -2, 4)]);
    }

    /// <summary>
    ///     Serializes a font from glyph records, filling pixels with an ascending nibble ramp so
    ///     nibble order and stride are both observable.
    /// </summary>
    private static byte[] BuildFont(
        FntLayout layout,
        (int WidthUnits, int Height, int Baseline, int AdvanceWidth)[] glyphs)
    {
        var paletted = layout == FntLayout.PalettedWithAdvance;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        writer.Write(glyphs.Length);
        foreach (var (widthUnits, height, baseline, advance) in glyphs)
        {
            writer.Write(widthUnits);
            writer.Write(height);
            writer.Write(baseline);
            if (paletted)
                writer.Write(advance);
        }

        if (paletted)
        {
            // Entry 0 is black with STP set (opaque); entry 15 is 0x0000 (the transparent texel).
            writer.Write((ushort)0x8000);
            for (var i = 1; i < FntFile.PaletteEntryCount - 1; i++)
                writer.Write((ushort)(0x8000 | (i * 2) | ((i * 2) << 5) | ((i * 2) << 10)));
            writer.Write((ushort)0x0000);
        }

        foreach (var (widthUnits, height, _, _) in glyphs)
        {
            var pixels = Math.Max(0, widthUnits) * Math.Max(0, height);
            var length = (pixels << 1) + ((pixels & 1) << 1);
            for (var i = 0; i < length; i++)
                writer.Write((byte)(((2 * i + 1) & 0x0F) << 4 | ((2 * i) & 0x0F)));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
