using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed partial class GsGifInterpreter
{
    private void PresentFromDisplayBuffer(GsDumpFile dump)
    {
        // Need at least through BGCOLOR (offset 224 + 8). The previous threshold was 168
        // (a single circuit's DISPFB+DISPLAY) which masked the dual-circuit case.
        if (dump.Registers.Length < 232)
        {
            NoteUnsupported("display_registers_missing");
            return;
        }

        static ulong U64(ReadOnlySpan<byte> source, int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        }

        var regs = dump.Registers.AsSpan();
        var pmode = U64(regs, 0);
        var bgcolor = U64(regs, 224);

        var en1 = (pmode & 0x1) != 0;
        var en2 = (pmode & 0x2) != 0;
        var mmod = (pmode & 0x20) != 0;
        var amod = (pmode & 0x40) != 0;
        var slbg = (pmode & 0x80) != 0;
        var alpConst = (uint)((pmode >> 8) & 0xFF);

        renderAudit.PmodeRaw = pmode;
        renderAudit.PmodeEn1 = en1;
        renderAudit.PmodeEn2 = en2;
        renderAudit.PmodeMmod = mmod;
        renderAudit.PmodeAmod = amod;
        renderAudit.PmodeSlbg = slbg;
        renderAudit.PmodeAlp = alpConst;
        renderAudit.BgColor = (uint)(bgcolor & 0xFFFFFF);
        if (amod)
            NoteApproximation("pcrtc_amod_ignored");

        var circuit1 = TryReadCircuit(regs, 0, 112, 128, en1);
        var circuit2 = TryReadCircuit(regs, 1, 144, 160, en2);
        renderAudit.PresentedCircuits.Add(circuit1.Audit);
        renderAudit.PresentedCircuits.Add(circuit2.Audit);

        if (!en1 && !en2)
        {
            NoteUnsupported("display_no_active_circuit");
            return;
        }

        if (en1 && circuit1.Rgba == null)
        {
            NoteUnsupported($"display_psm_0x{circuit1.Audit.Psm:X2}");
            return;
        }

        if (en2 && circuit2.Rgba == null)
        {
            NoteUnsupported($"display_psm_0x{circuit2.Audit.Psm:X2}");
            return;
        }

        // Pick the "front" record for legacy audit fields: prefer circuit 1 when enabled.
        var front = en1 ? circuit1 : circuit2;
        renderAudit.PresentedFramebufferKey = front.Audit.Key;
        renderAudit.PresentedFramebufferFbp = front.Audit.Fbp;
        renderAudit.PresentedFramebufferFbw = front.Audit.Fbw;
        renderAudit.PresentedFramebufferWidth = front.Audit.Width;
        renderAudit.PresentedFramebufferHeight = front.Audit.Height;
        renderAudit.PresentedFramebufferPsm = front.Audit.Psm;
        renderAudit.PresentedFramebufferNonBlackPixels = front.Audit.NonBlackPixels;

        // Original code rejected framebuffers with fewer than 1024 non-black pixels as
        // a "blank dump" heuristic. That throws away the present pass for small / mostly
        // background captures whose visible content is correctly coming from BGCOLOR or
        // a sparse HUD layer, so we only skip when both enabled circuits are truly empty.
        var totalNonBlack = (en1 ? circuit1.Audit.NonBlackPixels : 0) +
                            (en2 ? circuit2.Audit.NonBlackPixels : 0);
        if (totalNonBlack < 1)
        {
            NoteUnsupported("display_framebuffer_blank");
            return;
        }

        Array.Clear(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var bgR = (byte)(bgcolor & 0xFF);
        var bgG = (byte)((bgcolor >> 8) & 0xFF);
        var bgB = (byte)((bgcolor >> 16) & 0xFF);

        var outWidth = Math.Min(options.Width,
            Math.Max(en1 ? circuit1.Audit.Width : 0, en2 ? circuit2.Audit.Width : 0));
        var outHeight = Math.Min(options.Height,
            Math.Max(en1 ? circuit1.Audit.Height : 0, en2 ? circuit2.Audit.Height : 0));

        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                var dstOffset = (y * options.Width + x) * 4;

                // Sample each circuit (or zero when disabled / out of bounds).
                var (c1R, c1G, c1B, c1A) = SampleCircuit(circuit1, x, y, en1);
                var (c2R, c2G, c2B, c2A) = SampleCircuit(circuit2, x, y, en2);

                // PCRTC behaviour:
                //   - When SLBG=1, circuit 1 is alpha-blended with BGCOLOR (circuit 2 ignored).
                //   - When only one circuit is enabled, that circuit is output directly with no blend.
                //   - When both are enabled and SLBG=0, alpha-blend circuit 1 over circuit 2.
                //   - MMOD=1 uses circuit 1's per-pixel alpha as the blend factor (PS2 nominal 128 = 1.0);
                //     MMOD=0 uses the constant ALP from PMODE (0..255 mapped to 0..1.0).
                byte outR, outG, outB;
                if (!en1 && en2)
                {
                    outR = c2R;
                    outG = c2G;
                    outB = c2B;
                }
                else if (en1 && !en2 && !slbg)
                {
                    outR = c1R;
                    outG = c1G;
                    outB = c1B;
                }
                else
                {
                    var backR = slbg || !en2 ? bgR : c2R;
                    var backG = slbg || !en2 ? bgG : c2G;
                    var backB = slbg || !en2 ? bgB : c2B;
                    var alpha = mmod ? Math.Clamp(c1A / 128f, 0f, 1f) : alpConst / 255f;
                    outR = (byte)Math.Clamp((int)MathF.Round(c1R * alpha + backR * (1f - alpha)), 0, 255);
                    outG = (byte)Math.Clamp((int)MathF.Round(c1G * alpha + backG * (1f - alpha)), 0, 255);
                    outB = (byte)Math.Clamp((int)MathF.Round(c1B * alpha + backB * (1f - alpha)), 0, 255);
                }

                pixels[dstOffset] = outR;
                pixels[dstOffset + 1] = outG;
                pixels[dstOffset + 2] = outB;
                pixels[dstOffset + 3] = 255;
            }
        }

        renderAudit.PresentedFramebuffer = true;
    }

    private CircuitLayer TryReadCircuit(ReadOnlySpan<byte> regs, int circuitIndex, int dispfbOffset, int displayOffset,
        bool enabled)
    {
        var dispfb = BinaryPrimitives.ReadUInt64LittleEndian(regs[dispfbOffset..]);
        var display = BinaryPrimitives.ReadUInt64LittleEndian(regs[displayOffset..]);

        var fbp = (uint)(dispfb & 0x1FF) << 5;
        var fbw = (uint)((dispfb >> 9) & 0x3F);
        var psm = (uint)((dispfb >> 15) & 0x1F);
        var dbx = (int)((dispfb >> 32) & 0x7FF);
        var dby = (int)((dispfb >> 43) & 0x7FF);
        var dx = (int)(display & 0xFFF);
        var dy = (int)((display >> 12) & 0x7FF);
        var magh = (int)((display >> 23) & 0xF) + 1;
        var magv = (int)((display >> 27) & 0x3) + 1;
        var dw = (int)((display >> 32) & 0xFFF) + 1;
        var dh = (int)((display >> 44) & 0x7FF) + 1;
        var sourceWidth = Math.Clamp(dw / Math.Max(1, magh), 1, options.Width);
        var sourceHeight = Math.Clamp(dh / Math.Max(1, magv), 1, options.Height);

        var audit = new GsPresentedCircuitAudit
        {
            Circuit = circuitIndex,
            Enabled = enabled,
            Key = MakeFramebufferKey(fbp, fbw, psm, 0),
            Fbp = fbp,
            Fbw = fbw,
            Psm = psm,
            Width = sourceWidth,
            Height = sourceHeight,
            Dbx = dbx,
            Dby = dby,
            Dx = dx,
            Dy = dy,
            Dw = dw,
            Dh = dh,
            Magh = magh,
            Magv = magv
        };

        if (!enabled)
            return new CircuitLayer(null, audit);

        if (dbx != 0 || dby != 0)
            NoteUnsupported("display_framebuffer_offset_ignored");

        var rgba = ReadFramebufferRgba(fbp, fbw, psm, sourceWidth, sourceHeight);
        if (rgba != null)
            audit.NonBlackPixels = CountNonBlackPixels(rgba);
        return new CircuitLayer(rgba, audit);
    }

    private static (byte R, byte G, byte B, byte A) SampleCircuit(CircuitLayer layer, int x, int y, bool enabled)
    {
        if (!enabled || layer.Rgba == null)
            return (0, 0, 0, 0);
        if (x >= layer.Audit.Width || y >= layer.Audit.Height)
            return (0, 0, 0, 0);
        var src = (y * layer.Audit.Width + x) * 4;
        var rgba = layer.Rgba;
        return (rgba[src], rgba[src + 1], rgba[src + 2], rgba[src + 3]);
    }

    private static int CountNonBlackPixels(byte[] rgba)
    {
        var count = 0;
        for (var i = 0; i + 2 < rgba.Length; i += 4)
        {
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0)
                count++;
        }

        return count;
    }

    private void MaybeSaveDrawRt(GsFramebufferTarget target)
    {
        if (options.SaveRtSink == null)
            return;
        var drawIndex = renderAudit.DrawsSeen;
        if (drawIndex < options.SaveRtStart)
            return;
        if (options.SaveRtCount.HasValue && drawIndex >= options.SaveRtStart + options.SaveRtCount.Value)
            return;
        if (options.SaveRtFbp.HasValue && options.SaveRtFbp.Value != target.Fbp)
            return;
        if (options.SaveRtOnStateTransition)
        {
            // Capture only at framebuffer-state transitions: write nothing while a run of
            // consecutive draws targets the same (Fbp, Fbw, Psm). Used to align our output
            // against PCSX2's per-primitive-batch RT dumps without our per-triangle index
            // counter forcing 30x more snapshots than PCSX2 produces.
            var key = ((ulong)target.Fbp << 32) | ((ulong)target.Fbw << 16) | target.Psm;
            if (key == _lastSaveRtTransitionKey)
                return;
            _lastSaveRtTransitionKey = key;
        }

        var width = Math.Clamp((int)target.Fbw * 64, 1, options.Width);
        var height = options.Height;
        if (renderAudit.FramebufferTargets.TryGetValue(MakeFramebufferKey(target), out var auditRow)
            && auditRow.WriteBounds != null)
        {
            height = Math.Clamp(auditRow.WriteBounds.Y + auditRow.WriteBounds.Height, 1, options.Height);
        }

        // Use the raw VRAM contents (preserving real alpha for diagnostic comparison
        // against PCSX2's _alpha.png companions). ReadFramebufferRgba flattens alpha
        // to 255 for end-of-frame display — not what we want here.
        var rgba = ReadFramebufferRgbaRaw(target.Fbp, target.Fbw, target.Psm, width, height);
        if (rgba == null)
            return;
        options.SaveRtSink(new GsDrawRtSnapshot(
            drawIndex,
            target.Fbp,
            target.Fbw,
            target.Psm,
            target.Fbmsk,
            width,
            height,
            rgba));
    }

    private byte[]? ReadFramebufferRgbaRaw(uint fbp, uint fbw, uint psm, int width, int height)
    {
        // Like ReadFramebufferRgba but preserves the raw alpha byte for PSMCT32 / PSMZ32
        // (matches PCSX2's SaveBMP output).
        if (psm is Ps2TexPixelDecoder.PSMCT32 or Ps2GsVram.PSMZ32)
            return vram.ReadRectPSMCT32(fbp, fbw, width, height);
        return ReadFramebufferRgba(fbp, fbw, psm, width, height);
    }

    private byte[]? ReadFramebufferRgba(uint fbp, uint fbw, uint psm, int width, int height)
    {
        return psm switch
        {
            Ps2TexPixelDecoder.PSMCT32 => ReadPsmct32Framebuffer(fbp, fbw, width, height),
            Ps2GsVram.PSMZ32 => ReadPsmct32Framebuffer(fbp, fbw, width, height),
            Ps2TexPixelDecoder.PSMCT24 or Ps2GsVram.PSMZ24 => ReadPsmct24Framebuffer(fbp, fbw, width, height),
            Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => ReadPsmct16Framebuffer(fbp, fbw, psm, width, height),
            _ => null
        };
    }

    private byte[] ReadPsmct32Framebuffer(uint fbp, uint fbw, int width, int height)
    {
        var raw = vram.ReadRectPSMCT32(fbp, fbw, width, height);
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = raw[i];
            rgba[i + 1] = raw[i + 1];
            rgba[i + 2] = raw[i + 2];
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    private byte[] ReadPsmct24Framebuffer(uint fbp, uint fbw, int width, int height)
    {
        var raw = vram.ReadRectPSMCT32(fbp, fbw, width, height);
        var rgba = new byte[width * height * 4];
        for (var src = 0; src < raw.Length; src += 4)
        {
            var dst = src;
            rgba[dst] = raw[src];
            rgba[dst + 1] = raw[src + 1];
            rgba[dst + 2] = raw[src + 2];
            rgba[dst + 3] = 255;
        }

        return rgba;
    }

    private byte[] ReadPsmct16Framebuffer(uint fbp, uint fbw, uint psm, int width, int height)
    {
        var raw = psm == Ps2GsVram.PSMCT16S
            ? vram.ReadRectPSMCT16S(fbp, fbw, width, height)
            : vram.ReadRectPSMCT16(fbp, fbw, width, height);
        var rgba = new byte[width * height * 4];
        for (var src = 0; src + 1 < raw.Length; src += 2)
        {
            var pixel = raw[src] | (raw[src + 1] << 8);
            var dst = src * 2;
            rgba[dst] = Expand5(pixel & 0x1F);
            rgba[dst + 1] = Expand5((pixel >> 5) & 0x1F);
            rgba[dst + 2] = Expand5((pixel >> 10) & 0x1F);
            rgba[dst + 3] = 255;
        }

        return rgba;
    }

    private static byte Expand5(int value)
    {
        return (byte)((value << 3) | (value >> 2));
    }
}
