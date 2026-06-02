using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed partial class GsGifInterpreter
{
    private void InvalidateTextureCacheForFramebufferWrite(GsFramebufferTarget target)
    {
        if (textureCache.Count == 0)
            return;
        // PCSX2 SW renderer invalidates cached textures whose source pages overlap
        // the just-written framebuffer pages (GSRendererSW.cpp:667). PS2 GS addresses
        // memory in 256-byte blocks (TBP/FBP units); 32 blocks per 8KB page. A texture
        // occupies (W*H*bpp/8)/256 blocks. We invalidate any cached entry whose
        // [TBP, TBP + texture_blocks) range contains target.Fbp.
        //
        // Also invalidate when target.Fbp overlaps the cached entry's CLUT range
        // (TEX0.CBP..+CLUT_byte_size). Without this, a rewrite of the CLUT at CBP
        // (via FB write or VRAM upload) goes unnoticed — the decoded texture stays
        // cached with stale palette entries, producing wrong colours/alpha at
        // sub-CLUT precision. Manifested as missing mid-tone alpha values on the
        // bloom-feedback cascade (alpha histogram shows only 0/128, never 80/58/13).
        List<GsTextureCacheKey>? toRemove = null;
        foreach (var key in textureCache.Keys)
        {
            var tbp = (uint)(key.Tex0 & 0x3FFFu);
            var tw = 1u << (int)((key.Tex0 >> 26) & 0xF);
            var th = 1u << (int)((key.Tex0 >> 30) & 0xF);
            var psm = (uint)((key.Tex0 >> 20) & 0x3F);
            var bitsPerPixel = psm switch
            {
                Ps2TexPixelDecoder.PSMCT32 or Ps2GsVram.PSMZ32 => 32u,
                Ps2TexPixelDecoder.PSMCT24 or Ps2GsVram.PSMZ24 => 32u,
                Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => 16u,
                Ps2TexPixelDecoder.PSMT8 => 8u,
                Ps2TexPixelDecoder.PSMT4 => 4u,
                _ => 32u
            };
            var byteSize = tw * th * bitsPerPixel / 8u;
            var blockCount = (byteSize + 255u) / 256u;
            if (target.Fbp >= tbp && target.Fbp < tbp + blockCount)
            {
                toRemove ??= [];
                toRemove.Add(key);
                continue;
            }

            // CLUT range overlap (PSMT4 / PSMT8 indexed textures).
            if (psm is Ps2TexPixelDecoder.PSMT4 or Ps2TexPixelDecoder.PSMT8)
            {
                var cbp = (uint)((key.Tex0 >> 37) & 0x3FFFu);
                var cpsm = (uint)((key.Tex0 >> 51) & 0xF);
                var clutEntries = psm == Ps2TexPixelDecoder.PSMT4 ? 16u : 256u;
                var clutBpp = cpsm switch
                {
                    Ps2TexPixelDecoder.PSMCT32 => 32u,
                    Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => 16u,
                    _ => 32u
                };
                var clutByteSize = clutEntries * clutBpp / 8u;
                var clutBlockCount = (clutByteSize + 255u) / 256u;
                if (clutBlockCount == 0)
                    clutBlockCount = 1;
                if (target.Fbp >= cbp && target.Fbp < cbp + clutBlockCount)
                {
                    toRemove ??= [];
                    toRemove.Add(key);
                }
            }
        }

        if (toRemove != null)
        {
            foreach (var key in toRemove)
                textureCache.Remove(key);
        }
    }

    private GsSample WriteFramebufferPixel(
        GsFramebufferTarget target,
        GsContext context,
        int x,
        int y,
        byte r,
        byte g,
        byte b,
        byte a,
        uint extraFbmsk = 0)
    {
        if (target.Fbw == 0 || !IsFramebufferPsmSupported(target.Psm))
            return new GsSample(r, g, b, a);

        // FBA (Frame Buffer Alpha Adjustment) ORs the alpha output with PS2-nominal
        // 0x80 (128) — i.e., forces the high bit set, not forces alpha to 255.
        if ((context.Fba & 1) != 0)
            a |= 0x80;

        // PS2 GS dithering: when DTHE=1 and the FB is PSMCT16/16S, add DIMX[y%4][x%4]
        // to each colour channel before 5-bit quantization. Eliminates visible banding
        // that otherwise appears as a quantization "grid" over smooth gradients.
        if ((state.Dthe & 1) != 0 && (target.Psm == Ps2TexPixelDecoder.PSMCT16 || target.Psm == Ps2GsVram.PSMCT16S))
        {
            var dither = DecodeDimx(state.Dimx, x, y);
            r = (byte)Math.Clamp(r + dither, 0, 255);
            g = (byte)Math.Clamp(g + dither, 0, 255);
            b = (byte)Math.Clamp(b + dither, 0, 255);
        }

        vram.WritePixel(target.Fbp, target.Fbw, target.Psm, x, y, r, g, b, a, target.Fbmsk | extraFbmsk);
        // Pass state.Texa so PSMCT16/16S write-confirmation reads back match the spec-correct
        // alpha the dst read in BlendPixel will see — same TEXA, same readback value, same probe.
        var written = vram.ReadPixelRgba(target.Fbp, target.Fbw, target.Psm, x, y, state.Texa);
        // Mirror the *post-mask, post-readback* bytes into the per-(FBP, FBW, PSM) shadow
        // cache. The cache lets later texture samples pull RT pixels per-FBW-surface instead
        // of going through the shared-VRAM swizzle, which produces FBW-aliasing strips when
        // the same FBP is written at multiple FBWs (THAW bloom pyramid pattern).
        renderTargetCache.WritePixel(target.Fbp, target.Fbw, target.Psm, x, y,
            written.R, written.G, written.B, written.A);
        return new GsSample(written.R, written.G, written.B, written.A);
    }

    private static int DecodeDimx(ulong dimx, int x, int y)
    {
        // DIMX is a 4x4 matrix of *3-bit signed* values (range -4..+3) plus a 1-bit padding
        // per cell. Each row occupies 16 bits (4 cells x 4 bits/cell, 3 data + 1 pad).
        // PCSX2 GSRegs.h:589-... documents the bit layout exactly.
        var bitIndex = ((y & 3) << 4) | ((x & 3) << 2);
        var threeBits = (int)((dimx >> bitIndex) & 0x7);
        // Sign-extend 3-bit value: values 4..7 represent -4..-1.
        return threeBits >= 4 ? threeBits - 8 : threeBits;
    }

    private GsSample BlendPixel(
        GsContext context,
        GsFramebufferTarget target,
        int x,
        int y,
        int p,
        float srcR,
        float srcG,
        float srcB,
        float srcA)
    {
        var dst = ReadBlendDestination(target, x, y, p);
        var dstR = dst.R;
        var dstG = dst.G;
        var dstB = dst.B;
        var dstA = dst.A;
        var a = (int)(context.Alpha & 0x3);
        var b = (int)((context.Alpha >> 2) & 0x3);
        var c = (int)((context.Alpha >> 4) & 0x3);
        var d = (int)((context.Alpha >> 6) & 0x3);
        var fix = (float)((context.Alpha >> 32) & 0xFF);

        // PS2 GS blend factor = byte_value / 128 (per PS2 GS spec: 128 = 1.0).
        // PSMCT32 stores raw 8-bit alpha, so 255 yields the HDR ~1.99 factor.
        var blend = c switch
        {
            0 => srcA / 128f,
            1 => dstA / 128f,
            2 => fix / 128f,
            _ => 1f
        };

        var ar = SelectColor(a, srcR, dstR);
        var ag = SelectColor(a, srcG, dstG);
        var ab = SelectColor(a, srcB, dstB);
        var br = SelectColor(b, srcR, dstR);
        var bg = SelectColor(b, srcG, dstG);
        var bb = SelectColor(b, srcB, dstB);
        var dr = SelectColor(d, srcR, dstR);
        var dg = SelectColor(d, srcG, dstG);
        var db = SelectColor(d, srcB, dstB);

        return new GsSample(
            ClampByte((ar - br) * blend + dr),
            ClampByte((ag - bg) * blend + dg),
            ClampByte((ab - bb) * blend + db),
            ClampByte(srcA));
    }

    private bool TryReadFramebufferPixel(GsFramebufferTarget target, int x, int y, out GsSample pixel)
    {
        if (target.Fbw == 0 || !IsFramebufferPsmSupported(target.Psm))
        {
            pixel = default;
            return false;
        }

        // Pass state.Texa so PSMCT16/16S Cd reads during ABE blending get the spec-correct
        // alpha (TA0/TA1 expansion with AEM rule) instead of the binary 0/255.
        var rgba = vram.ReadPixelRgba(target.Fbp, target.Fbw, target.Psm, x, y, state.Texa);
        pixel = new GsSample(rgba.R, rgba.G, rgba.B, rgba.A);
        return true;
    }

    /// <summary>
    ///     Resolves the destination color for ABE blending. PSMCT16/16S/Z need spec-correct
    ///     TEXA-expanded alpha from VRAM (the dithered, masked, post-write byte); for those
    ///     <see cref="TryReadFramebufferPixel" /> handles it directly. For PSMs the VRAM
    ///     read doesn't support, fall through to the per-FBP screen-space buffer (which
    ///     stores RGBA bytes the rasterizer wrote, isolated per target FBP so an unrelated
    ///     draw to a different FBP doesn't pollute Cd). If even the per-FBP buffer hasn't
    ///     been written at this coordinate, return (0,0,0,255) — matches the initial
    ///     <c>pixels[]</c> state pre-refactor when no draw had touched that screen coord.
    ///     <para>
    ///         <paramref name="screenIndex" /> is reserved for diagnostic asserts; the
    ///         current implementation no longer reads the screen-space <c>pixels[]</c>
    ///         buffer here, but keeping the index lets future probes correlate Cd reads
    ///         with the final PCRTC-composed output.
    ///     </para>
    /// </summary>
    private GsSample ReadBlendDestination(GsFramebufferTarget target, int x, int y, int screenIndex)
    {
        _ = screenIndex;
        if (TryReadFramebufferPixel(target, x, y, out var framebufferPixel))
            return framebufferPixel;
        if (renderTargetCache.TryReadScreenSpacePixel(target.Fbp, target.Fbw, target.Psm, x, y, out var rgba))
            return new GsSample(rgba.R, rgba.G, rgba.B, rgba.A);
        return new GsSample(0, 0, 0, 255);
    }

    private bool ShouldProbe(int x, int y, GsFramebufferTarget target)
    {
        if (options.PixelProbe == null)
            return false;
        if (options.ProbeX.HasValue && options.ProbeX.Value != x)
            return false;
        if (options.ProbeY.HasValue && options.ProbeY.Value != y)
            return false;
        if (options.ProbeFbp.HasValue && options.ProbeFbp.Value != target.Fbp)
            return false;
        // Require at least one filter to avoid emitting probe events for every pixel.
        return options.ProbeX.HasValue || options.ProbeY.HasValue || options.ProbeFbp.HasValue;
    }

    private void EmitPixelProbe(
        int x,
        int y,
        string primitive,
        GsFramebufferTarget target,
        GsContext context,
        bool textureSampled,
        GsSample sample,
        float vertexR,
        float vertexG,
        float vertexB,
        float vertexA,
        float srcR,
        float srcG,
        float srcB,
        float srcA,
        float z,
        GsSample? preBlendDst,
        GsSample written)
    {
        if (options.PixelProbe == null)
            return;
        var info = new GsPixelProbeInfo(
            x,
            y,
            primitive,
            renderAudit.DrawsSeen,
            target.Fbp,
            target.Fbw,
            target.Psm,
            target.Fbmsk,
            context.Tex0,
            context.Alpha,
            context.Test,
            state.Tme,
            state.Abe,
            textureSampled,
            sample.R,
            sample.G,
            sample.B,
            sample.A,
            vertexR,
            vertexG,
            vertexB,
            vertexA,
            srcR,
            srcG,
            srcB,
            srcA,
            z,
            preBlendDst.HasValue,
            preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.R, 0, 255) : (byte)0,
            preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.G, 0, 255) : (byte)0,
            preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.B, 0, 255) : (byte)0,
            preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.A, 0, 255) : (byte)0,
            (byte)Math.Clamp((int)written.R, 0, 255),
            (byte)Math.Clamp((int)written.G, 0, 255),
            (byte)Math.Clamp((int)written.B, 0, 255),
            (byte)Math.Clamp((int)written.A, 0, 255));
        options.PixelProbe(info);
    }

    private static float SelectColor(int selector, float src, float dst)
    {
        return selector switch
        {
            0 => src,
            1 => dst,
            2 => 0f,
            _ => 0f
        };
    }

    private static byte ClampByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
    }

    private void ApplyFog(float fog, ref float r, ref float g, ref float b)
    {
        var sourceWeight = Math.Clamp(fog / 256f, 0f, 1f);
        var fogR = state.FogColor & 0xFF;
        var fogG = (state.FogColor >> 8) & 0xFF;
        var fogB = (state.FogColor >> 16) & 0xFF;
        r = MathF.Truncate(fogR + (r - fogR) * sourceWeight);
        g = MathF.Truncate(fogG + (g - fogG) * sourceWeight);
        b = MathF.Truncate(fogB + (b - fogB) * sourceWeight);
    }

    // PS2 GS texture function combiner. Inputs in 0..255 byte space where 128 is nominal 1.0.
    // Modes per GS User's Manual section 4.7.2 (and PCSX2 GSDrawScanline reference):
    //   TFX=0 MODULATE:   Cout = Tc * Cv / 128;          Aout = TCC ? At * Av / 128 : Av
    //   TFX=1 DECAL:      Cout = Tc;                     Aout = TCC ? At           : Av
    //   TFX=2 HIGHLIGHT:  Cout = Tc * Cv / 128 + Av;     Aout = TCC ? At + Av      : Av
    //   TFX=3 HIGHLIGHT2: Cout = Tc * Cv / 128 + Av;     Aout = TCC ? At           : Av
    // When texturing is disabled (TME=0) the GS bypasses the texture combiner entirely and
    // emits the vertex color directly; do the same here instead of modulating by the
    // Sample() white-fallback, which would double-bright every untextured draw.
    private void CombineTextureFunction(
        GsContext context,
        GsSample sample,
        float vR,
        float vG,
        float vB,
        float vA,
        out float srcR,
        out float srcG,
        out float srcB,
        out float srcA)
    {
        if (!state.Tme)
        {
            srcR = Math.Clamp(vR, 0f, 255f);
            srcG = Math.Clamp(vG, 0f, 255f);
            srcB = Math.Clamp(vB, 0f, 255f);
            srcA = Math.Clamp(vA, 0f, 255f);
            return;
        }

        var tcc = ((context.Tex0 >> 34) & 0x1) != 0;
        var tfx = (int)((context.Tex0 >> 35) & 0x3);
        var tR = sample.R;
        var tG = sample.G;
        var tB = sample.B;
        var tA = sample.A;

        switch (tfx)
        {
            case 1: // DECAL
                srcR = tR;
                srcG = tG;
                srcB = tB;
                srcA = tcc ? tA : vA;
                break;
            case 2: // HIGHLIGHT
                srcR = tR * vR / 128f + vA;
                srcG = tG * vG / 128f + vA;
                srcB = tB * vB / 128f + vA;
                srcA = tcc ? tA + vA : vA;
                break;
            case 3: // HIGHLIGHT2
                srcR = tR * vR / 128f + vA;
                srcG = tG * vG / 128f + vA;
                srcB = tB * vB / 128f + vA;
                srcA = tcc ? tA : vA;
                break;
            default: // MODULATE
                srcR = tR * vR / 128f;
                srcG = tG * vG / 128f;
                srcB = tB * vB / 128f;
                srcA = tcc ? tA * vA / 128f : vA;
                break;
        }

        srcR = Math.Clamp(srcR, 0f, 255f);
        srcG = Math.Clamp(srcG, 0f, 255f);
        srcB = Math.Clamp(srcB, 0f, 255f);
        srcA = Math.Clamp(srcA, 0f, 255f);
    }

    private float[] GetDepthBuffer(GsContext context)
    {
        if (!depthBuffers.TryGetValue(context.Zbuf, out var buffer))
        {
            buffer = new float[options.Width * options.Height];
            depthBuffers[context.Zbuf] = buffer;
        }

        return buffer;
    }

    private void WriteDepthToVram(GsContext context, int x, int y, float z)
    {
        // PCSX2 ZBUF layout (GSRegs.h:948-957): ZBP=bits 0..8 (page units),
        // PSM=bits 24..27 (4-bit *wire* field; the upper 0x30 nibble is implicit),
        // ZMSK=bit 32. The Z buffer shares its stride with the colour buffer's FBW
        // (there is no ZBW field).
        //
        // GSRegs.h documents PSM as `u32 PSM : 6` because PCSX2 stores the *decoded*
        // value internally after OR'ing 0x30 in; the wire format only stores the low
        // nibble (PSMZ32=0, PSMZ24=1, PSMZ16=2, PSMZ16S=10). Reading 6 bits raw and
        // comparing against PSMZ24=0x31 always failed — we then dropped every Z write
        // (2M skipped, 0 stored) and the bloom-feedback chain that samples the Z buffer
        // as a PSMZ24 texture saw garbage instead of the depth silhouette.
        var zpsm = 0x30u | (uint)((context.Zbuf >> 24) & 0xFu);
        if (zpsm != Ps2GsVram.PSMZ32 && zpsm != Ps2GsVram.PSMZ24 &&
            zpsm != Ps2GsVram.PSMZ16 && zpsm != Ps2GsVram.PSMZ16S)
        {
            renderAudit.DepthVramWritesSkippedPsm++;
            return;
        }

        var fbw = (uint)((context.Frame >> 16) & 0x3Fu);
        if (fbw == 0)
        {
            renderAudit.DepthVramWritesSkippedFbw0++;
            return;
        }

        var zbp = (uint)(context.Zbuf & 0x1FFu) << 5;
        var zi = z <= 0f ? 0u : z >= 4294967295f ? 0xFFFFFFFFu : (uint)z;
        var r = (byte)(zi & 0xFFu);
        var g = (byte)((zi >> 8) & 0xFFu);
        var b = (byte)((zi >> 16) & 0xFFu);
        var a = (byte)((zi >> 24) & 0xFFu);
        vram.WritePixel(zbp, fbw, zpsm, x, y, r, g, b, a);
        renderAudit.DepthVramWrites++;
        if (zpsm == Ps2GsVram.PSMZ16)
            renderAudit.DepthVramWritesPsm16++;
        else if (zpsm == Ps2GsVram.PSMZ16S)
            renderAudit.DepthVramWritesPsm16S++;
    }

    private static bool PassesDepth(float z, int idx, GsContext context, float[] depth)
    {
        var zte = ((context.Test >> 16) & 1) != 0;
        if (!zte)
            return true;

        var ztst = (int)((context.Test >> 17) & 0x3);
        return ztst switch
        {
            0 => false,
            1 => true,
            2 => z >= depth[idx],
            3 => z > depth[idx],
            _ => true
        };
    }

    private static bool ZWriteMasked(GsContext context)
    {
        return ((context.Zbuf >> 32) & 1) != 0;
    }

    private static GsAlphaTestResult EvaluateAlphaTest(float alpha, GsContext context)
    {
        var ate = (context.Test & 1) != 0;
        if (!ate)
            return GsAlphaTestResult.Pass;

        var atst = (int)((context.Test >> 1) & 0x7);
        var aref = (int)((context.Test >> 4) & 0xFF);
        var passes = atst switch
        {
            0 => false,
            1 => true,
            2 => alpha < aref,
            3 => alpha <= aref,
            4 => Math.Abs(alpha - aref) < 0.5f,
            5 => alpha >= aref,
            6 => alpha > aref,
            7 => Math.Abs(alpha - aref) >= 0.5f,
            _ => true
        };

        if (passes)
            return GsAlphaTestResult.Pass;

        return ((context.Test >> 12) & 0x3) switch
        {
            1 => GsAlphaTestResult.FailFramebufferOnly,
            2 => GsAlphaTestResult.FailZBufferOnly,
            3 => GsAlphaTestResult.FailRgbOnly,
            _ => GsAlphaTestResult.FailKeep
        };
    }

    private static uint AlphaWriteMaskForPsm(uint psm)
    {
        return psm switch
        {
            Ps2TexPixelDecoder.PSMCT32 => 0xFF000000u,
            Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => 0x8000u,
            _ => 0u
        };
    }

    private static string AlphaFailModeName(GsAlphaTestResult result)
    {
        return result switch
        {
            GsAlphaTestResult.FailFramebufferOnly => "fb_only",
            GsAlphaTestResult.FailZBufferOnly => "zb_only",
            GsAlphaTestResult.FailRgbOnly => "rgb_only",
            _ => "keep"
        };
    }

    private (int X0, int Y0, int X1, int Y1) DecodeScissor(GsContext context)
    {
        if (context.Scissor == 0)
            return (0, 0, options.Width - 1, options.Height - 1);

        var x0 = (int)(context.Scissor & 0x7FF);
        var x1 = (int)((context.Scissor >> 16) & 0x7FF);
        var y0 = (int)((context.Scissor >> 32) & 0x7FF);
        var y1 = (int)((context.Scissor >> 48) & 0x7FF);
        x0 = (int)MathF.Floor(x0 * options.CoordinateScaleX);
        y0 = (int)MathF.Floor(y0 * options.CoordinateScaleY);
        x1 = (int)MathF.Ceiling((x1 + 1) * options.CoordinateScaleX) - 1;
        y1 = (int)MathF.Ceiling((y1 + 1) * options.CoordinateScaleY) - 1;
        return (
            Math.Clamp(x0, 0, options.Width - 1),
            Math.Clamp(y0, 0, options.Height - 1),
            Math.Clamp(x1, 0, options.Width - 1),
            Math.Clamp(y1, 0, options.Height - 1));
    }

    private float InterpolateTextureCoordinate(
        float aValue,
        float bValue,
        float cValue,
        float aQ,
        float bQ,
        float cQ,
        float w0,
        float w1,
        float w2)
    {
        if (state.Fst)
            return aValue * w0 + bValue * w1 + cValue * w2;

        var q = aQ * w0 + bQ * w1 + cQ * w2;
        if (MathF.Abs(q) < 0.000001f)
            return aValue * w0 + bValue * w1 + cValue * w2;

        return (aValue * w0 + bValue * w1 + cValue * w2) / q;
    }

    private float PointTextureCoordinate(float value, float q)
    {
        if (state.Fst || MathF.Abs(q) < 0.000001f)
            return value;

        return value / q;
    }

    private GsSample Sample(GsContext context, GsTexture? texture, float u, float v)
    {
        if (texture == null)
            return new GsSample(255, 255, 255, 255);

        var wms = (int)(context.Clamp & 0x3);
        var wmt = (int)((context.Clamp >> 2) & 0x3);
        var minU = (int)((context.Clamp >> 4) & 0x3FF);
        var maxU = (int)((context.Clamp >> 14) & 0x3FF);
        var minV = (int)((context.Clamp >> 24) & 0x3FF);
        var maxV = (int)((context.Clamp >> 34) & 0x3FF);
        var tcc = (int)((context.Tex0 >> 34) & 0x1);
        // TEX1.MMAG (bit 5) selects magnification filter: 0=NEAREST, 1=LINEAR.
        // TEX1.MMIN (bits 6-8) selects minification filter: 1=LINEAR, 4/5/6/7 also LINEAR per PCSX2.
        // Matches PCSX2's IsMagLinear / IsMinLinear at GSRegs.h:854-855.
        // We don't compute LOD per-pixel; apply bilinear whenever EITHER mag or min uses LINEAR.
        // This over-applies bilinear for mixed-mode draws (e.g. mag=LINEAR + min=NEAREST during
        // minification), but matches PCSX2 closely enough in practice — bilinear on mip-0 is a
        // strict superset of NEAREST's information for most THAW textures.
        var mmag = (int)((context.Tex1 >> 5) & 0x1);
        var mmin = (int)((context.Tex1 >> 6) & 0x7);
        var isMagLinear = mmag != 0;
        var isMinLinear = mmin == 1 || (mmin & 4) != 0;
        var useLinear = isMagLinear || isMinLinear;

        if (!useLinear)
        {
            var su = Wrap(u, texture.Width, wms, minU, maxU);
            var sv = Wrap(v, texture.Height, wmt, minV, maxV);
            var i = (sv * texture.Width + su) * 4;
            return new GsSample(texture.Rgba[i], texture.Rgba[i + 1], texture.Rgba[i + 2],
                tcc == 0 ? 255 : texture.Rgba[i + 3]);
        }

        // Bilinear (MMAG=LINEAR): 4-tap average of neighbouring texels at the texel-center offset.
        var fu = u * texture.Width - 0.5f;
        var fv = v * texture.Height - 0.5f;
        var u0 = (int)MathF.Floor(fu);
        var v0 = (int)MathF.Floor(fv);
        var du = fu - u0;
        var dv = fv - v0;

        var su0 = WrapInt(u0, texture.Width, wms, minU, maxU);
        var su1 = WrapInt(u0 + 1, texture.Width, wms, minU, maxU);
        var sv0 = WrapInt(v0, texture.Height, wmt, minV, maxV);
        var sv1 = WrapInt(v0 + 1, texture.Height, wmt, minV, maxV);

        var i00 = (sv0 * texture.Width + su0) * 4;
        var i10 = (sv0 * texture.Width + su1) * 4;
        var i01 = (sv1 * texture.Width + su0) * 4;
        var i11 = (sv1 * texture.Width + su1) * 4;

        var w00 = (1f - du) * (1f - dv);
        var w10 = du * (1f - dv);
        var w01 = (1f - du) * dv;
        var w11 = du * dv;

        var r = texture.Rgba[i00] * w00 + texture.Rgba[i10] * w10 + texture.Rgba[i01] * w01 + texture.Rgba[i11] * w11;
        var g = texture.Rgba[i00 + 1] * w00 + texture.Rgba[i10 + 1] * w10 + texture.Rgba[i01 + 1] * w01 +
                texture.Rgba[i11 + 1] * w11;
        var b = texture.Rgba[i00 + 2] * w00 + texture.Rgba[i10 + 2] * w10 + texture.Rgba[i01 + 2] * w01 +
                texture.Rgba[i11 + 2] * w11;
        var a = tcc == 0
            ? 255f
            : texture.Rgba[i00 + 3] * w00 + texture.Rgba[i10 + 3] * w10 + texture.Rgba[i01 + 3] * w01 +
              texture.Rgba[i11 + 3] * w11;
        return new GsSample(r, g, b, a);
    }

    private static int WrapInt(int texel, int size, int mode, int regionMinOrMask, int regionMaxOrFix)
    {
        if (mode == 1)
            return Math.Clamp(texel, 0, size - 1);

        if (mode == 2)
        {
            var min = Math.Clamp(regionMinOrMask, 0, size - 1);
            var max = Math.Clamp(regionMaxOrFix, 0, size - 1);
            if (max < min)
                (min, max) = (max, min);
            return Math.Clamp(texel, min, max);
        }

        if (mode == 3)
            return Math.Clamp((texel & regionMinOrMask) | regionMaxOrFix, 0, size - 1);

        var s = size > 0 ? size : 1;
        return (texel % s + s) % s;
    }

    private static int Wrap(float value, int size, int mode, int regionMinOrMask, int regionMaxOrFix)
    {
        if (mode == 1)
            return Math.Clamp((int)MathF.Floor(value * size), 0, size - 1);

        if (mode == 2)
        {
            var texel = (int)MathF.Floor(value * size);
            var min = Math.Clamp(regionMinOrMask, 0, size - 1);
            var max = Math.Clamp(regionMaxOrFix, 0, size - 1);
            if (max < min)
                (min, max) = (max, min);
            return Math.Clamp(texel, min, max);
        }

        if (mode == 3)
        {
            var texel = (int)MathF.Floor(value * size);
            return Math.Clamp((texel & regionMinOrMask) | regionMaxOrFix, 0, size - 1);
        }

        var wrapped = value - MathF.Floor(value);
        return Math.Clamp((int)MathF.Floor(wrapped * size), 0, size - 1);
    }
}
