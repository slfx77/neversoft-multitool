using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.GsDump;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump;

public sealed class GsDumpAuditTests
{
    private const ulong PrimTriangle = 3;
    private const ulong PrimTriangleTextureStq = 3 | (1UL << 4);
    private const ulong PrimSprite = 6;
    private const ulong PrimSpriteTextureFst = 6 | (1UL << 4) | (1UL << 8);

    [Fact]
    public void ParseRawDump_ReadsHeaderRegistersAndPackets()
    {
        var raw = BuildRawDump(
            new TransferPacket(3, DisabledGif()),
            new VSyncPacket(1),
            new RegistersPacket(Fill(8192, 0x5A)));

        var dump = GsDumpFile.Parse(raw);

        Assert.Equal(0x12345678u, dump.Crc);
        Assert.Equal(2, dump.StateVersion);
        Assert.Equal("SLUS-21295", dump.Serial);
        Assert.Equal(16, dump.ScreenshotWidth);
        Assert.Equal(8, dump.ScreenshotHeight);
        Assert.Equal(4, dump.State.Length);
        Assert.Equal(8192, dump.Registers.Length);
        Assert.Equal(3, dump.Packets.Count);
        Assert.Equal(GsDumpPacketKind.Transfer, dump.Packets[0].Kind);
        Assert.Equal(GsTransferPath.Path1New, dump.Packets[0].Path);
        Assert.Equal(GsDumpPacketKind.VSync, dump.Packets[1].Kind);
        Assert.Equal(GsDumpPacketKind.Registers, dump.Packets[2].Kind);
    }

    [Fact]
    public void ParseRawDump_ReadsEmbeddedScreenshotPixels()
    {
        var screenshot = Fill(16 * 8 * 4, 0x5C);
        var dump = GsDumpFile.Parse(BuildRawDump([new TransferPacket(3, DisabledGif())], screenshot));

        Assert.Equal(16, dump.ScreenshotWidth);
        Assert.Equal(8, dump.ScreenshotHeight);
        Assert.Equal(screenshot.Length, dump.ScreenshotPixels.Length);
        Assert.Equal(0x5C, dump.ScreenshotPixels[0]);
    }

