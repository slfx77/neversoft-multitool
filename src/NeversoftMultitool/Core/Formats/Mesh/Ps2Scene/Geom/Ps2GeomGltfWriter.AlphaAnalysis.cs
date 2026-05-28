using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomGltfWriter
{
    /// <summary>
    ///     Scan a PNG's alpha channel and bucket each pixel as low (≤ 8), high (≥ 248),
    ///     or middle. The classification rule is "where is the mass of the histogram?":
    ///     pixels concentrated at the extremes indicate a cutout-with-fringe (Bimodal),
    ///     pixels spread through the mid range indicate a true gradient (Graduated).
    ///     Using mid-fraction alone (the simpler rule) wrongly classifies palm-leaf
    ///     and sign textures as Graduated because of their antialiased edges, which
    ///     then renders as BLEND and bleeds through the depth buffer.
    /// </summary>
    private static AlphaProfile AnalyzeAlphaProfile(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var total = image.Width * image.Height;
        if (total == 0) return AlphaProfile.AllOpaque;

        var counts = new int[3]; // 0=low, 1=high, 2=mid
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var a = row[x].A;
                    if (a <= 8) counts[0]++;
                    else if (a >= 248) counts[1]++;
                    else counts[2]++;
                }
            }
        });
        var low = counts[0];
        var high = counts[1];
        var mid = counts[2];

        // No transparency at all: texture is fully opaque.
        if (low == 0 && mid == 0)
            return AlphaProfile.AllOpaque;

        // Low-opacity masks and glass overlays often have no fully opaque pixels
        // after the PS2 texture decoder has scaled them into normal PNG alpha.
        // They are real blended surfaces, not hard cutouts; exporting them as MASK
        // either drops them completely or turns them into solid cards.
        if (high == 0 && mid > 0)
            return AlphaProfile.Graduated;

        // Extremes (a=0 + a=255) ≥ 80% of pixels: most of the image is opaque or
        // fully transparent, the rest is antialiasing noise. MASK at 0.5 keeps the
        // hard outline and writes to depth so geometry behind correctly occludes.
        // The 80% threshold is empirical: palm-leaf cutouts come in around 85-95%
        // extremes (5-15% AA fringe); soft shadow textures come in well below 50%.
        if ((low + high) * 5 >= total * 4)
            return AlphaProfile.Bimodal;

        return AlphaProfile.Graduated;
    }

    private static bool HasUsefulDestinationAlpha(byte[] pngBytes)
    {
        return AnalyzeAlphaProfile(pngBytes) != AlphaProfile.AllOpaque;
    }

    /// <summary>
    ///     Whether the mask should be flattened to its average alpha before
    ///     synthesis. True for "decorative pattern" masks — small alpha
    ///     textures (containing transparent pixels) that tile many times
    ///     across a large consumer surface, where keeping the per-texel
    ///     pattern would produce a visible tile grid in the synthesized
    ///     output. The PS2 GS doesn't show tiles in this case because of
    ///     mipmap minification + bilinear filtering at high LOD: the
    ///     per-texel pattern blurs to a near-uniform tint.
    /// </summary>
    /// <remarks>
    ///     Single-instance shape masks (≤4× tiling) skip the flattening so
    ///     dirt-patch silhouettes and other purposeful cutouts retain their
    ///     local detail. Masks without any transparent pixels (e.g. grass-
    ///     base alpha-grade textures) also skip — they already render as
    ///     uniform opaque whether tiled or not.
    /// </remarks>
    private static bool MaskShouldFlattenToAverage(byte[] maskPng, Ps2GeomLeaf maskLeaf)
    {
        const float maxTilingFactor = 4.0f;

        using var image = Image.Load<Rgba32>(maskPng);
        var texW = image.Width;
        var texH = image.Height;
        if (texW <= 0 || texH <= 0)
            return false;

        var hasTransparentPixels = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !hasTransparentPixels; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A == 0)
                    {
                        hasTransparentPixels = true;
                        break;
                    }
                }
            }
        });
        if (!hasTransparentPixels)
            return false;

        var (min, max) = ComputeBbox(maskLeaf.Vertices);
        var size = max - min;
        var sx = MathF.Abs(size.X);
        var sy = MathF.Abs(size.Y);
        var sz = MathF.Abs(size.Z);
        var maxAxis = MathF.Max(sx, MathF.Max(sy, sz));
        var minAxis = MathF.Min(sx, MathF.Min(sy, sz));
        var midAxis = sx + sy + sz - maxAxis - minAxis;
        if (maxAxis <= 0f || midAxis <= 0f)
            return false;

        var tilingMajor = maxAxis / texW;
        var tilingMinor = midAxis / texH;
        if (tilingMajor < tilingMinor) (tilingMajor, tilingMinor) = (tilingMinor, tilingMajor);

        return tilingMajor > maxTilingFactor;
    }

    /// <summary>
    ///     Returns a 1×1 fully-opaque PNG. Used as the synthesis mask when the
    ///     paired earlier sibling was an opaque draw (alpha1 in {0x0A,0x1A,0x00}):
    ///     the GS wrote framebuffer alpha=1 uniformly, so the C=Ad consumer
    ///     should render with no masking — its texture stays as-is.
    /// </summary>
    private static byte[] CreateUniformOpaqueMask()
    {
        using var flat = new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, 255));
        using var ms = new MemoryStream();
        flat.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Returns a 1×1 PNG with the source mask's average alpha and a
    ///     placeholder RGB. <see cref="ApplyDestinationAlphaMask" /> samples
    ///     mask alpha at the source UVs, so a 1×1 mask reads the same alpha
    ///     for every source texel — a uniform translucent overlay.
    /// </summary>
    private static byte[] FlattenMaskAlphaToAverage(byte[] maskPng)
    {
        using var src = Image.Load<Rgba32>(maskPng);
        long sum = 0;
        long count = 0;
        src.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    sum += row[x].A;
                    count++;
                }
            }
        });
        var avgAlpha = count > 0 ? (byte)(sum / count) : (byte)0;

        using var flat = new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, avgAlpha));
        using var ms = new MemoryStream();
        flat.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Detect a high-frequency dithered alpha pattern: pixels at extreme alpha
    ///     (a=0 or a=255) that alternate every 1-2 pixels. Returns true when the
    ///     fraction of horizontal-and-vertical neighbour pairs that flip between
    ///     the two extremes exceeds a small threshold.
    ///     Empirically validated against z_bh.pak.ps2 worldzone textures: window
    ///     glass dithers score 5-28%, while genuine cutouts (chain link fences,
    ///     antialiased silhouettes) score below 4%.
    /// </summary>
    private static bool IsDitheredAlpha(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var w = image.Width;
        var h = image.Height;
        var pairTotal = 2 * w * h - w - h;
        if (pairTotal <= 0) return false;

        var alternations = 0;
        image.ProcessPixelRows(accessor =>
            alternations = CountExtremeAlphaAlternations(accessor));

        // 5% threshold separates dithers from cutouts in the empirical sample.
        return alternations * 20 >= pairTotal;
    }

    private static bool IsDarkAlphaOverlay(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        long visiblePixels = 0;
        long weightedMaxChannel = 0;
        long alphaWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                        continue;

                    visiblePixels++;
                    weightedMaxChannel += Math.Max(p.R, Math.Max(p.G, p.B)) * p.A;
                    alphaWeight += p.A;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        var totalPixels = image.Width * image.Height;
        if (visiblePixels * 100 < totalPixels)
            return false;

        var averageMaxChannel = weightedMaxChannel / (double)alphaWeight;
        return averageMaxChannel <= 32.0;
    }

    /// <summary>
    ///     Detects "monochrome alpha-mask" textures: stencils where every visible
    ///     pixel shares a single RGB color (any color — pure white, pure black,
    ///     a flat tint) and the actual shape lives entirely in the alpha channel.
    ///     The classic case is shadow cards: white texture × dark vertex colours
    ///     × graduated alpha = soft-edged shadow on the receiver. Forcing these
    ///     into MASK alpha mode at the bimodal threshold clips the alpha gradient
    ///     and turns the soft shadow into a hard cutout — visible as fully opaque
    ///     edges instead of feathered ones.
    /// </summary>
    /// <remarks>
    ///     Heuristic: the visible pixels' RGB has near-zero channel range
    ///     (max - min ≤ 8 per pixel, on average), <em>and</em> non-trivial
    ///     mid-alpha pixels exist (≥ 2% of visible pixels carry 8 ≤ alpha &lt; 248
    ///     — actual gradient, not just a binary cutout). The 6CBB6DE0 cannon
    ///     shadow in z_sm hits this: RGB ≈ (253, 253, 253) for every visible
    ///     pixel, with a soft alpha falloff at the silhouette edges.
    /// </remarks>
    private static bool IsMonochromeAlphaMask(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        long visiblePixels = 0;
        long midAlphaPixels = 0;
        long channelRangeWeight = 0;
        long alphaWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                        continue;
                    visiblePixels++;
                    alphaWeight += p.A;
                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    var minChannel = Math.Min(p.R, Math.Min(p.G, p.B));
                    channelRangeWeight += (maxChannel - minChannel) * p.A;
                    if (p.A is >= 8 and < 248)
                        midAlphaPixels++;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        var averageChannelRange = channelRangeWeight / (double)alphaWeight;
        // Tight monochrome threshold: avg per-pixel channel-range ≤ 8 means
        // every visible pixel is within 8/255 of a single grey level.
        if (averageChannelRange > 8.0)
            return false;

        // Need real gradient pixels — not just a binary 0/255 cutout. The 2%
        // floor lets z_bh foliage cards (which can be technically monochrome
        // too but use a hard cutout) stay on the MASK path.
        return midAlphaPixels * 50 >= visiblePixels;
    }

    private static bool IsLikelySoftShadowOverlay(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long visiblePixels = 0;
        long highAlphaPixels = 0;
        long alphaWeight = 0;
        long maxChannelWeight = 0;
        long channelRangeWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                        continue;

                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    var minChannel = Math.Min(p.R, Math.Min(p.G, p.B));
                    visiblePixels++;
                    if (p.A >= 248)
                        highAlphaPixels++;
                    alphaWeight += p.A;
                    maxChannelWeight += maxChannel * p.A;
                    channelRangeWeight += (maxChannel - minChannel) * p.A;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        // These are the broad, grey/black texture-card shadows. They cover much of
        // the card, have a meaningful opaque-alpha component, and are low-saturation.
        // Foliage, boardwalk masks, and water either have sparse coverage, colourful
        // pixels, or only mid-alpha pixels and should stay on their normal path.
        if (visiblePixels * 10 < totalPixels * 7)
            return false;
        if (highAlphaPixels * 5 < visiblePixels)
            return false;

        var averageMaxChannel = maxChannelWeight / (double)alphaWeight;
        var averageChannelRange = channelRangeWeight / (double)alphaWeight;
        return averageMaxChannel <= 128.0 && averageChannelRange <= 32.0;
    }

    private static bool IsLikelyFoliageCutout(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long lowAlphaPixels = 0;
        long visiblePixels = 0;
        long alphaWeight = 0;
        long redWeight = 0;
        long greenWeight = 0;
        long blueWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                    {
                        lowAlphaPixels++;
                        continue;
                    }

                    visiblePixels++;
                    alphaWeight += p.A;
                    redWeight += p.R * p.A;
                    greenWeight += p.G * p.A;
                    blueWeight += p.B * p.A;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        // Foliage cards commonly have large transparent regions and many antialias
        // alpha levels, so the generic alpha histogram treats them as BLEND. Use the
        // visible colour bias to keep these hard-depth cutouts as MASK instead.
        if (lowAlphaPixels * 20 < totalPixels)
            return false;

        var averageRed = redWeight / (double)alphaWeight;
        var averageGreen = greenWeight / (double)alphaWeight;
        var averageBlue = blueWeight / (double)alphaWeight;
        return averageGreen >= averageRed * 1.03
               && averageGreen >= averageBlue * 1.05
               && averageGreen >= 50.0
               && averageRed <= 170.0;
    }

    private static bool IsExtremeAlphaFlip(byte a1, byte a2)
    {
        return (a1 == 0 && a2 == 255) || (a1 == 255 && a2 == 0);
    }

    private static int CountExtremeAlphaAlternations(PixelAccessor<Rgba32> accessor)
    {
        var count = 0;
        for (var y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < row.Length - 1; x++)
            {
                if (IsExtremeAlphaFlip(row[x].A, row[x + 1].A))
                    count++;
            }
        }

        for (var y = 0; y < accessor.Height - 1; y++)
        {
            var rowA = accessor.GetRowSpan(y);
            var rowB = accessor.GetRowSpan(y + 1);
            for (var x = 0; x < rowA.Length; x++)
            {
                if (IsExtremeAlphaFlip(rowA[x].A, rowB[x].A))
                    count++;
            }
        }

        return count;
    }

}
