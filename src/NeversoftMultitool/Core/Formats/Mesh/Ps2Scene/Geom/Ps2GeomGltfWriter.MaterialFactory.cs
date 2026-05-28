using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using AlphaMode = SharpGLTF.Materials.AlphaMode;
using Image = SixLabors.ImageSharp.Image;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomGltfWriter
{
    private static GeomMaterialInfo GetOrCreateGeomMaterial(
        GeomMaterialKey key,
        Dictionary<GeomMaterialKey, GeomMaterialInfo> cache,
        Ps2TexaTextureResolver? textureProvider,
        ulong texa)
    {
        if (cache.TryGetValue(key, out var existing))
            return existing;

        var textureChecksum = key.TextureChecksum;
        var clampBits = key.ClampBits;
        var alphaBlend = key.AlphaBlend;
        var aref = key.AlphaRef;
        var fixValue = key.FixValue;

        var matName = textureChecksum != 0
            ? QbKey.QbKey.TryResolve(textureChecksum) ?? $"tex_{textureChecksum:X8}"
            : "default";

        // Decode blend equation fields from ALPHA_1 register.
        // Cv = ((A-B)*C)>>7 + D where A/B/D select Cs/Cd/0, C selects As/Ad/FIX.
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;

        // Additive: A=Cs(0), B=0(2), D=Cd(1) -> Cs*C + Cd (foam, glow, water spray)
        var isAdditive = aField == 0 && bField == 2 && dField == 1;
        // Subtractive: A=0(2), B=Cs(0), D=Cd(1) -> (0-Cs)*C + Cd = Cd - Cs*C (shadows)
        var isSubtractive = aField == 2 && bField == 0 && dField == 1;

        var builder = new MaterialBuilder(matName)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        // ALPHA_1 low byte: 0x0A/0x1A/0x00 means PS2 outputs source colour with no
        // alpha contribution (Cv = Cs). When ATE is also off, the alpha channel is
        // entirely irrelevant — the texture renders fully opaque even if the artist
        // baked alpha gradients into the PNG (typical of building wall textures with
        // window highlights). Treating those PNGs as BLEND/MASK based on histogram
        // makes whole buildings translucent.
        //
        // We use this as the "force opaque" signal: if GS would have ignored alpha,
        // bake alpha=255 so glTF viewers do the same.
        var isOpaqueBlend = alphaBlend is 0x0A or 0x1A or 0x00;
        var psIgnoresAlpha = isOpaqueBlend && aref == 0;

        // Standard alpha blend (Cs*As + Cd*(1-As)) with alpha test disabled. PS2
        // titles routinely encoded a per-pixel checker/dither pattern in the alpha
        // channel of window glass and similar surfaces and relied on the low GS
        // resolution + CRT blur to perceptually average it into translucency. In
        // glTF we can't reproduce that smoothing, so a literal MASK at 0.5 renders
        // the dither as a visible checkerboard — see dither investigation in
        // tools/diagnostics/score_dither_textures.py for the empirical separator.
        var isStandardBlend = IsStandardSourceAlphaBlend(alphaBlend);
        var ditherCandidate = isStandardBlend && aref == 0;

        var alphaProfile = AlphaProfile.AllOpaque;
        var isDarkBlendOverlay = false;
        var isSoftShadowOverlay = false;
        var isMonochromeAlphaMask = false;
        var isDitherResolved = false;
        var isFoliageCutout = false;
        if (textureProvider != null && textureChecksum != 0)
        {
            var pngBytes = textureProvider(textureChecksum, texa);
            if (pngBytes != null)
            {
                isDarkBlendOverlay = isStandardBlend && !psIgnoresAlpha && IsDarkAlphaOverlay(pngBytes);
                isFoliageCutout = IsLikelyFoliageCutout(pngBytes);
                isSoftShadowOverlay = isStandardBlend
                                      && !psIgnoresAlpha
                                      && !isFoliageCutout
                                      && IsLikelySoftShadowOverlay(pngBytes);
                // A "monochrome alpha mask" is a texture whose visible pixels
                // share a single RGB color (e.g. pure white, pure black) with
                // shape detail entirely encoded in the alpha channel. These
                // textures are used as stencils — the actual rendered colour
                // comes from per-vertex modulation. Forcing them to MASK alpha
                // mode clips the soft alpha gradient at the shape edges, which
                // is what gives shadows their soft falloff. Force BLEND
                // instead so vertex-color × alpha gradient renders smoothly.
                isMonochromeAlphaMask = isStandardBlend
                                        && !psIgnoresAlpha
                                        && !isFoliageCutout
                                        && !isDarkBlendOverlay
                                        && IsMonochromeAlphaMask(pngBytes);
                var forceOpaqueRgbOnlyTexture = isStandardBlend
                                                && !isFoliageCutout
                                                && IsAllTransparentWithUsefulRgb(pngBytes);

                // Additive / subtractive blend approximations: convert the texture to
                // luminance-alpha first so the profile reflects the final image.
                if (isAdditive)
                    pngBytes = ConvertAdditiveBlendTexture(pngBytes);
                else if (isSubtractive)
                {
                    // PS2 subtractive equation: Cd - Cs*As/128 (constant subtraction
                    // proportional to texture brightness). glTF BLEND can only do
                    // Cs*As + Cd*(1-As) (proportional dimming). For typical dirt/cloud
                    // overlays the proportional approximation reads ~2x stronger than
                    // the engine's constant subtraction, producing dark "blobs" where
                    // the engine renders subtle tints. Scale the converted luminance-
                    // alpha down to bring it back into the in-game perceptual range.
                    pngBytes = ConvertBlendTexture(pngBytes, 0, 0, 0);
                    pngBytes = ScaleTextureAlpha(pngBytes, WorldzoneSubtractiveAlphaScale);
                }
                else if (psIgnoresAlpha)
                    pngBytes = ForceAlphaOpaque(pngBytes);
                else if (forceOpaqueRgbOnlyTexture)
                    pngBytes = ForceAlphaOpaque(pngBytes);
                else if (ditherCandidate && IsDitheredAlpha(pngBytes))
                {
                    pngBytes = ResolveDitheredAlpha(pngBytes);
                    isDitherResolved = true;
                }
                else if (isSoftShadowOverlay)
                {
                    pngBytes = ScaleTextureAlpha(pngBytes, WorldzoneSoftShadowAlphaScale);
                }

                // Per-leaf vertex-tint bake (Option B for OPAQUE worldzone draws):
                // when the leaf carries a non-zero baked tint, multiply the texture
                // by that tint in PS2-style 8-bit math (clamped to 255). The vertex
                // attribute is then emitted as (1,1,1,1) so the result is no longer
                // gamma-amplified by viewers' linear-space vertex modulation.
                if (key.BakedVertexTintRgba >> 24 == 0xFFu)
                {
                    var tintR = (byte)((key.BakedVertexTintRgba >> 16) & 0xFFu);
                    var tintG = (byte)((key.BakedVertexTintRgba >> 8) & 0xFFu);
                    var tintB = (byte)(key.BakedVertexTintRgba & 0xFFu);
                    pngBytes = ModulateTextureBy8BitTint(pngBytes, tintR, tintG, tintB);
                }

                alphaProfile = AnalyzeAlphaProfile(pngBytes);

                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Set texture wrap mode from CLAMP_1 register.
                // WMS/WMT: 0=REPEAT, 1=CLAMP. Only simple modes used in practice.
                // Emit REPEAT explicitly too; some Windows viewers do not reliably apply
                // glTF's default repeat sampler to large worldzone UV ranges.
                var wms = clampBits & 0x03;
                var wmt = (clampBits >> 2) & 0x03;
                var wrapS = wms != 0
                    ? TextureWrapMode.CLAMP_TO_EDGE
                    : TextureWrapMode.REPEAT;
                var wrapT = wmt != 0
                    ? TextureWrapMode.CLAMP_TO_EDGE
                    : TextureWrapMode.REPEAT;
                builder.GetChannel(KnownChannel.BaseColor)
                    .Texture
                    .WithSampler(wrapS, wrapT);
            }
        }

        // Alpha mode selection.
        //
        // Additive/subtractive get BLEND (texture is already luminance-alpha) and
        // FIX-mode opacity gets BLEND with a base-colour alpha override.
        //
        // Otherwise we use the PNG alpha HISTOGRAM rather than the GS AREF byte:
        //   AllOpaque  -> OPAQUE (no transparent pixels — includes the "PS2 ignores
        //                 alpha" case where we already forced alpha=255 above)
        //   Bimodal    -> MASK at 0.5 (signs / fences / foliage — clean hard cutout)
        //   Graduated  -> BLEND (shadows / decals — soft falloff)
        //
        // This fixes shadow textures that were previously forced into MASK with a
        // pixel-accurate AREF/255 threshold (jagged edge), and sign textures whose
        // graphic was hidden inside a larger transparent quad.
        // C=Ad (destination-alpha-blend) override path. Strategies via
        // THAW_DEST_ALPHA: "opaque" collapses the GS equation to Cs (assuming
        // destination alpha was 1 from a prior opaque pass); "blend" emits a
        // normal source-alpha BLEND when no exact-bbox sibling synthesized
        // a mask (the synthesis path runs upstream and stamps a synthetic
        // checksum with bit 31 set — when that's our texture, the synthesis
        // baked the mask in already and the override would replace it).
        var destAlphaOverride = DestAlphaOverrideForCField(cField);
        var isSyntheticDestAlphaTexture = (textureChecksum & 0x80000000u) != 0u;
        var alphaModeName = "OPAQUE";
        if (destAlphaOverride is { } overrideMode && !isSyntheticDestAlphaTexture)
        {
            switch (overrideMode)
            {
                case DestAlphaOverride.Opaque:
                    // alpha mode stays OPAQUE; force the texture's alpha to 255 so
                    // glTF doesn't drop the texture in BLEND-style sorting.
                    if (textureProvider != null && textureChecksum != 0)
                    {
                        var pngBytes = textureProvider(textureChecksum, texa);
                        if (pngBytes != null)
                        {
                            var forced = ForceAlphaOpaque(pngBytes);
                            builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(forced));
                        }
                    }

                    var info0 = new GeomMaterialInfo(builder, alphaModeName);
                    cache[key] = info0;
                    return info0;
                case DestAlphaOverride.Blend:
                    builder.WithAlpha(AlphaMode.BLEND);
                    alphaModeName = "BLEND";
                    var infoB = new GeomMaterialInfo(builder, alphaModeName);
                    cache[key] = infoB;
                    return infoB;
            }
        }

        if (isAdditive)
        {
            builder.WithAlpha(AlphaMode.BLEND);
            alphaModeName = "BLEND";

            // For FIX-mode additive (Cs*FIX/128 + Cd), scale brightness
            if (cField == 2)
            {
                var intensity = Math.Min(fixValue / 128f, 1f);
                builder.WithBaseColor(new Vector4(intensity, intensity, intensity, 1f));
            }
        }
        else if (isSubtractive)
        {
            // Subtractive: Cd - Cs*FIX/128. Texture converted to black + luminance alpha.
            // BLEND output = black * alpha + dst * (1-alpha) = dst * (1-alpha) -> darkens.
            builder.WithAlpha(AlphaMode.BLEND);
            alphaModeName = "BLEND";

            if (cField == 2)
            {
                var opacity = Math.Min(fixValue / 128f, 1f);
                builder.WithBaseColor(new Vector4(0f, 0f, 0f, opacity));
            }
            else
            {
                builder.WithBaseColor(new Vector4(0f, 0f, 0f, 1f));
            }
        }
        else if (cField == 2 && fixValue < Ps2SceneGltfWriter.FixBlendOpaqueThreshold)
        {
            // Fixed-blend opacity: C field (bits 4-5) == 2 means FIX mode. FIX value in
            // ALPHA_1 bits 32-39; opacity = FIX/128. High FIX values fall through to the
            // histogram path below and typically land on OPAQUE, avoiding z-sort issues.
            builder.WithAlpha(AlphaMode.BLEND);
            alphaModeName = "BLEND";
            builder.WithBaseColor(new Vector4(1f, 1f, 1f, fixValue / 128f));
        }
        else if ((key.PreferCutout || isFoliageCutout) && alphaProfile != AlphaProfile.AllOpaque)
        {
            builder.WithAlpha(AlphaMode.MASK);
            alphaModeName = "MASK";
        }
        else if ((key.PreferBlend && alphaProfile != AlphaProfile.Bimodal)
                 || isDarkBlendOverlay
                 || isSoftShadowOverlay
                 || isMonochromeAlphaMask
                 || isDitherResolved)
        {
            // Shadow/decal overlays and low vertex-alpha worldzone cards are authored for GS
            // alpha blending. Exporting them as MASK makes the overlay look like opaque black
            // geometry; keep them as BLEND while foliage/sign cutouts stay on the histogram
            // path below.
            builder.WithAlpha(AlphaMode.BLEND);
            alphaModeName = "BLEND";
        }
        else
        {
            switch (alphaProfile)
            {
                case AlphaProfile.Bimodal:
                    builder.WithAlpha(AlphaMode.MASK);
                    alphaModeName = "MASK";
                    break;
                case AlphaProfile.Graduated:
                    builder.WithAlpha(AlphaMode.BLEND);
                    alphaModeName = "BLEND";
                    break;
                case AlphaProfile.AllOpaque:
                default:
                    // Leave as default OPAQUE. Nothing to blend against.
                    break;
            }
        }

        var info = new GeomMaterialInfo(builder, alphaModeName);
        cache[key] = info;
        return info;
    }

    /// <summary>
    ///     Rewrite a PNG so every pixel has alpha = 255 while preserving RGB. Used for
    ///     materials whose GS ALPHA register yields output = Cs (source colour only)
    ///     with the alpha test disabled — PS2 hardware ignores the alpha channel
    ///     entirely, and we want glTF viewers to render the same way regardless of how
    ///     they handle premultiplication.
    /// </summary>
    private static byte[] ForceAlphaOpaque(byte[] pngBytes)
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
                    row[x] = new Rgba32(p.R, p.G, p.B, 255);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] ScaleTextureAlpha(byte[] pngBytes, float scale)
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
                    var alpha = Math.Clamp((int)MathF.Round(p.A * scale), 0, 255);
                    row[x] = new Rgba32(p.R, p.G, p.B, (byte)alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
