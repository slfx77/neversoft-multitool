using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Static texture baking for THUG2/THAW Xbox/PC/GC multi-pass materials.
///
///     The engine combines material passes in-shader (THUG
///     <c>render.cpp:420-486 get_pixel_shader</c>): pass-k (k ≥ 1) ADDs,
///     SUBtracts, or LERPs onto the running colour, and the final framebuffer
///     blend uses pass-0's vBLEND_MODE with alpha from pass 0. glTF has a
///     single texture + blend mode per material, so both effects are baked into
///     the texture instead:
///     <list type="bullet">
///         <item>
///             pass-k overlays (tattoos, detail layers) composite onto the
///             pass-0 image at proportional UVs (<see cref="CompositeOverlays" />);
///         </item>
///         <item>
///             pass-0 ADD/SUBTRACT framebuffer modes (1-4) convert to the
///             portable alpha approximations the PS2 path already uses
///             (<see cref="ApplyFramebufferBlendBake" /> — additive keeps hue
///             with alpha = max-channel, subtractive renders black with
///             luminance alpha, mirroring
///             <c>GltfModelExporter.ProcessTextureForPortableGltf</c>).
///         </item>
///     </list>
///     Baked textures register under a deterministic synthetic checksum
///     (bit 31 set, like <c>Ps2GeomDestinationAlphaSynthesis</c>) so a plain
///     material sharing the same source texture keeps the pristine copy.
/// </summary>
internal static class XbxPassCompositor
{
    /// <summary>
    ///     Mirrors <c>GltfModelExporter.Ps2SubtractiveAlphaScale</c>: the
    ///     multiplicative alpha-over approximation under-darkens relative to the
    ///     hardware's additive subtract, so the strength is tuned down to match
    ///     typical mid-tone destinations.
    /// </summary>
    private const float SubtractiveAlphaScale = 0.30f;

    /// <summary>Pass-0 modes whose framebuffer blend needs a texture bake.</summary>
    public static bool IsFramebufferBakeMode(uint blendMode)
    {
        return blendMode is >= 1 and <= 4;
    }

    /// <summary>
    ///     The *_FIXED blend scalar: <c>fixedAlpha / 128</c> with 128 = 1.0
    ///     (THUG <c>material.cpp:671</c>), clamped — glTF cannot over-brighten.
    ///     0 is treated as neutral 1.0: the engine never authors a FIXED mode
    ///     that draws nothing, and the GC reader does not parse a fix value.
    /// </summary>
    public static float FixedAlphaScale(uint fixedAlpha)
    {
        return fixedAlpha == 0 ? 1f : Math.Clamp(fixedAlpha / 128f, 0f, 1f);
    }