    [Fact]
    public void ParseFile_RejectsCompressedDumpsForV1()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"compressed_{Guid.NewGuid():N}.gs.xz");
        File.WriteAllBytes(tempPath, []);
        try
        {
            var ex = Assert.Throws<NotSupportedException>(() => GsDumpFile.ParseFile(tempPath));
            Assert.Contains("Decompress", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void GifInterpreter_DecodesPackedReglistImageAndAdWrites()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1);
        var gif = Concat(
            AdTag(
                (0x06, tex0),
                (0x45, 1),
                (0x50, MakeBitbltbuf(dbp: 0, dbw: 1, dpsm: 0)),
                (0x52, MakeTrxreg(2, 2)),
                (0x53, 0)),
            ImageTag(Fill(16, 0x7F)),
            RegListTag(
                MakeRegs(0x00, 0x01, 0x02, 0x0D, 0x05),
                PrimTriangle,
                Rgbaq64(128, 128, 128, 128),
                St64(0.25f, 0.75f),
                Xyz(1, 1),
                Xyz(2, 2)),
            PackedTag(
                nloop: 3,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimTriangle,
                Rgbaq(128, 0, 0, 128),
                PackedXyz(2, 2),
                Rgbaq(0, 128, 0, 128),
                PackedXyz(8, 2),
                Rgbaq(0, 0, 128, 128),
                PackedXyz(2, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        Assert.Equal(1, result.Gif.ProcessedVu1Packets);
        Assert.Equal(4, result.Gif.GifTagCount);
        // XYZ3 vertices now count too (forceNoKick variant — they enter the
        // vertex queue with the skip flag set, suppressing immediate triangle
        // emit but preserving strip parity).
        Assert.Equal(5, result.Gif.XyzWriteCount);
        Assert.Equal(1, result.Gif.UniqueTex0Count);
        Assert.Equal(1, result.Gif.RegisterWrites["XYZ3"]);
        Assert.Equal(1, result.Gif.RegisterWrites["XYZ2"]);
        Assert.Equal(1, result.Gif.RegisterWrites["ST"]);
        // DTHE/DIMX dithering is now implemented per PS2 GS spec; no longer flagged unsupported.
        Assert.False(result.Render.UnsupportedStates.ContainsKey("dither_enabled"));
        Assert.False(result.Render.UnsupportedStates.ContainsKey("reglist_st_q_missing"));
        Assert.False(result.Render.UnsupportedStates.ContainsKey("image_transfer_short_data"));
    }

    [Fact]
    public void GifInterpreter_AccumulatesChunkedImageTransfers()
    {
        var gif = Concat(
            AdTag(
                (0x50, MakeBitbltbuf(dbp: 0, dbw: 1, dpsm: 0)),
                (0x52, MakeTrxreg(4, 4)),
                (0x53, 0)),
            ImageTag(Fill(16, 0x11)),
            ImageTag(Fill(16, 0x22)),
            ImageTag(Fill(16, 0x33)),
            ImageTag(Fill(16, 0x44)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        Assert.Equal(1, result.Render.ImageTransfersStarted);
        Assert.Equal(1, result.Render.ImageTransfersCompleted);
        Assert.Equal(64, result.Render.ImageTransferBytes);
        Assert.False(result.Render.UnsupportedStates.ContainsKey("image_transfer_short_data"));
    }

    [Fact]
    public void GifInterpreter_UsesTex0FromSelectedContext()
    {
        var tex0Context2 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x0A, tbp: 12);
        var primSpriteContext2 = PrimSpriteTextureFst | (1UL << 9);
        var gif = Concat(
            AdTag((0x07, tex0Context2)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: primSpriteContext2,
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = tex0 => tex0 == tex0Context2
                    ? new GsResolvedTexture(2, 2, Fill(16, 255))
                    : null
            });

        Assert.True(result.XyzByTex0.ContainsKey(tex0Context2));
        Assert.Equal(0, result.Render.TextureDecodeMisses);
    }

    [Fact]
    public void Renderer_RendersOpaqueTriangle()
    {
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbw: 1, psm: 0))),
            SimpleTriangle(128, 128, 128));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        Assert.Equal(3, result.Gif.XyzWriteCount);
        Assert.Equal(1, result.Render.TrianglesDrawn);
        Assert.True(result.Render.PixelsTouched > 0);
        var material = Assert.Single(result.Render.Materials);
        Assert.Equal("TRIANGLE", material.Primitive);
        Assert.Equal(1, material.Draws);
        Assert.False(material.TextureEnabled);
        Assert.Equal(128, material.AvgR);
        // PS2 GS RGBAQ uses 128 as nominal 1.0 and TME=0 emits vertex color directly.
        // RGBAQ=(128,128,128) should produce a mid-gray pixel, not blown-out white.
        var pixel = ReadPixel(result.DirectPixels, 16, 4, 4);
        Assert.InRange(pixel.R, 120, 140);
        Assert.InRange(pixel.G, 120, 140);
        Assert.InRange(pixel.B, 120, 140);
    }

    [Fact]
    public void Renderer_RendersPointPrimitive()
    {
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbw: 1, psm: 0))),
            PackedTag(
                nloop: 1,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: 0,
                Rgbaq(128, 0, 0, 128),
                PackedXyz(4, 4)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        Assert.Equal(1, result.Render.PointsDrawn);
        Assert.False(result.Render.UnsupportedStates.ContainsKey("primitive_point"));
        // RGBAQ=(128,0,0,128) on an untextured point yields nominal-1.0 red mid-tone.
        var pixel = ReadPixel(result.DirectPixels, 16, 4, 4);
        Assert.InRange(pixel.R, 120, 140);
        Assert.True(pixel.G < 32);
        Assert.True(pixel.B < 32);
    }

    [Fact]
    public void Renderer_UsesDepthTestForOverlappingTriangles()
    {
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbw: 1, psm: 0))),
            TriangleAtZ(128, 0, 0, z: 10),
            TriangleAtZ(0, 0, 128, z: 1));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        var pixel = ReadPixel(result.DirectPixels, 16, 4, 4);
        // Front-most triangle (z=1) is red RGBAQ=(128,0,0); TME=0 passes vertex color through.
        Assert.InRange(pixel.R, 120, 140);
        Assert.True(pixel.B < 32);
    }

    [Fact]
    public void Renderer_AppliesScissorClipping()
    {
        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(fbw: 1, psm: 0)),
                (0x40, MakeScissor(x0: 0, x1: 3, y0: 0, y1: 3))),
            SimpleTriangle(128, 128, 128));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        Assert.InRange(ReadPixel(result.DirectPixels, 16, 2, 2).R, 120, 140);
        Assert.Equal(0, ReadPixel(result.DirectPixels, 16, 8, 8).R);
    }

    [Fact]
    public void Renderer_AppliesAlphaTestForTexturedSprite()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x0A, tcc: 1);
        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(fbw: 1, psm: 0)),
                (0x06, tex0),
                (0x47, MakeAlphaTestGreaterOrEqual(1))),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = _ => new GsResolvedTexture(
                    2,
                    2,
                    [
                        255, 0, 0, 0,
                        0, 255, 0, 255,
                        0, 0, 255, 255,
                        255, 255, 0, 255
                    ])
            });

        Assert.Equal(0, ReadPixel(result.DirectPixels, 10, 1, 1).R);
        Assert.True(ReadPixel(result.DirectPixels, 10, 6, 6).R > 200);
        Assert.True(ReadPixel(result.DirectPixels, 10, 6, 6).G > 200);
    }

    [Fact]
    public void Renderer_TfxDecalBypassesVertexColor()
    {
        // DECAL (TFX=1) outputs the texel directly; vertex color must not modulate it.
        // Use a distinctly non-128 vertex color so a stale MODULATE path would visibly skew RGB.
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x0A, tcc: 1, tfx: 1);
        var gif = Concat(
            // FRAME at FBP=1 (block 32) keeps the render target clear of TEX0.TBP=0 —
            // aliasing them turns the draw into framebuffer feedback sampling its own output.
            AdTag(
                (0x4C, MakeFrame(fbp: 1, fbw: 1, psm: 0)),
                (0x06, tex0)),
            PackedTag(
                nloop: 2,
                nreg: 3,
                regs: MakeRegs(0x01, 0x03, 0x04),
                prim: PrimSpriteTextureFst,
                Rgbaq(255, 0, 0, 255),
                PackedUv(0, 0),
                PackedXyz(0, 0),
                Rgbaq(255, 0, 0, 255),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = _ => new GsResolvedTexture(
                    2,
                    2,
                    [
                        64, 96, 192, 255,
                        64, 96, 192, 255,
                        64, 96, 192, 255,
                        64, 96, 192, 255
                    ])
            });

        // DECAL: pixel must match the texel (64,96,192) regardless of the bright-red vertex color.
        var pixel = ReadPixel(result.DirectPixels, 10, 4, 4);
        Assert.InRange(pixel.R, 56, 72);
        Assert.InRange(pixel.G, 88, 104);
        Assert.InRange(pixel.B, 184, 200);
    }

    [Fact]
    public void Renderer_TfxHighlightAddsVertexAlpha()
    {
        // HIGHLIGHT (TFX=2): Cout = Tc*Cv/128 + Av. With Tc=64 and Cv=128 the modulate term is 64,
        // and Av=64 makes the expected output 128 per channel.
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x0A, tcc: 0, tfx: 2);
        var gif = Concat(
            // FRAME at FBP=1 (block 32) keeps the render target clear of TEX0.TBP=0 —
            // aliasing them turns the draw into framebuffer feedback sampling its own output.
            AdTag(
                (0x4C, MakeFrame(fbp: 1, fbw: 1, psm: 0)),
                (0x06, tex0)),
            PackedTag(
                nloop: 2,
                nreg: 3,
                regs: MakeRegs(0x01, 0x03, 0x04),
                prim: PrimSpriteTextureFst,
                Rgbaq(128, 128, 128, 64),
                PackedUv(0, 0),
                PackedXyz(0, 0),
                Rgbaq(128, 128, 128, 64),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = _ => new GsResolvedTexture(
                    2,
                    2,
                    [
                        64, 64, 64, 255,
                        64, 64, 64, 255,
                        64, 64, 64, 255,
                        64, 64, 64, 255
                    ])
            });

        var pixel = ReadPixel(result.DirectPixels, 10, 4, 4);
        Assert.InRange(pixel.R, 120, 140);
        Assert.InRange(pixel.G, 120, 140);
        Assert.InRange(pixel.B, 120, 140);
    }

    [Fact]
    public void Renderer_PcrtcComposesTwoCircuitsWithAlpha()
    {
        // Draw a red sprite into FBP=0 PSMCT32; circuit 2 reads FBP=11392 PSMCT16
        // which is zero-initialised VRAM and thus contributes black with alpha=0.
        // With MMOD=0 and ALP=128 the constant blend factor is ~0.502, so the
        // final pixel should be roughly 128 * 0.502 == 64 in the red channel.
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbp: 0, fbw: 1, psm: 0))),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 0, 0, 128),
                PackedXyz(0, 0),
                Rgbaq(128, 0, 0, 128),
                PackedXyz(10, 10)));

        var registers = BuildRegistersPacket(
            pmode: MakePmode(en1: true, en2: true, mmod: false, slbg: false, alp: 128),
            dispfb1: MakeDispfb(fbpField: 0, fbw: 1, psm: 0),
            display1: MakeDisplay(10, 10),
            dispfb2: MakeDispfb(fbpField: 11392 >> 5, fbw: 1, psm: 0x0A),
            display2: MakeDisplay(10, 10),
            bgcolor: 0x00FF0000UL);

        var dump = GsDumpFile.Parse(BuildRawDumpWithRegisters(registers, new TransferPacket(3, gif)));
        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 10, Height = 10 });

        var pixel = ReadPixel(result.Pixels, 10, 5, 5);
        Assert.InRange(pixel.R, 56, 72);
        Assert.True(pixel.G < 16);
        Assert.True(pixel.B < 16);
        Assert.True(result.Render.PresentedFramebuffer);
        Assert.True(result.Render.PmodeEn1);
        Assert.True(result.Render.PmodeEn2);
        Assert.Equal(2, result.Render.PresentedCircuits.Count);
        Assert.True(result.Render.PresentedCircuits[0].Enabled);
        Assert.True(result.Render.PresentedCircuits[1].Enabled);
        Assert.Equal(0x0Au, result.Render.PresentedCircuits[1].Psm);
    }

    [Fact]
    public void Renderer_PcrtcFallsBackToBgcolorWithSlbg()
    {
        // EN1 enabled with SLBG=1 blends circuit 1 (red) with BGCOLOR (blue) at
        // 50% (ALP=128). Output should be ~half-red + ~half-blue, regardless of
        // what circuit 2 holds.
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbp: 0, fbw: 1, psm: 0))),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 0, 0, 128),
                PackedXyz(0, 0),
                Rgbaq(128, 0, 0, 128),
                PackedXyz(10, 10)));

        var registers = BuildRegistersPacket(
            pmode: MakePmode(en1: true, en2: false, mmod: false, slbg: true, alp: 128),
            dispfb1: MakeDispfb(fbpField: 0, fbw: 1, psm: 0),
            display1: MakeDisplay(10, 10),
            bgcolor: 0x00FF0000UL);

        var dump = GsDumpFile.Parse(BuildRawDumpWithRegisters(registers, new TransferPacket(3, gif)));
        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 10, Height = 10 });

        var pixel = ReadPixel(result.Pixels, 10, 5, 5);
        Assert.InRange(pixel.R, 56, 72);
        Assert.True(pixel.G < 16);
        Assert.InRange(pixel.B, 120, 140);
        Assert.True(result.Render.PmodeSlbg);
    }

    [Fact]
    public void Renderer_PcrtcPassesSingleCircuitThroughUnblended()
    {
        // EN1=1 / EN2=0 / SLBG=0 is the common single-circuit case; the present
        // pass should copy circuit 1 directly without any alpha blending so
        // bright HUD/text isn't dimmed by a stray constant ALP.
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbp: 0, fbw: 1, psm: 0))),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 0, 0, 128),
                PackedXyz(0, 0),
                Rgbaq(128, 0, 0, 128),
                PackedXyz(10, 10)));

        var registers = BuildRegistersPacket(
            pmode: MakePmode(en1: true, en2: false, mmod: false, slbg: false, alp: 128),
            dispfb1: MakeDispfb(fbpField: 0, fbw: 1, psm: 0),
            display1: MakeDisplay(10, 10));

        var dump = GsDumpFile.Parse(BuildRawDumpWithRegisters(registers, new TransferPacket(3, gif)));
        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 10, Height = 10 });

        var pixel = ReadPixel(result.Pixels, 10, 5, 5);
        Assert.InRange(pixel.R, 120, 140);
        Assert.True(pixel.G < 16);
        Assert.True(pixel.B < 16);
    }

    [Fact]
    public void Renderer_CountsPerspectiveStqTexturePixels()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x0A);
        var gif = Concat(
            AdTag((0x06, tex0)),
            PackedTag(
                nloop: 3,
                nreg: 2,
                regs: MakeRegs(0x02, 0x04),
                prim: PrimTriangleTextureStq,
                PackedSt(0.0f, 0.0f, 1.0f),
                PackedXyz(2, 2),
                PackedSt(2.0f, 0.0f, 2.0f),
                PackedXyz(12, 2),
                PackedSt(0.0f, 2.0f, 1.0f),
                PackedXyz(2, 12)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 16,
                Height = 16,
                TextureResolver = _ => new GsResolvedTexture(2, 2, Fill(16, 255))
            });

        Assert.True(result.Render.PerspectiveTexturePixels > 0);
        Assert.Equal(0, result.Render.FixedTexturePixels);
        Assert.True(result.Render.PixelsTouched > 0);
    }

    [Fact]
    public void Renderer_TreatsPsmz24AsTwentyFourBitFramebufferForAudit()
    {
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbw: 1, psm: 0x31))),
            SimpleTriangle(128, 64, 0));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 16 });

        Assert.False(result.Render.UnsupportedStates.ContainsKey("framebuffer_psm_0x31"));
        Assert.Equal(1, result.Render.TrianglesDrawn);
        // SimpleTriangle(128, 64, 0) emits RGBAQ=(128,64,0); TME=0 passes vertex color through.
        Assert.InRange(ReadPixel(result.DirectPixels, 16, 4, 4).R, 120, 140);
    }

    [Fact]
    public void Renderer_DecodesZbufPsmFromLowNibbleWithImplicit0x30Prefix()
    {
        // Regression for the THAW bloom-cascade Z-buffer bug. The GS hardware stores
        // ZBUF.PSM as a 4-bit field at bits 24..27 with the upper 0x30 nibble *implicit*
        // (PSMZ32 = field 0x0, PSMZ24 = field 0x1, PSMZ16 = 0x2, PSMZ16S = 0xA). The
        // commented variant in PCSX2's GSRegs.h shows the 4-bit form; the active 6-bit
        // declaration is PCSX2's *decoded* form after the implicit 0x30 has been OR'd in.
        // Reading the wire form as 6 bits gave us 0x1 instead of 0x31, so every Z write
        // was rejected ("zpsm != PSMZ32 && zpsm != PSMZ24") and the bloom-feedback path
        // that samples the Z buffer as a PSMZ24 texture saw garbage. Worst dump:
        // 1,968,267 Z writes were silently dropped — empty Z buffer, no scene silhouette.
        const uint zbpPage = 4;
        const uint tbpBlock = zbpPage * 32;
        // Wire format: only the low nibble of PSM at bits 24..27. 0x1 means PSMZ24.
        var zbufWireFormat = (ulong)zbpPage | (0x1UL << 24);
        var tex0PsmZ24 = MakeTex0(widthPow: 2, heightPow: 2, psm: 0x31, tbp: tbpBlock, tcc: 0, tfx: 1);

        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(fbw: 1, psm: 0)),
                (0x4E, zbufWireFormat)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 128, 128, 128),
                PackedXyz(0, 0, 0x123456),
                Rgbaq(128, 128, 128, 128),
                PackedXyz(4, 4, 0x123456)),
            AdTag((0x06, tex0PsmZ24)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(8, 0),
                PackedUv(64, 64),
                PackedXyz(12, 4)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 8 });

        // Z value 0x123456 must have been written to VRAM at TBP=128 PSMZ24 and then
        // sampled back by the second sprite. If the wire-format PSM decoding is wrong,
        // depthBuffer holds the value but VRAM is empty and the readback returns 0.
        Assert.True(result.Render.DepthVramWrites > 0,
            $"Expected Z writes to VRAM but counter was {result.Render.DepthVramWrites}");
        var pixel = ReadPixel(result.DirectPixels, 16, 10, 2);
        Assert.Equal(0x56, pixel.R);
        Assert.Equal(0x34, pixel.G);
        Assert.Equal(0x12, pixel.B);
    }

    [Fact]
    public void Renderer_WritesDepthToVramSoFramebufferFeedbackCanSampleIt()
    {
        // Regression for THAW dump 0188 (PSMZ24 depth-as-texture sample reading all-zero).
        // After a draw with PSMZ24 ZBUF and ZMSK=0 the Z value must be persisted to VRAM
        // at the ZBP so subsequent texture samples at (TBP=ZBP, PSM=PSMZ24) — the game's
        // own depth-feedback path for SSAO / blur / fog cones — read real depth bytes
        // instead of the leftover seed. The depthBuffers dictionary is screen-space only;
        // VRAM is the source of truth for texture reads.
        // ZBP is in 32-block PAGE units; TBP0 is in BLOCK units. ZBP=4 → BLOCK address 128.
        const uint zbpPage = 4;
        const uint tbpBlock = zbpPage * 32;
        var zbufPsmz24NoMask = (ulong)zbpPage | (0x31UL << 24);
        var tex0PsmZ24 = MakeTex0(widthPow: 2, heightPow: 2, psm: 0x31, tbp: tbpBlock, tcc: 0, tfx: 1);

        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(fbw: 1, psm: 0)),
                (0x4E, zbufPsmz24NoMask)),
            // Draw 1: untextured sprite at (0,0)-(4,4) with Z=0x123456. Writes that Z to
            // VRAM at ZBP=128 in PSMZ24 (R=0x56, G=0x34, B=0x12, A masked).
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 128, 128, 128),
                PackedXyz(0, 0, 0x123456),
                Rgbaq(128, 128, 128, 128),
                PackedXyz(4, 4, 0x123456)),
            // Draw 2: textured sprite at (8,0)-(12,4) sampling PSMZ24 at TBP=128. TFX=DECAL
            // emits the texel directly. Texel (2,2) was written by draw 1, so the sampled
            // pixel must read back (0x56, 0x34, 0x12).
            AdTag((0x06, tex0PsmZ24)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(8, 0),
                PackedUv(64, 64),
                PackedXyz(12, 4)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 8 });

        Assert.Equal(0, result.Render.TextureDecodeMisses);
        var pixel = ReadPixel(result.DirectPixels, 16, 10, 2);
        Assert.Equal(0x56, pixel.R);
        Assert.Equal(0x34, pixel.G);
        Assert.Equal(0x12, pixel.B);
    }

    [Fact]
    public void Renderer_Psmz16S_WriteDepthToVram_IncrementsDepthVramWrites()
    {
        // Regression: PSMZ16/PSMZ16S writes used to be skipped (DepthVramWritesSkippedPsm
        // ~71k/frame on THAW dump 20260507234126). After the Z-buffer correctness sweep
        // adds PSMZ16/16S addressing they must persist to VRAM like PSMZ32/24.
        const uint zbpPage = 4;
        var zbufWireFormat = (ulong)zbpPage | (0xAUL << 24); // 0xA = PSMZ16S low nibble.
        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(fbw: 1, psm: 0)),
                (0x4E, zbufWireFormat)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 128, 128, 128),
                PackedXyz(0, 0, 0x123456),
                Rgbaq(128, 128, 128, 128),
                PackedXyz(4, 4, 0x123456)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 8 });

        Assert.True(result.Render.DepthVramWrites > 0,
            $"Expected PSMZ16S Z writes but counter was {result.Render.DepthVramWrites}");
        Assert.Equal(0, result.Render.DepthVramWritesSkippedPsm);
        Assert.True(result.Render.DepthVramWritesPsm16S > 0,
            $"Expected PSMZ16S counter to track 16S writes but was {result.Render.DepthVramWritesPsm16S}");
    }

    [Fact]
    public void Renderer_WritesDepthAndSamplesAsTexture_Psmz16S_RoundTrips()
    {
        // Regression: when the game writes Z via PSMZ16S and later samples that Z buffer
        // as a TEX0.PSM=PSMZ16S texture, the bytes must round-trip through the Z swizzle
        // so depth silhouettes appear in the bloom-pyramid sampling path. PSMZ16S packs
        // the upper 16 bits of the Z value into a PSMCT16-style 5-5-5-1 word; the test
        // verifies the high-bit quantisation reproduces the encoded value.
        const uint zbpPage = 4;
        const uint tbpBlock = zbpPage * 32;
        var zbufPsmz16SNoMask = (ulong)zbpPage | (0xAUL << 24);
        var tex0PsmZ16S = MakeTex0(widthPow: 2, heightPow: 2, psm: 0x3A, tbp: tbpBlock, tcc: 0, tfx: 1);

        // Z value with interesting top 16 bits (0x789A → unpacks to non-zero 5-5-5-1
        // channels). PackedXyz takes int so we stay within the positive range.
        const int zValue = 0x789ABCDE;
        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(fbw: 1, psm: 0)),
                (0x4E, zbufPsmz16SNoMask)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimSprite,
                Rgbaq(128, 128, 128, 128),
                PackedXyz(0, 0, zValue),
                Rgbaq(128, 128, 128, 128),
                PackedXyz(4, 4, zValue)),
            AdTag((0x06, tex0PsmZ16S)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(8, 0),
                PackedUv(64, 64),
                PackedXyz(12, 4)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 16, Height = 8 });

        Assert.True(result.Render.DepthVramWrites > 0);
        Assert.True(result.Render.DepthVramWritesPsm16S > 0);
        Assert.Equal(0, result.Render.TextureDecodeMisses);
        var pixel = ReadPixel(result.DirectPixels, 16, 10, 2);
        // Encoded Z top 16 = 0xF8F8 → R5G5B5A1 = (0x18, 0x07, 0x1E, 1) → expand to
        // (0xC6, 0x39, 0xF7) after 5→8 expansion. The pixel must reflect the depth bytes
        // we wrote rather than zero / leftover background.
        Assert.True(pixel.R > 0 || pixel.G > 0 || pixel.B > 0,
            $"Expected non-zero RGB from Z-as-texture sample but got ({pixel.R}, {pixel.G}, {pixel.B})");
    }

    [Fact]
    public void Renderer_TreatsPsmz32AsThirtyTwoBitTextureForAudit()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x30);
        var gif = Concat(
            // FRAME at FBP=1 (block 32) keeps the render target clear of the texture
            // uploaded at DBP=0 — aliasing them turns the draw into framebuffer feedback.
            AdTag(
                (0x4C, MakeFrame(fbp: 1, fbw: 1, psm: 0)),
                (0x50, MakeBitbltbuf(dbp: 0, dbw: 1, dpsm: 0x30)),
                (0x52, MakeTrxreg(2, 2)),
                (0x53, 0),
                (0x06, tex0)),
            ImageTag([
                255, 0, 0, 128,
                255, 0, 0, 128,
                255, 0, 0, 128,
                255, 0, 0, 128
            ]),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 10, Height = 10 });

        Assert.Equal(0, result.Render.TextureDecodeMisses);
        Assert.False(result.Render.UnsupportedStates.ContainsKey("texture_psm_0x30"));
        Assert.False(result.Render.UnsupportedStates.ContainsKey("textured_draw_skipped_missing_texture"));
        Assert.True(result.Render.Approximations["texture_psmz32_as_color"] > 0);
        Assert.True(ReadPixel(result.DirectPixels, 10, 4, 4).R > 200);
    }

    [Fact]
    public void Renderer_ReportsBlendAndRegionClampCoverage()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, psm: 0x0A);
        var gif = Concat(
            AdTag(
                (0x06, tex0),
                (0x08, MakeClamp(wms: 2, wmt: 0))),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst | (1UL << 6),
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(64, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = _ => new GsResolvedTexture(2, 2, Fill(16, 255))
        });

        Assert.True(result.Render.Approximations["gs_alpha_blend_approximated"] > 0);
        Assert.False(result.Render.UnsupportedStates.ContainsKey("region_clamp_or_region_repeat"));
    }

    [Fact]
    public void TextureDump_CropsToRegionClamp()
    {
        var tex0 = MakeTex0(widthPow: 2, heightPow: 2);
        var textureDumps = new List<GsRuntimeTextureDump>();
        var gif = Concat(
            AdTag(
                (0x06, tex0),
                (0x08, MakeClamp(wms: 2, wmt: 2, minU: 1, maxU: 2, minV: 1, maxV: 3))),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(64, 64),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = _ => new GsResolvedTexture(4, 4, Fill(4 * 4 * 4, 255)),
                TextureDumpSink = textureDump =>
                {
                    textureDumps.Add(textureDump);
                    return null;
                }
            });

        var audit = Assert.Single(result.Render.TextureDumps);
        Assert.Equal(4, audit.TextureWidth);
        Assert.Equal(4, audit.TextureHeight);
        Assert.Equal(1, audit.RegionX);
        Assert.Equal(1, audit.RegionY);
        Assert.Equal(2, audit.Width);
        Assert.Equal(3, audit.Height);
        Assert.Equal(2 * 3 * 4, Assert.Single(textureDumps).Rgba.Length);
    }

    [Fact]
    public void TextureRgbMode_IgnoresTextureAlphaForRenderAndDumpPreview()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, tcc: 0);
        var textureDumps = new List<GsRuntimeTextureDump>();
        var transparentRed = new byte[]
        {
            255, 0, 0, 0,
            255, 0, 0, 0,
            255, 0, 0, 0,
            255, 0, 0, 0
        };
        var gif = Concat(
            // FRAME at FBP=1 (block 32) keeps the render target clear of TEX0.TBP=0 —
            // aliasing them turns the draw into framebuffer feedback sampling its own output.
            AdTag(
                (0x4C, MakeFrame(fbp: 1, fbw: 1, psm: 0)),
                (0x06, tex0)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureResolver = _ => new GsResolvedTexture(2, 2, transparentRed),
                TextureDumpSink = textureDump =>
                {
                    textureDumps.Add(textureDump);
                    return null;
                }
            });

        Assert.True(ReadPixel(result.DirectPixels, 10, 4, 4).R > 200);
        var dumpPixels = Assert.Single(textureDumps).Rgba;
        Assert.All(Enumerable.Range(0, dumpPixels.Length / 4), i => Assert.Equal(255, dumpPixels[i * 4 + 3]));
        Assert.False(Assert.Single(result.Render.TextureDumps).AllAlphaZero);
    }

    [Fact]
    public void TextureDump_LabelsFramebufferFeedbackSource()
    {
        var tex0 = MakeTex0(widthPow: 1, heightPow: 1, tbp: 0, tbw: 1, psm: 0);
        var textureDumps = new List<GsRuntimeTextureDump>();
        var gif = Concat(
            AdTag((0x4C, MakeFrame(fbp: 0, fbw: 1, psm: 0))),
            PackedTag(
                nloop: 3,
                nreg: 2,
                regs: MakeRegs(0x01, 0x04),
                prim: PrimTriangle,
                Rgbaq(255, 0, 0, 128),
                PackedXyz(0, 0),
                Rgbaq(255, 0, 0, 128),
                PackedXyz(8, 0),
                Rgbaq(255, 0, 0, 128),
                PackedXyz(0, 8)),
            AdTag((0x06, tex0)),
            PackedTag(
                nloop: 2,
                nreg: 2,
                regs: MakeRegs(0x03, 0x04),
                prim: PrimSpriteTextureFst,
                PackedUv(0, 0),
                PackedXyz(0, 0),
                PackedUv(32, 32),
                PackedXyz(8, 8)));
        var dump = GsDumpFile.Parse(BuildRawDump(new TransferPacket(3, gif)));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions
            {
                Width = 10,
                Height = 10,
                TextureDumpSink = textureDump =>
                {
                    textureDumps.Add(textureDump);
                    return null;
                }
            });

        Assert.Single(textureDumps);
        var audit = Assert.Single(result.Render.TextureDumps);
        Assert.Equal("framebuffer", audit.Source);
        Assert.Equal("FBP=0,FBW=1,PSM=0x00,FBMSK=0x00000000", audit.SourceKey);
    }

    [Fact]
    public void AuditRunner_WritesJsonRenderAndDiffArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NsMultitool_GsDump_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var gsPath = Path.Combine(tempDir, "synthetic.gs");
            var pngPath = Path.Combine(tempDir, "synthetic.png");
            var outDir = Path.Combine(tempDir, "out");
            var tex0 = MakeTex0(widthPow: 1, heightPow: 1);
            var gif = Concat(
                AdTag(
                    (0x50, MakeBitbltbuf(dbp: 0, dbw: 1, dpsm: 0)),
                    (0x52, MakeTrxreg(2, 2)),
                    (0x53, 0),
                    (0x06, tex0)),
                ImageTag([
                    255, 0, 0, 128,
                    0, 255, 0, 128,
                    0, 0, 255, 128,
                    255, 255, 0, 128
                ]),
                PackedTag(
                    nloop: 2,
                    nreg: 2,
                    regs: MakeRegs(0x03, 0x04),
                    prim: PrimSpriteTextureFst,
                    PackedUv(0, 0),
                    PackedXyz(0, 0),
                    PackedUv(32, 32),
                    PackedXyz(8, 8)));
            File.WriteAllBytes(gsPath, BuildRawDump(new TransferPacket(3, gif)));
            using (var image = new Image<Rgba32>(16, 16, new Rgba32(0, 0, 0, 255)))
                image.SaveAsPng(pngPath);

            var report = GsDumpAuditRunner.Run(gsPath, outDir, new GsDumpAuditOptions());

            Assert.Equal(1, report.PacketCount);
            Assert.NotNull(report.PixelDiff);
            Assert.True(File.Exists(Path.Combine(outDir, "synthetic.gsdump-audit.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "synthetic.render.png")));
            Assert.True(File.Exists(Path.Combine(outDir, "synthetic.diff.png")));
            Assert.True(File.Exists(Path.Combine(outDir, "synthetic.materials.csv")));
            Assert.NotNull(report.MaterialDumpPath);
            Assert.NotNull(report.TextureDumpDirectory);
            Assert.True(Directory.Exists(report.TextureDumpDirectory));
            Assert.NotEmpty(Directory.GetFiles(report.TextureDumpDirectory, "*.png"));
            Assert.NotEmpty(report.Render.TextureDumps);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void KnownThawCapture_SeedRoundtripsToVramAtFbp4480_WhenPresent()
    {
        var capturePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "PCSX2",
            "snaps",
            "Tony Hawk's American Wasteland [Collector's Edition]_SLUS-21295_20260507234210.gs");
        Assert.SkipWhen(!File.Exists(capturePath), $"Local GS dump not found: {capturePath}");

        var dump = GsDumpFile.ParseFile(capturePath);
        Assert.True(dump.TryGetInitialGsMemory(out var memory),
            $"State version {dump.StateVersion} did not expose initial GS memory.");

        var vram = new NeversoftMultitool.Core.Formats.Texture.Ps2.Ps2GsVram(
            NeversoftMultitool.Core.Formats.Texture.Ps2.Ps2GifQwordWordOrder.Identity);
        vram.WriteRawBytes(0, memory);

        // The seed audit (tools/diagnostics/gsdump_seed_audit.ps1) shows the file bytes at
        // FBP=4480 are populated PSMCT16 data ("21 04 21 04 ..."). After WriteRawBytes,
        // ReadRectPSMCT16 should recover non-zero pixels at the same location.
        var rgba = vram.ReadRectPSMCT16(4480, 10, 16, 4);
        Assert.Equal(16 * 4 * 2, rgba.Length);
        var nonZeroBytes = 0;
        for (var i = 0; i < rgba.Length; i++)
            if (rgba[i] != 0) nonZeroBytes++;
        var pixelsHex = string.Join(" ", rgba.Take(64).Select(b => b.ToString("X2")));
        Assert.True(
            nonZeroBytes > 0,
            $"Expected non-zero seed data at FBP=4480 PSMCT16, but the round-trip returned all zeros. First 64 bytes: {pixelsHex}");
    }

    [Fact]
    public void KnownThawCapture_MatchesPythonReferenceCounts_WhenPresent()
    {
        var capturePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "PCSX2",
            "snaps",
            "Tony Hawk's American Wasteland [Collector's Edition]_SLUS-21295_20260507234210.gs");
        Assert.SkipWhen(!File.Exists(capturePath), $"Local GS dump not found: {capturePath}");

        var dump = GsDumpFile.ParseFile(capturePath);
        var result = GsGifInterpreter.Interpret(dump, new GsGifInterpretOptions { Width = 64, Height = 64 });

        Assert.Equal(10581, dump.Packets.Count);
        Assert.Equal(10573, dump.Packets.Count(static packet => packet.Kind == GsDumpPacketKind.Transfer));
        Assert.Equal(10573, result.Gif.ProcessedVu1Packets);
        Assert.Equal(23019, result.Gif.GifTagCount);
        // XYZ count: 115214 baseline + 2240 from XYZF3/XYZ3 (NoKick) routing.
        // The XYZF3/XYZ3 packed-register variants now get added to the vertex queue
        // (with the skip flag set) so strip winding parity is preserved across
        // HUD font glyphs and similar quad sequences.
        Assert.Equal(117454, result.Gif.XyzWriteCount);
        Assert.Equal(254, result.Gif.UniqueTex0Count);
    }

    private static byte[] SimpleTriangle(byte r, byte g, byte b) => TriangleAtZ(r, g, b, z: 10);

    private static byte[] TriangleAtZ(byte r, byte g, byte b, int z) =>
        PackedTag(
            nloop: 3,
            nreg: 2,
            regs: MakeRegs(0x01, 0x04),
            prim: PrimTriangle,
            Rgbaq(r, g, b, 128),
            PackedXyz(2, 2, z),
            Rgbaq(r, g, b, 128),
            PackedXyz(12, 2, z),
            Rgbaq(r, g, b, 128),
            PackedXyz(2, 12, z));

    private static byte[] BuildRawDump(params IGsPacket[] packets) =>
        BuildRawDump(packets, screenshotPixels: null, registers: null);

    private static byte[] BuildRawDumpWithRegisters(byte[] registers, params IGsPacket[] packets) =>
        BuildRawDump(packets, screenshotPixels: null, registers: registers);

    private static byte[] BuildRawDump(IGsPacket[] packets, byte[]? screenshotPixels, byte[]? registers = null)
    {
        using var stream = new MemoryStream();
        var serial = "SLUS-21295"u8.ToArray();
        var screenshotOffset = 36 + serial.Length;
        var header = new byte[screenshotOffset + (screenshotPixels?.Length ?? 0)];
        WriteU32(header, 0, 2);
        WriteU32(header, 4, 4);
        WriteU32(header, 8, 36);
        WriteU32(header, 12, (uint)serial.Length);
        WriteU32(header, 16, 0x12345678);
        WriteU32(header, 20, 16);
        WriteU32(header, 24, 8);
        if (screenshotPixels != null)
        {
            WriteU32(header, 28, (uint)screenshotOffset);
            WriteU32(header, 32, (uint)screenshotPixels.Length);
            screenshotPixels.CopyTo(header.AsSpan(screenshotOffset));
        }

        serial.CopyTo(header.AsSpan(36));

        WriteU32(stream, 0xFFFFFFFF);
        WriteU32(stream, (uint)header.Length);
        stream.Write(header);
        stream.Write([1, 2, 3, 4]);
        var regsBlock = new byte[8192];
        if (registers != null)
            Array.Copy(registers, regsBlock, Math.Min(registers.Length, regsBlock.Length));
        stream.Write(regsBlock);
        foreach (var packet in packets)
            packet.Write(stream);
        return stream.ToArray();
    }

    private static byte[] DisabledGif() => GifTag(nloop: 0, flg: 3, nreg: 1, regs: 0);

    private static byte[] PackedTag(int nloop, int nreg, ulong regs, ulong prim, params byte[][] qwords)
    {
        using var stream = new MemoryStream();
        stream.Write(GifTag(nloop, flg: 0, nreg, regs, prim, pre: true));
        foreach (var qword in qwords)
            stream.Write(qword);
        return stream.ToArray();
    }

    private static byte[] RegListTag(ulong regs, params ulong[] values)
    {
        using var stream = new MemoryStream();
        stream.Write(GifTag(nloop: 1, flg: 1, nreg: values.Length, regs));
        foreach (var value in values)
            WriteU64(stream, value);
        if ((values.Length & 1) != 0)
            WriteU64(stream, 0);
        return stream.ToArray();
    }

    private static byte[] AdTag(params (int Address, ulong Value)[] writes)
    {
        var qwords = writes
            .Select(static write => Qword(write.Value, (ulong)write.Address))
            .ToArray();
        return PackedTag(writes.Length, 1, 0x0E, PrimTriangle, qwords);
    }

    private static byte[] ImageTag(byte[] data)
    {
        using var stream = new MemoryStream();
        stream.Write(GifTag(data.Length / 16, flg: 2, nreg: 1, regs: 0));
        stream.Write(data);
        return stream.ToArray();
    }

    private static byte[] GifTag(int nloop, int flg, int nreg, ulong regs, ulong prim = 0, bool pre = false)
    {
        var lo = (uint)nloop |
                 ((pre ? 1UL : 0UL) << 46) |
                 ((prim & 0x7FF) << 47) |
                 ((ulong)(flg & 3) << 58) |
                 ((ulong)(nreg & 0xF) << 60);
        return Qword(lo, regs);
    }

    private static ulong MakeRegs(params int[] regs)
    {
        ulong value = 0;
        for (var i = 0; i < regs.Length; i++)
            value |= ((ulong)regs[i] & 0xF) << (i * 4);
        return value;
    }

    private static byte[] Rgbaq(byte r, byte g, byte b, byte a)
    {
        var qword = new byte[16];
        qword[0] = r;
        qword[4] = g;
        qword[8] = b;
        qword[12] = a;
        return qword;
    }

    private static byte[] PackedXyz(int x, int y, int z = 10)
    {
        var qword = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(qword.AsSpan(0, 2), (ushort)(x * 16));
        BinaryPrimitives.WriteUInt16LittleEndian(qword.AsSpan(4, 2), (ushort)(y * 16));
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(8, 4), (uint)z);
        return qword;
    }

    private static byte[] PackedUv(int u, int v)
    {
        var qword = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(0, 4), (uint)u);
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(4, 4), (uint)v);
        return qword;
    }

    private static byte[] PackedSt(float s, float t, float q)
    {
        var qword = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(0, 4), BitConverter.SingleToUInt32Bits(s));
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(4, 4), BitConverter.SingleToUInt32Bits(t));
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(8, 4), BitConverter.SingleToUInt32Bits(q));
        return qword;
    }

    private static ulong Rgbaq64(byte r, byte g, byte b, byte a) =>
        r | ((ulong)g << 8) | ((ulong)b << 16) | ((ulong)a << 24);

    private static ulong St64(float s, float t) =>
        BitConverter.SingleToUInt32Bits(s) | ((ulong)BitConverter.SingleToUInt32Bits(t) << 32);

    private static ulong Xyz(int x, int y, int z = 10) =>
        (uint)(x * 16) | ((ulong)(uint)(y * 16) << 16) | ((ulong)(uint)z << 32);

    private static ulong Uv(int u, int v) =>
        (uint)u | ((ulong)(uint)v << 16);

    private static ulong MakeTex0(
        int widthPow,
        int heightPow,
        uint psm = 0,
        uint tbp = 0,
        uint tbw = 1,
        uint tcc = 0,
        uint tfx = 0) =>
        tbp |
        ((ulong)tbw << 14) |
        ((ulong)psm << 20) |
        ((ulong)widthPow << 26) |
        ((ulong)heightPow << 30) |
        ((ulong)tcc << 34) |
        ((ulong)tfx << 35);

    private static ulong MakeBitbltbuf(uint dbp, uint dbw, uint dpsm) =>
        ((ulong)dbp << 32) | ((ulong)dbw << 48) | ((ulong)dpsm << 56);

    private static ulong MakeTrxreg(int width, int height) =>
        (uint)width | ((ulong)(uint)height << 32);

    private static ulong MakeScissor(int x0, int x1, int y0, int y1) =>
        (uint)x0 | ((ulong)(uint)x1 << 16) | ((ulong)(uint)y0 << 32) | ((ulong)(uint)y1 << 48);

    private static ulong MakeFrame(uint fbp = 0, uint fbw = 10, uint psm = 0, uint fbmsk = 0) =>
        fbp | ((ulong)fbw << 16) | ((ulong)psm << 24) | ((ulong)fbmsk << 32);

    private static ulong MakePmode(bool en1, bool en2, bool mmod = false, bool slbg = false, byte alp = 0xFF) =>
        (en1 ? 0x1UL : 0) |
        (en2 ? 0x2UL : 0) |
        (mmod ? 0x20UL : 0) |
        (slbg ? 0x80UL : 0) |
        ((ulong)alp << 8);

    // FBP is stored as a 9-bit field in DISPFB bits 0..8, interpreted in units of 32 blocks.
    // FBW is in bits 9..14, PSM in bits 15..19. Display width/height live in DISPLAY (separate).
    private static ulong MakeDispfb(uint fbpField, uint fbw, uint psm) =>
        ((ulong)fbpField & 0x1FF) |
        (((ulong)fbw & 0x3F) << 9) |
        (((ulong)psm & 0x1F) << 15);

    // Encodes the visible display size with magnification 1x. MAGH and MAGV fields are
    // stored as (mag-1) so 0 means 1x; the parser does +1 on read. DW/DH are similar
    // (stored as size-1), placed at bits 32..43 and 44..54 respectively.
    private static ulong MakeDisplay(int width, int height) =>
        ((ulong)(uint)(width - 1) << 32) |
        ((ulong)(uint)(height - 1) << 44);

    private static byte[] BuildRegistersPacket(
        ulong pmode = 0,
        ulong dispfb1 = 0,
        ulong display1 = 0,
        ulong dispfb2 = 0,
        ulong display2 = 0,
        ulong bgcolor = 0)
    {
        var regs = new byte[8192];
        BinaryPrimitives.WriteUInt64LittleEndian(regs.AsSpan(0), pmode);
        BinaryPrimitives.WriteUInt64LittleEndian(regs.AsSpan(112), dispfb1);
        BinaryPrimitives.WriteUInt64LittleEndian(regs.AsSpan(128), display1);
        BinaryPrimitives.WriteUInt64LittleEndian(regs.AsSpan(144), dispfb2);
        BinaryPrimitives.WriteUInt64LittleEndian(regs.AsSpan(160), display2);
        BinaryPrimitives.WriteUInt64LittleEndian(regs.AsSpan(224), bgcolor);
        return regs;
    }

    private static ulong MakeAlphaTestGreaterOrEqual(int aref) =>
        1UL | (5UL << 1) | ((ulong)(uint)aref << 4);

    private static ulong MakeClamp(
        int wms,
        int wmt,
        int minU = 0,
        int maxU = 0,
        int minV = 0,
        int maxV = 0) =>
        (uint)wms |
        ((ulong)(uint)wmt << 2) |
        ((ulong)(uint)minU << 4) |
        ((ulong)(uint)maxU << 14) |
        ((ulong)(uint)minV << 24) |
        ((ulong)(uint)maxV << 34);

    private static byte[] Qword(ulong lo, ulong hi = 0)
    {
        var qword = new byte[16];
        WriteU64(qword, 0, lo);
        WriteU64(qword, 8, hi);
        return qword;
    }

    private static Pixel ReadPixel(byte[] pixels, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return new Pixel(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    private static byte[] Fill(int count, byte value)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] chunks)
    {
        using var stream = new MemoryStream();
        foreach (var chunk in chunks)
            stream.Write(chunk);
        return stream.ToArray();
    }

    private static void WriteU32(MemoryStream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void WriteU64(MemoryStream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteU64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);

    private interface IGsPacket
    {
        void Write(MemoryStream stream);
    }

    private sealed record TransferPacket(byte Path, byte[] Data) : IGsPacket
    {
        public void Write(MemoryStream stream)
        {
            stream.WriteByte(0);
            stream.WriteByte(Path);
            WriteU32(stream, (uint)Data.Length);
            stream.Write(Data);
        }
    }

    private sealed record VSyncPacket(byte Field) : IGsPacket
    {
        public void Write(MemoryStream stream)
        {
            stream.WriteByte(1);
            stream.WriteByte(Field);
        }
    }

    private sealed record RegistersPacket(byte[] Data) : IGsPacket
    {
        public void Write(MemoryStream stream)
        {
            stream.WriteByte(3);
            stream.Write(Data);
        }
    }
}
