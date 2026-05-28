using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomGltfWriter
{
    private static bool ShouldBakeVertexTint(bool isWorldZoneScene, Ps2GeomLeaf leaf, byte alphaBlend)
    {
        if (!isWorldZoneScene || leaf.IsBillboard)
            return false;
        if (alphaBlend is not (0x0A or 0x1A or 0x00))
            return false;
        if (leaf.Vertices.Length == 0)
            return false;
        byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        var hasMeaningfulTint = false;
        foreach (var v in leaf.Vertices)
        {
            if (!v.HasColor) return false;
            if (v.R < minR) minR = v.R;
            if (v.R > maxR) maxR = v.R;
            if (v.G < minG) minG = v.G;
            if (v.G > maxG) maxG = v.G;
            if (v.B < minB) minB = v.B;
            if (v.B > maxB) maxB = v.B;
            if (Math.Abs(v.R - 128) > 8 || Math.Abs(v.G - 128) > 8 || Math.Abs(v.B - 128) > 8)
                hasMeaningfulTint = true;
        }

        if (!hasMeaningfulTint)
            return false;
        var maxRange = Math.Max(Math.Max(maxR - minR, maxG - minG), maxB - minB);
        return maxRange <= VertexTintBakeMaxChannelRange;
    }

    /// <summary>
    ///     Compute the leaf's average vertex tint and pack it into a 32-bit value
    ///     keyed as <c>0xFF_RR_GG_BB</c> — the high byte 0xFF is the "bake active"
    ///     sentinel that distinguishes this from the default 0 (no bake). Average
    ///     is plain arithmetic mean across all coloured vertices.
    /// </summary>
    private static uint ComputeBakedVertexTint(Ps2Vertex[] vertices)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        var n = 0;
        foreach (var v in vertices)
        {
            if (!v.HasColor) continue;
            sumR += v.R;
            sumG += v.G;
            sumB += v.B;
            n++;
        }

        if (n == 0) return 0u;
        var avgR = (byte)Math.Clamp(sumR / n, 0, 255);
        var avgG = (byte)Math.Clamp(sumG / n, 0, 255);
        var avgB = (byte)Math.Clamp(sumB / n, 0, 255);
        return 0xFF000000u | ((uint)avgR << 16) | ((uint)avgG << 8) | avgB;
    }

    /// <summary>
    ///     Multiply every RGB pixel of a texture by an 8-bit per-channel tint
    ///     using PS2-style integer math (<c>out = pixel * tint / 128</c>, clamped
    ///     to 255). Alpha is left untouched. Used to bake per-leaf vertex colour
    ///     into the texture so per-vertex glTF modulation can be skipped.
    /// </summary>
    private static byte[] ModulateTextureBy8BitTint(byte[] pngBytes, byte tintR, byte tintG, byte tintB)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    var r = (byte)Math.Min(255, p.R * tintR / 128);
                    var g = (byte)Math.Min(255, p.G * tintG / 128);
                    var b = (byte)Math.Min(255, p.B * tintB / 128);
                    row[x] = new Rgba32(r, g, b, p.A);
                }
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static bool IsStandardSourceAlphaBlend(byte alphaBlend)
    {
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        return aField == 0 && bField == 1 && cField == 0 && dField == 1;
    }

    private static bool UsesDestinationAlphaBlend(byte alphaBlend)
    {
        var cField = (alphaBlend >> 4) & 0x03;
        return cField == 1;
    }

    /// <summary>
    ///     Synthesis is eligible (per upstream check) for both <c>synthesize</c>
    ///     and <c>blend</c> strategies. In both we attempt to bake the prior
    ///     same-bbox sibling's alpha into the C=Ad consumer texture; the
    ///     difference is the fallback when no exact sibling exists — see
    ///     <see cref="DestAlphaOverrideForCField" />.
    /// </summary>
    private static bool DestAlphaSynthesisEligible()
    {
        return ReadDestAlphaStrategy() is "synthesize" or "blend";
    }

    private static DestAlphaOverride? DestAlphaOverrideForCField(int cField)
    {
        if (cField != 1)
            return null; // not a C=Ad material — no override
        return ReadDestAlphaStrategy() switch
        {
            "opaque" => DestAlphaOverride.Opaque,
            // "blend" only forces BLEND when synthesis upstream produced no
            // mask — i.e. there's no exact-bbox earlier sibling. The override
            // is gated by texChecksum still equalling the original source
            // (not a synthetic checksum), checked in GetOrCreateGeomMaterial.
            "blend" => DestAlphaOverride.Blend,
            _ => null // synthesize / unknown — leave to existing logic
        };
    }

    private static string ReadDestAlphaStrategy()
    {
        var v = Environment.GetEnvironmentVariable("THAW_DEST_ALPHA");
        if (string.IsNullOrWhiteSpace(v)) return "synthesize";
        return v.Trim().ToLowerInvariant();
    }

    private static uint CreateSyntheticTextureChecksum(uint sourceChecksum, uint maskChecksum)
    {
        var hash = 0xA1F3D5B7u;
        hash ^= RotateLeft(sourceChecksum, 7);
        hash ^= RotateLeft(maskChecksum, 19);
        return hash | 0x80000000u;
    }

    private static uint RotateLeft(uint value, int shift)
    {
        return (value << shift) | (value >> (32 - shift));
    }


    private static byte[] ResolveDitheredAlpha(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var width = image.Width;
        var height = image.Height;
        var original = new Rgba32[width * height];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).CopyTo(original.AsSpan(y * width, width));
        });

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var alphaSum = 0;
                    var sampleCount = 0;
                    var alphaWeight = 0;
                    var rSum = 0;
                    var gSum = 0;
                    var bSum = 0;

                    for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                    {
                        for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                        {
                            var p = original[yy * width + xx];
                            alphaSum += p.A;
                            sampleCount++;
                            if (p.A == 0)
                                continue;

                            alphaWeight += p.A;
                            rSum += p.R * p.A;
                            gSum += p.G * p.A;
                            bSum += p.B * p.A;
                        }
                    }

                    var current = original[y * width + x];
                    var alpha = (byte)(alphaSum / Math.Max(1, sampleCount));
                    if (alphaWeight == 0)
                    {
                        row[x] = new Rgba32(current.R, current.G, current.B, alpha);
                    }
                    else
                    {
                        row[x] = new Rgba32(
                            (byte)(rSum / alphaWeight),
                            (byte)(gSum / alphaWeight),
                            (byte)(bSum / alphaWeight),
                            alpha);
                    }
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte SampleAlpha(
        byte[] alpha,
        int width,
        int height,
        float u,
        float v,
        bool clampS,
        bool clampT)
    {
        var x = TextureCoordinateToPixel(u, width, clampS);
        var y = TextureCoordinateToPixel(v, height, clampT);
        return alpha[y * width + x];
    }

    private static int TextureCoordinateToPixel(float coordinate, int length, bool clamp)
    {
        if (length <= 1)
            return 0;

        double normalized;
        if (clamp)
        {
            normalized = Math.Clamp(coordinate, 0f, 1f);
            var clamped = (int)Math.Floor(normalized * length);
            return Math.Min(length - 1, Math.Max(0, clamped));
        }

        normalized = coordinate - Math.Floor(coordinate);
        if (normalized < 0)
            normalized += 1.0;
        var repeated = (int)Math.Floor(normalized * length);
        return Math.Min(length - 1, Math.Max(0, repeated));
    }
}