    /// <summary>
    ///     Bakes a pass-0 ADD/ADD_FIXED/SUBTRACT/SUB_FIXED framebuffer blend
    ///     into the texture. Other modes return the input unchanged.
    /// </summary>
    public static byte[] ApplyFramebufferBlendBake(byte[] pngBytes, uint blendMode, uint fixedAlpha)
    {
        return blendMode switch
        {
            1 => MeshTextureHelper.ConvertAdditiveBlendTexture(pngBytes),
            2 => MeshTextureHelper.ScaleTextureAlpha(
                MeshTextureHelper.ConvertAdditiveBlendTexture(pngBytes),
                FixedAlphaScale(fixedAlpha)),
            3 => MeshTextureHelper.ScaleTextureAlpha(
                MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0),
                SubtractiveAlphaScale),
            4 => MeshTextureHelper.ScaleTextureAlpha(
                MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0),
                SubtractiveAlphaScale * FixedAlphaScale(fixedAlpha)),
            _ => pngBytes
        };
    }

    /// <summary>
    ///     Composites every eligible pass-k (k ≥ 1) overlay onto the pass-0
    ///     image. Eligible = textured, blend mode 1-6, and none of
    ///     <see cref="XbxMaterialFlags.OverlayCompositingSkipMask" /> (generated
    ///     or animated UVs cannot bake). Overlay UVs are assumed to share
    ///     pass-0's mapping (pass-k UV sets are not parsed); vertex-alpha
    ///     modulation is per-vertex and is deliberately not baked. The base
    ///     alpha channel is preserved throughout — the engine takes framebuffer
    ///     alpha from pass 0 alone.
    /// </summary>
    public static (byte[] Png, int CompositedCount) CompositeOverlays(
        XbxMaterial material,
        byte[] basePng,
        MeshChecksumTextureResolver textureProvider)
    {
        if (material.Passes.Length <= 1)
            return (basePng, 0);

        Image<Rgba32>? canvas = null;
        var composited = 0;
        try
        {
            for (var k = 1; k < material.Passes.Length; k++)
            {
                var pass = material.Passes[k];
                if (pass.TextureChecksum == 0 ||
                    pass.BlendMode is < 1 or > 6 ||
                    (pass.Flags & XbxMaterialFlags.OverlayCompositingSkipMask) != 0)
                {
                    continue;
                }

                var overlayPng = textureProvider(pass.TextureChecksum);
                if (overlayPng == null)
                    continue;

                using var overlay = Image.Load<Rgba32>(overlayPng);
                canvas ??= Image.Load<Rgba32>(basePng);

                // A higher-resolution overlay (e.g. a tattoo sheet over a low-res
                // body base) would lose its detail sampled down. Grow each canvas
                // axis independently so crossed dimensions (4x1 over 2x2) preserve
                // both inputs' resolution, and never shrink an earlier pass.
                var outputWidth = Math.Max(canvas.Width, overlay.Width);
                var outputHeight = Math.Max(canvas.Height, overlay.Height);
                if (outputWidth != canvas.Width || outputHeight != canvas.Height)
                    canvas.Mutate(op => op.Resize(outputWidth, outputHeight));

                BlendOverlayOntoCanvas(canvas, overlay, pass);
                composited++;
            }

            if (canvas == null || composited == 0)
                return (basePng, 0);

            using var ms = new MemoryStream();
            canvas.SaveAsPng(ms);
            return (ms.ToArray(), composited);
        }
        finally
        {
            canvas?.Dispose();
        }
    }

    /// <summary>
    ///     Deterministic checksum for a baked texture variant: FNV-1a over the
    ///     pass-0 checksum and every pass's blend ingredients, with bit 31 set
    ///     (the <c>Ps2GeomDestinationAlphaSynthesis</c> convention) so it can
    ///     never collide with a native checksum in use — including the GC
    ///     dictionary indices, which are small integers.
    /// </summary>
    public static uint CreateSyntheticTextureChecksum(XbxMaterial material)
    {
        const uint fnvPrime = 16777619;
        var hash = 2166136261u;
        void Mix(uint value)
        {
            for (var i = 0; i < 4; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= fnvPrime;
            }
        }

        foreach (var pass in material.Passes)
        {
            Mix(pass.TextureChecksum);
            Mix(pass.BlendMode);
            Mix(pass.FixedAlpha);
            Mix(pass.Flags);
            // HasColor gates overlay modulation, and the THUG2 reader fills
            // Color even when it is false — two materials differing only by
            // this byte bake different pixels, so it must key the checksum.
            Mix(pass.HasColor ? 1u : 0u);
            Mix((uint)(pass.Color.X * 255f) << 16 |
                (uint)(pass.Color.Y * 255f) << 8 |
                (uint)(pass.Color.Z * 255f));
        }

        return hash | 0x80000000u;
    }

    /// <summary>
    ///     Name suffix marking a baked texture variant (the PSX <c>__st{n}</c>
    ///     precedent): <c>__mp</c> for composited overlays, <c>__add</c> /
    ///     <c>__sub</c> for a baked pass-0 framebuffer blend.
    /// </summary>
    public static string TextureNameSuffix(uint pass0BlendMode, int compositedCount)
    {
        var suffix = compositedCount > 0 ? "__mp" : "";
        suffix += pass0BlendMode switch
        {
            1 or 2 => "__add",
            3 or 4 => "__sub",
            _ => ""
        };
        return suffix;
    }

    private static void BlendOverlayOntoCanvas(Image<Rgba32> canvas, Image<Rgba32> overlay, XbxPass pass)
    {
        // Pass colour modulates the overlay texel with 0.5 = neutral (the
        // engine multiplies then doubles — material.cpp:656, render.cpp _x4
        // with c.rgb loaded as colour/2), so the effective factor is colour×2.
        var modulate = pass.HasColor &&
                       (MathF.Abs(pass.Color.X - 0.5f) > 0.001f ||
                        MathF.Abs(pass.Color.Y - 0.5f) > 0.001f ||
                        MathF.Abs(pass.Color.Z - 0.5f) > 0.001f);
        var factorR = modulate ? pass.Color.X * 2f : 1f;
        var factorG = modulate ? pass.Color.Y * 2f : 1f;
        var factorB = modulate ? pass.Color.Z * 2f : 1f;

        // *_FIXED modes blend by the constant scalar; per-pixel modes by texel alpha.
        var fixedScale = pass.BlendMode is 2 or 4 or 6 ? FixedAlphaScale(pass.FixedAlpha) : -1f;

        // ADD/ADD_FIXED = +1 sign, SUBTRACT/SUB_FIXED = -1, BLEND/BLEND_FIXED = lerp.
        var isLerp = pass.BlendMode is 5 or 6;
        var sign = pass.BlendMode is 3 or 4 ? -1f : 1f;

        var ow = overlay.Width;
        var oh = overlay.Height;
        var cw = canvas.Width;
        var ch = canvas.Height;

        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ch; y++)
            {
                var row = accessor.GetRowSpan(y);
                var oy = (int)((long)y * oh / ch);
                for (var x = 0; x < cw; x++)
                {
                    var ox = (int)((long)x * ow / cw);
                    var o = overlay[ox, oy];

                    var a = fixedScale >= 0f ? fixedScale : o.A / 255f;
                    if (a <= 0f)
                        continue;

                    var or = Math.Min(255f, o.R * factorR);
                    var og = Math.Min(255f, o.G * factorG);
                    var ob = Math.Min(255f, o.B * factorB);

                    var p = row[x];
                    row[x] = new Rgba32(
                        BlendChannel(p.R, or, a, isLerp, sign),
                        BlendChannel(p.G, og, a, isLerp, sign),
                        BlendChannel(p.B, ob, a, isLerp, sign),
                        p.A);
                }
            }
        });
    }

    private static byte BlendChannel(float basis, float overlay, float a, bool isLerp, float sign)
    {
        var value = isLerp
            ? basis + (overlay - basis) * a
            : basis + sign * overlay * a;
        return (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
    }
}
