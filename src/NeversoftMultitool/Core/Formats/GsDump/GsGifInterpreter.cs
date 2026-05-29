using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed partial class GsGifInterpreter
{
    private const int RegisterAd = 0x0E;

    private static readonly Dictionary<int, string> PackedRegisterNames = new()
    {
        [0x00] = "PRIM",
        [0x01] = "RGBAQ",
        [0x02] = "ST",
        [0x03] = "UV",
        [0x04] = "XYZF2",
        [0x05] = "XYZ2",
        [0x06] = "TEX0_1",
        [0x07] = "TEX0_2",
        [0x08] = "CLAMP_1",
        [0x09] = "CLAMP_2",
        [0x0A] = "FOG",
        [0x0C] = "XYZF3",
        [0x0D] = "XYZ3",
        [0x0E] = "A+D",
        [0x0F] = "NOP"
    };

    private static readonly Dictionary<int, string> AdRegisterNames = new()
    {
        [0x00] = "PRIM",
        [0x01] = "RGBAQ",
        [0x02] = "ST",
        [0x03] = "UV",
        [0x04] = "XYZF2",
        [0x05] = "XYZ2",
        [0x06] = "TEX0_1",
        [0x07] = "TEX0_2",
        [0x08] = "CLAMP_1",
        [0x09] = "CLAMP_2",
        [0x0A] = "FOG",
        [0x0C] = "XYZF3",
        [0x0D] = "XYZ3",
        [0x14] = "TEX1_1",
        [0x15] = "TEX1_2",
        [0x16] = "TEX2_1",
        [0x17] = "TEX2_2",
        [0x18] = "XYOFFSET_1",
        [0x19] = "XYOFFSET_2",
        [0x1A] = "PRMODECONT",
        [0x1B] = "PRMODE",
        [0x1C] = "TEXCLUT",
        [0x22] = "SCANMSK",
        [0x34] = "MIPTBP1_1",
        [0x35] = "MIPTBP1_2",
        [0x36] = "MIPTBP2_1",
        [0x37] = "MIPTBP2_2",
        [0x3B] = "TEXA",
        [0x3D] = "FOGCOL",
        [0x3F] = "TEXFLUSH",
        [0x40] = "SCISSOR_1",
        [0x41] = "SCISSOR_2",
        [0x42] = "ALPHA_1",
        [0x43] = "ALPHA_2",
        [0x44] = "DIMX",
        [0x45] = "DTHE",
        [0x46] = "COLCLAMP",
        [0x47] = "TEST_1",
        [0x48] = "TEST_2",
        [0x49] = "PABE",
        [0x4A] = "FBA_1",
        [0x4B] = "FBA_2",
        [0x4C] = "FRAME_1",
        [0x4D] = "FRAME_2",
        [0x4E] = "ZBUF_1",
        [0x4F] = "ZBUF_2",
        [0x50] = "BITBLTBUF",
        [0x51] = "TRXPOS",
        [0x52] = "TRXREG",
        [0x53] = "TRXDIR",
        [0x54] = "HWREG",
        [0x60] = "SIGNAL",
        [0x61] = "FINISH",
        [0x62] = "LABEL"
    };

    private static readonly string[] PrimitiveNames =
    [
        "POINT",
        "LINE",
        "LINE_STRIP",
        "TRIANGLE",
        "TRIANGLE_STRIP",
        "TRIANGLE_FAN",
        "SPRITE",
        "INVALID"
    ];

    private readonly Dictionary<string, AlphaFailureAccumulator> alphaFailureDraws = [];
    private readonly Dictionary<ulong, float[]> depthBuffers = [];
    private readonly HashSet<string> dumpedTextureKeys = [];
    private readonly GsGifAudit gifAudit = new();
    private readonly Dictionary<string, GsMaterialAccumulator> materialDraws = [];
    private readonly Dictionary<string, MissingTextureDrawAccumulator> missingTextureDraws = [];

    private readonly GsGifInterpretOptions options;
    private readonly byte[] pixels;
    private readonly GsRenderAudit renderAudit = new();
    private readonly GsState state = new();
    private readonly Dictionary<ulong, long> tex0Writes = [];
    private readonly Dictionary<GsTextureCacheKey, GsTexture?> textureCache = [];
    private readonly Ps2GsVram vram = new();
    private readonly Dictionary<ulong, long> xyzByTex0 = [];
    private GsImageTransfer? activeImageTransfer;
    private byte[]? directPixels;
    // Tracks the prior (Fbp << 32) | (Fbw << 16) | Psm captured by MaybeSaveDrawRt when
    // options.SaveRtOnStateTransition is set. ulong.MaxValue is a sentinel "no prior key"
    // so the first qualifying draw always emits an RT.
    private ulong _lastSaveRtTransitionKey = ulong.MaxValue;

    private GsGifInterpreter(GsGifInterpretOptions options)
    {
        this.options = options;
        renderAudit.Width = options.Width;
        renderAudit.Height = options.Height;
        renderAudit.CoordinateScaleX = options.CoordinateScaleX;
        renderAudit.CoordinateScaleY = options.CoordinateScaleY;
        pixels = new byte[options.Width * options.Height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
            pixels[i + 3] = 255;
    }

    public static GsGifInterpretation Interpret(GsDumpFile dump, GsGifInterpretOptions options)
    {
        var interpreter = new GsGifInterpreter(options);
        interpreter.Interpret(dump);
        interpreter.gifAudit.RegisterWrites = interpreter.gifAudit.RegisterWrites
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);
        interpreter.gifAudit.XyzWritesByPrimitive = interpreter.gifAudit.XyzWritesByPrimitive
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);
        interpreter.gifAudit.TopTex0ByXyz = interpreter.xyzByTex0
            .OrderByDescending(static kv => kv.Value)
            .Take(64)
            .Select(static kv => MakeTex0Row(kv.Key, kv.Value))
            .ToList();
        interpreter.gifAudit.UniqueTex0Count = interpreter.tex0Writes.Count;
        foreach (var row in interpreter.renderAudit.FramebufferTargets.Values)
        {
            row.Tex0Pixels = row.Tex0Pixels
                .OrderByDescending(static kv => kv.Value)
                .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
                .Take(16)
                .ToDictionary(static kv => kv.Key, static kv => kv.Value);
        }

        interpreter.renderAudit.FramebufferTargets = interpreter.renderAudit.FramebufferTargets
            .OrderByDescending(static kv => kv.Value.PixelsWritten)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);
        interpreter.renderAudit.ImageTransferTargets = interpreter.renderAudit.ImageTransferTargets
            .OrderByDescending(static kv => kv.Value.Bytes)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);
        interpreter.renderAudit.MissingTextureDraws = interpreter.missingTextureDraws.Values
            .OrderByDescending(static row => row.Draws)
            .ThenBy(static row => row.Tex0)
            .Take(32)
            .Select(static row => row.ToAuditRow())
            .ToList();
        interpreter.renderAudit.AlphaFailureDraws = interpreter.alphaFailureDraws.Values
            .OrderByDescending(static row => row.Pixels)
            .ThenBy(static row => row.Tex0)
            .Take(32)
            .Select(static row => row.ToAuditRow())
            .ToList();
        interpreter.renderAudit.Materials = interpreter.materialDraws.Values
            .OrderByDescending(static row => row.PixelsWritten)
            .ThenByDescending(static row => row.Draws)
            .ThenBy(static row => row.Key, StringComparer.Ordinal)
            .Select(static row => row.ToAuditRow())
            .ToList();
        var framebufferSnapshots = interpreter.BuildFramebufferSnapshots();
        interpreter.renderAudit.FramebufferSnapshots = framebufferSnapshots
            .Select(static row => new GsFramebufferSnapshotAudit
            {
                Key = row.Key,
                Fbp = row.Fbp,
                Fbw = row.Fbw,
                Psm = row.Psm,
                Fbmsk = row.Fbmsk,
                Width = row.Width,
                Height = row.Height,
                NonBlackPixels = row.NonBlackPixels
            })
            .ToList();
        return new GsGifInterpretation
        {
            Gif = interpreter.gifAudit,
            Render = interpreter.renderAudit,
            DirectPixels = interpreter.directPixels ?? interpreter.pixels,
            Pixels = interpreter.pixels,
            FramebufferSnapshots = framebufferSnapshots,
            XyzByTex0 = interpreter.xyzByTex0
        };
    }

    private void Interpret(GsDumpFile dump)
    {
        LoadInitialState(dump);
        var vsyncCount = 0;
        foreach (var packet in dump.Packets)
        {
            if (packet.Kind == GsDumpPacketKind.VSync)
            {
                vsyncCount++;
                if (options.MaxVsync.HasValue && vsyncCount >= options.MaxVsync.Value)
                    break;
                continue;
            }

            if (packet.Kind != GsDumpPacketKind.Transfer)
                continue;

            if (packet.Path is GsTransferPath.Path1Old or GsTransferPath.Path1New)
            {
                ParseGifPackets(packet.Data);
                gifAudit.ProcessedVu1Packets++;
            }
            else
            {
                gifAudit.SkippedTransferPackets++;
            }
        }

        if (activeImageTransfer is { BytesWritten: > 0 } transfer)
            NoteUnsupported($"image_transfer_incomplete_{transfer.BytesWritten}_of_{transfer.ExpectedBytes}");

        directPixels = pixels.ToArray();
        PresentFromDisplayBuffer(dump);
    }

    private void LoadInitialState(GsDumpFile dump)
    {
        if (dump.StateVersion < 9 || dump.State.Length < 425)
        {
            NoteApproximation("initial_gs_state_snapshot_not_decoded");
            return;
        }

        var data = dump.State.AsSpan();

        static ulong U64(ReadOnlySpan<byte> source, int offset)
        {
            return offset >= 0 && offset + 8 <= source.Length
                ? BinaryPrimitives.ReadUInt64LittleEndian(source[offset..])
                : 0;
        }

        static uint U32(ReadOnlySpan<byte> source, int offset)
        {
            return offset >= 0 && offset + 4 <= source.Length
                ? BinaryPrimitives.ReadUInt32LittleEndian(source[offset..])
                : 0;
        }

        state.Prim = U64(data, 4);
        state.PrModeCont = U64(data, 12);
        state.Texa = U64(data, 36);
        state.FogColor = U64(data, 44);
        state.Dthe = U64(data, 60);
        state.Bitbltbuf = U64(data, 84);
        state.Trxdir = U64(data, 92);
        state.Trxpos = U64(data, 100);
        state.Trxreg = U64(data, 108);

        for (var i = 0; i < 2; i++)
        {
            var context = state.Contexts[i];
            var offset = 124 + i * 96;
            context.XyOffset = U64(data, offset);
            context.Tex0 = U64(data, offset + 8);
            context.Tex1 = U64(data, offset + 16);
            context.Clamp = U64(data, offset + 24);
            context.Scissor = U64(data, offset + 48);
            context.Alpha = U64(data, offset + 56);
            context.Test = U64(data, offset + 64);
            context.Fba = U64(data, offset + 72);
            context.Frame = U64(data, offset + 80);
            context.Zbuf = U64(data, offset + 88);
        }

        SetRgbaq(U64(data, 316));
        SetSt(U64(data, 324));
        SetUv(U32(data, 332));

        if (dump.TryGetInitialGsMemory(out var memory))
        {
            vram.WriteRawBytes(0, memory);
            renderAudit.InitialGsMemorySeeded = true;
            textureCache.Clear();
        }
        else
        {
            NoteApproximation("initial_gs_local_memory_not_decoded");
        }
    }

    private void ParseGifPackets(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + 16 <= data.Length)
        {
            var tagLo = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            var tagHi = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]);
            var nloop = (int)(tagLo & 0x7FFF);
            var pre = ((tagLo >> 46) & 1) != 0;
            var prim = (tagLo >> 47) & 0x7FF;
            var flg = (int)((tagLo >> 58) & 3);
            var nreg = (int)((tagLo >> 60) & 0xF);
            if (nreg == 0)
                nreg = 16;

            gifAudit.GifTagCount++;
            if (pre)
                SetPrim(prim);

            offset += 16;
            switch (flg)
            {
                case 0:
                {
                    var bodyBytes = nloop * nreg * 16;
                    if (offset + bodyBytes > data.Length)
                    {
                        NoteUnsupported("truncated_packed_gif_body");
                        return;
                    }

                    for (var loop = 0; loop < nloop; loop++)
                    {
                        for (var regIndex = 0; regIndex < nreg; regIndex++)
                        {
                            var reg = (int)((tagHi >> (regIndex * 4)) & 0xF);
                            ProcessPackedRegister(reg, data.Slice(offset, 16));
                            offset += 16;
                        }
                    }

                    break;
                }
                case 1:
                {
                    var itemCount = nloop * nreg;
                    var bodyBytes = (itemCount * 8 + 15) & ~15;
                    if (offset + bodyBytes > data.Length)
                    {
                        NoteUnsupported("truncated_reglist_gif_body");
                        return;
                    }

                    for (var i = 0; i < itemCount; i++)
                    {
                        var reg = (int)((tagHi >> (i % nreg * 4)) & 0xF);
                        var value = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + i * 8)..]);
                        ProcessRegisterValue(reg, value, false);
                    }

                    offset += bodyBytes;
                    break;
                }
                case 2:
                {
                    var imageBytes = nloop * 16;
                    if (offset + imageBytes > data.Length)
                    {
                        NoteUnsupported("truncated_image_gif_body");
                        return;
                    }

                    WriteImageData(data.Slice(offset, imageBytes));
                    offset += imageBytes;
                    break;
                }
                case 3:
                    break;
            }
        }
    }

    private void ProcessPackedRegister(int reg, ReadOnlySpan<byte> qword)
    {
        AddRegWrite(PackedRegisterNames.GetValueOrDefault(reg, $"?{reg:X}"));
        if (reg == RegisterAd)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(qword);
            var addr = qword[8];
            var name = AdRegisterNames.GetValueOrDefault(addr, $"?AD{addr:02X}");
            AddRegWrite("AD:" + name);
            ProcessAdRegister(addr, value);
            return;
        }

        var lo = BinaryPrimitives.ReadUInt64LittleEndian(qword);
        ProcessRegisterValue(reg, lo, true, qword);
    }

    private void ProcessRegisterValue(int reg, ulong value, bool packed, ReadOnlySpan<byte> qword = default)
    {
        if (!packed)
            AddRegWrite(PackedRegisterNames.GetValueOrDefault(reg, $"?{reg:X}"));

        switch (reg)
        {
            case 0x00:
                SetPrim(value);
                break;
            case 0x01:
                if (packed && qword.Length >= 16)
                    SetPackedRgba(qword);
                else
                    SetRgbaq(value);
                break;
            case 0x02:
                if (packed && qword.Length >= 12)
                    SetSt(qword);
                else
                    SetSt(value);
                break;
            case 0x03:
                if (packed && qword.Length >= 8)
                    SetPackedUv(qword);
                else
                    SetUv(value);
                break;
            case 0x04:
            case 0x05:
                if (packed && qword.Length >= 16)
                    ProcessPackedXyz(qword, reg == 0x04);
                else
                    ProcessXyz(value, reg == 0x04);
                break;
            case 0x0A:
                SetFog(value);
                break;
            case 0x0C:
            case 0x0D:
                // XYZF3 (0x0C) / XYZ3 (0x0D): force-NoKick vertex variants used for HUD font
                // strip-restart between glyphs. The vertex IS added to the strip buffer so it
                // affects winding parity, but the triangle that would include it is suppressed
                // (PCSX2 GIFRegHandlerXYZ3 / GIFPackedRegHandlerXYZ2 with adc=1).
                if (packed && qword.Length >= 16)
                    ProcessPackedXyz(qword, reg == 0x0C, true);
                else
                    ProcessXyz(value, reg == 0x0C, true);
                break;
            case 0x06:
                SetTex0(value, 0);
                break;
            case 0x07:
                SetTex0(value, 1);
                break;
            case 0x08:
                state.Contexts[0].Clamp = value;
                break;
            case 0x09:
                state.Contexts[1].Clamp = value;
                break;
        }
    }

    private void ProcessAdRegister(int addr, ulong value)
    {
        switch (addr)
        {
            case 0x00:
                SetPrim(value);
                break;
            case 0x01:
                SetRgbaq(value);
                break;
            case 0x02:
                SetSt(value);
                break;
            case 0x03:
                SetUv(value);
                break;
            case 0x04:
                ProcessXyz(value, true);
                break;
            case 0x05:
                ProcessXyz(value, false);
                break;
            case 0x0A:
                SetFog(value);
                break;
            case 0x0C:
                ProcessXyz(value, true, true);
                break;
            case 0x0D:
                ProcessXyz(value, false, true);
                break;
            case 0x06:
                SetTex0(value, 0);
                break;
            case 0x07:
                SetTex0(value, 1);
                break;
            case 0x08:
                state.Contexts[0].Clamp = value;
                break;
            case 0x09:
                state.Contexts[1].Clamp = value;
                break;
            case 0x14:
                state.Contexts[0].Tex1 = value;
                break;
            case 0x15:
                state.Contexts[1].Tex1 = value;
                break;
            case 0x18:
                state.Contexts[0].XyOffset = value;
                break;
            case 0x19:
                state.Contexts[1].XyOffset = value;
                break;
            case 0x1A:
                state.PrModeCont = value;
                break;
            case 0x1B:
                SetPrMode(value);
                break;
            case 0x3B:
                state.Texa = value;
                break;
            case 0x3D:
                state.FogColor = value;
                break;
            case 0x3F:
                textureCache.Clear();
                break;
            case 0x40:
                state.Contexts[0].Scissor = value;
                break;
            case 0x41:
                state.Contexts[1].Scissor = value;
                break;
            case 0x42:
                state.Contexts[0].Alpha = value;
                break;
            case 0x43:
                state.Contexts[1].Alpha = value;
                break;
            case 0x44:
                state.Dimx = value;
                break;
            case 0x45:
                state.Dthe = value;
                break;
            case 0x47:
                state.Contexts[0].Test = value;
                break;
            case 0x48:
                state.Contexts[1].Test = value;
                break;
            case 0x4A:
                state.Contexts[0].Fba = value;
                if ((value & 1) != 0)
                    NoteUnsupported("framebuffer_alpha_write");
                break;
            case 0x4B:
                state.Contexts[1].Fba = value;
                if ((value & 1) != 0)
                    NoteUnsupported("framebuffer_alpha_write");
                break;
            case 0x4C:
                state.Contexts[0].Frame = value;
                break;
            case 0x4D:
                state.Contexts[1].Frame = value;
                break;
            case 0x4E:
                state.Contexts[0].Zbuf = value;
                break;
            case 0x4F:
                state.Contexts[1].Zbuf = value;
                break;
            case 0x50:
                state.Bitbltbuf = value;
                break;
            case 0x51:
                state.Trxpos = value;
                break;
            case 0x52:
                state.Trxreg = value;
                break;
            case 0x53:
                state.Trxdir = value;
                break;
        }
    }

    private void AddRegWrite(string name)
    {
        AddCount(gifAudit.RegisterWrites, name);
    }

    private void NoteUnsupported(string name)
    {
        AddCount(renderAudit.UnsupportedStates, name);
    }

    private void NoteApproximation(string name)
    {
        AddCount(renderAudit.Approximations, name);
    }

    private static GsFramebufferTarget DecodeFramebufferTarget(GsContext context)
    {
        return new GsFramebufferTarget(
            (uint)(context.Frame & 0x1FF) << 5,
            (uint)((context.Frame >> 16) & 0x3F),
            (uint)((context.Frame >> 24) & 0x3F),
            (uint)(context.Frame >> 32));
    }

    private static bool IsFramebufferPsmSupported(uint psm)
    {
        return psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2TexPixelDecoder.PSMCT16
            or Ps2GsVram.PSMCT16S
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24;
    }

    private void RecordFramebufferDraw(GsFramebufferTarget target)
    {
        var row = GetFramebufferTargetRow(target);
        row.Draws++;
    }

    private void RecordFramebufferPixels(
        GsFramebufferTarget target,
        ulong tex0,
        long pixelsWritten,
        int minX,
        int minY,
        int maxX,
        int maxY)
    {
        if (pixelsWritten <= 0)
            return;

        var row = GetFramebufferTargetRow(target);
        row.PixelsWritten += pixelsWritten;
        row.WriteBounds = MergeBounds(row.WriteBounds, minX, minY, maxX, maxY, pixelsWritten);
        AddCount(row.Tex0Pixels, $"0x{tex0:X16}", pixelsWritten);
    }

    private GsFramebufferTargetAudit GetFramebufferTargetRow(GsFramebufferTarget target)
    {
        var key = MakeFramebufferKey(target);
        if (!renderAudit.FramebufferTargets.TryGetValue(key, out var row))
        {
            row = new GsFramebufferTargetAudit
            {
                Fbp = target.Fbp,
                Fbw = target.Fbw,
                Psm = target.Psm,
                Fbmsk = target.Fbmsk
            };
            renderAudit.FramebufferTargets[key] = row;
        }

        return row;
    }

    private void RecordMissingTextureDraw(
        ulong tex0,
        GsFramebufferTarget target,
        int minX,
        int minY,
        int maxX,
        int maxY)
    {
        var framebufferKey = MakeFramebufferKey(target);
        var key = $"{tex0:X16}|{framebufferKey}";
        if (!missingTextureDraws.TryGetValue(key, out var row))
        {
            row = new MissingTextureDrawAccumulator(tex0, framebufferKey);
            missingTextureDraws[key] = row;
        }

        row.Draws++;
        row.Bounds = MergeBounds(row.Bounds, minX, minY, maxX, maxY, 1);
    }

    private void RecordAlphaFailure(
        ulong tex0,
        GsFramebufferTarget target,
        GsAlphaTestResult failMode,
        int x,
        int y)
    {
        var framebufferKey = MakeFramebufferKey(target);
        var failModeName = AlphaFailModeName(failMode);
        var key = $"{tex0:X16}|{framebufferKey}|{failModeName}";
        if (!alphaFailureDraws.TryGetValue(key, out var row))
        {
            row = new AlphaFailureAccumulator(tex0, framebufferKey, failModeName);
            alphaFailureDraws[key] = row;
        }

        row.Pixels++;
        row.Bounds = MergeBounds(row.Bounds, x, y, x, y, 1);
    }

    private GsMaterialAccumulator BeginMaterialDraw(
        string primitiveName,
        GsFramebufferTarget target,
        int minX,
        int minY,
        int maxX,
        int maxY,
        bool missingTexture,
        GsVertex a,
        GsVertex b,
        GsVertex c,
        int vertexCount)
    {
        var context = state.Context;
        var key = MakeMaterialKey(primitiveName, context, target);
        if (!materialDraws.TryGetValue(key, out var row))
        {
            row = new GsMaterialAccumulator(
                key,
                primitiveName,
                state,
                context,
                target,
                DecodeScissor(context));
            materialDraws[key] = row;
        }

        row.Draws++;
        if (missingTexture)
            row.MissingTextureDraws++;

        row.Bounds = MergeBounds(row.Bounds, minX, minY, maxX, maxY, 0);
        row.AddVertex(a);
        if (vertexCount > 1)
            row.AddVertex(b);
        if (vertexCount > 2)
            row.AddVertex(c);
        return row;
    }

    private string MakeMaterialKey(string primitiveName, GsContext context, GsFramebufferTarget target)
    {
        return string.Join(
            '|',
            primitiveName,
            $"PRIM=0x{state.Prim:X16}",
            $"CTXT={state.ContextIndex}",
            $"TEX0=0x{context.Tex0:X16}",
            $"TEX1=0x{context.Tex1:X16}",
            $"CLAMP=0x{context.Clamp:X16}",
            $"ALPHA=0x{context.Alpha:X16}",
            $"TEST=0x{context.Test:X16}",
            $"TEXA=0x{state.Texa:X16}",
            $"FOGCOL=0x{state.FogColor:X16}",
            $"FRAME={MakeFramebufferKey(target)}",
            $"ZBUF=0x{context.Zbuf:X16}",
            $"SCISSOR=0x{context.Scissor:X16}",
            $"DTHE=0x{state.Dthe:X16}",
            $"FBA=0x{context.Fba:X16}");
    }

    private void RecordImageTransfer(GsImageTransferDescriptor descriptor)
    {
        var key = MakeImageTransferKey(descriptor);
        if (!renderAudit.ImageTransferTargets.TryGetValue(key, out var row))
        {
            row = new GsImageTransferTargetAudit
            {
                Dbp = descriptor.Dbp,
                Dbw = descriptor.Dbw,
                Dpsm = descriptor.Dpsm,
                Width = descriptor.Width,
                Height = descriptor.Height,
                Dsax = descriptor.Dsax,
                Dsay = descriptor.Dsay
            };
            renderAudit.ImageTransferTargets[key] = row;
        }

        row.Transfers++;
        row.Bytes += descriptor.ExpectedBytes;
    }

    private List<GsFramebufferSnapshot> BuildFramebufferSnapshots()
    {
        var targets = new List<GsFramebufferTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddTarget(GsFramebufferTarget target)
        {
            if (!IsFramebufferPsmSupported(target.Psm))
                return;

            if (target.Fbw == 0)
                return;

            if (seen.Add(MakeFramebufferKey(target)))
                targets.Add(target);
        }

        if (renderAudit.PresentedFramebuffer)
        {
            AddTarget(new GsFramebufferTarget(
                renderAudit.PresentedFramebufferFbp,
                renderAudit.PresentedFramebufferFbw,
                renderAudit.PresentedFramebufferPsm,
                0));
        }

        foreach (var row in renderAudit.FramebufferTargets.Values.Take(8))
            AddTarget(new GsFramebufferTarget(row.Fbp, row.Fbw, row.Psm, row.Fbmsk));

        var snapshots = new List<GsFramebufferSnapshot>(targets.Count);
        foreach (var target in targets)
        {
            var width = Math.Clamp((int)target.Fbw * 64, 1, options.Width);
            var height = options.Height;
            if (renderAudit.FramebufferTargets.TryGetValue(MakeFramebufferKey(target), out var auditRow)
                && auditRow.WriteBounds != null)
            {
                height = Math.Clamp(auditRow.WriteBounds.Y + auditRow.WriteBounds.Height, 1, options.Height);
            }

            if (target.Fbp == renderAudit.PresentedFramebufferFbp
                && target.Fbw == renderAudit.PresentedFramebufferFbw
                && target.Psm == renderAudit.PresentedFramebufferPsm)
            {
                width = Math.Clamp(renderAudit.PresentedFramebufferWidth, 1, options.Width);
                height = Math.Clamp(renderAudit.PresentedFramebufferHeight, 1, options.Height);
            }

            var rgba = ReadFramebufferRgba(target.Fbp, target.Fbw, target.Psm, width, height);
            if (rgba == null)
                continue;

            snapshots.Add(new GsFramebufferSnapshot(
                MakeFramebufferKey(target),
                target.Fbp,
                target.Fbw,
                target.Psm,
                target.Fbmsk,
                width,
                height,
                CountNonBlackPixels(rgba),
                rgba));
        }

        return snapshots;
    }

    private static string MakeFramebufferKey(GsFramebufferTarget target)
    {
        return MakeFramebufferKey(target.Fbp, target.Fbw, target.Psm, target.Fbmsk);
    }

    private static string MakeFramebufferKey(uint fbp, uint fbw, uint psm, uint fbmsk)
    {
        return $"FBP={fbp},FBW={fbw},PSM=0x{psm:X2},FBMSK=0x{fbmsk:X8}";
    }

    private static string MakeImageTransferKey(GsImageTransferDescriptor descriptor)
    {
        return
            $"DBP={descriptor.Dbp},DBW={descriptor.Dbw},PSM=0x{descriptor.Dpsm:X2},RECT={descriptor.Width}x{descriptor.Height}@{descriptor.Dsax},{descriptor.Dsay}";
    }

    private static GsPixelBounds MergeBounds(
        GsPixelBounds? current,
        int minX,
        int minY,
        int maxX,
        int maxY,
        long pixels)
    {
        if (current == null)
        {
            return new GsPixelBounds
            {
                X = minX,
                Y = minY,
                Width = maxX - minX + 1,
                Height = maxY - minY + 1,
                NonBlackPixels = pixels
            };
        }

        var x0 = Math.Min(current.X, minX);
        var y0 = Math.Min(current.Y, minY);
        var x1 = Math.Max(current.X + current.Width - 1, maxX);
        var y1 = Math.Max(current.Y + current.Height - 1, maxY);
        return new GsPixelBounds
        {
            X = x0,
            Y = y0,
            Width = x1 - x0 + 1,
            Height = y1 - y0 + 1,
            NonBlackPixels = current.NonBlackPixels + pixels
        };
    }

    private static void AddCount<TKey>(Dictionary<TKey, long> map, TKey key) where TKey : notnull
    {
        map.TryGetValue(key, out var count);
        map[key] = count + 1;
    }

    private static void AddCount<TKey>(Dictionary<TKey, long> map, TKey key, long increment) where TKey : notnull
    {
        map.TryGetValue(key, out var count);
        map[key] = count + increment;
    }

    private static GsTex0AuditRow MakeTex0Row(ulong tex0, long xyzWrites)
    {
        var tw = (int)((tex0 >> 26) & 0xF);
        var th = (int)((tex0 >> 30) & 0xF);
        return new GsTex0AuditRow
        {
            Tex0 = $"0x{tex0:X16}",
            Tbp = (uint)(tex0 & 0x3FFF),
            Tbw = (uint)((tex0 >> 14) & 0x3F),
            Psm = (uint)((tex0 >> 20) & 0x3F),
            Width = 1 << tw,
            Height = 1 << th,
            Cbp = (uint)((tex0 >> 37) & 0x3FFF),
            Cpsm = (uint)((tex0 >> 51) & 0xF),
            XyzWrites = xyzWrites
        };
    }

    private readonly record struct CircuitLayer(byte[]? Rgba, GsPresentedCircuitAudit Audit);

    private sealed class GsMaterialAccumulator
    {
        private readonly ulong alpha;
        private readonly bool alphaBlendEnabled;
        private readonly ulong clamp;
        private readonly int contextIndex;
        private readonly ulong dthe;
        private readonly ulong fba;
        private readonly bool fixedTextureCoordinates;
        private readonly ulong fogColor;
        private readonly bool fogEnabled;
        private readonly GsFramebufferTarget framebuffer;
        private readonly string framebufferKey;
        private readonly ulong prim;
        private readonly (int X0, int Y0, int X1, int Y1) scissor;
        private readonly ulong scissorRaw;
        private readonly ulong test;
        private readonly ulong tex0;
        private readonly ulong tex1;
        private readonly ulong texa;
        private readonly bool textureEnabled;
        private readonly ulong zbuf;
        private double maxA = double.NegativeInfinity;
        private double maxB = double.NegativeInfinity;
        private double maxG = double.NegativeInfinity;
        private double maxQ = double.NegativeInfinity;
        private double maxR = double.NegativeInfinity;
        private double maxU = double.NegativeInfinity;
        private double maxV = double.NegativeInfinity;
        private double minA = double.PositiveInfinity;
        private double minB = double.PositiveInfinity;
        private double minG = double.PositiveInfinity;
        private double minQ = double.PositiveInfinity;
        private double minR = double.PositiveInfinity;
        private double minU = double.PositiveInfinity;
        private double minV = double.PositiveInfinity;
        private double sumA;
        private double sumB;
        private double sumG;
        private double sumR;
        private long vertexCount;

        public GsMaterialAccumulator(
            string key,
            string primitive,
            GsState state,
            GsContext context,
            GsFramebufferTarget framebuffer,
            (int X0, int Y0, int X1, int Y1) scissor)
        {
            Key = key;
            Primitive = primitive;
            prim = state.Prim;
            contextIndex = state.ContextIndex;
            textureEnabled = state.Tme;
            fogEnabled = state.Fge;
            alphaBlendEnabled = state.Abe;
            fixedTextureCoordinates = state.Fst;
            tex0 = context.Tex0;
            tex1 = context.Tex1;
            clamp = context.Clamp;
            alpha = context.Alpha;
            test = context.Test;
            texa = state.Texa;
            fogColor = state.FogColor;
            zbuf = context.Zbuf;
            scissorRaw = context.Scissor;
            dthe = state.Dthe;
            fba = context.Fba;
            this.framebuffer = framebuffer;
            framebufferKey = MakeFramebufferKey(framebuffer);
            this.scissor = scissor;
        }

        public string Key { get; }
        public string Primitive { get; }
        public long Draws { get; set; }
        public long MissingTextureDraws { get; set; }
        public long PixelsWritten { get; set; }
        public GsPixelBounds? Bounds { get; set; }

        private bool HasVertices => vertexCount > 0;

        public void AddVertex(GsVertex vertex)
        {
            vertexCount++;
            minR = Math.Min(minR, vertex.R);
            maxR = Math.Max(maxR, vertex.R);
            sumR += vertex.R;
            minG = Math.Min(minG, vertex.G);
            maxG = Math.Max(maxG, vertex.G);
            sumG += vertex.G;
            minB = Math.Min(minB, vertex.B);
            maxB = Math.Max(maxB, vertex.B);
            sumB += vertex.B;
            minA = Math.Min(minA, vertex.A);
            maxA = Math.Max(maxA, vertex.A);
            sumA += vertex.A;
            minU = Math.Min(minU, vertex.U);
            maxU = Math.Max(maxU, vertex.U);
            minV = Math.Min(minV, vertex.V);
            maxV = Math.Max(maxV, vertex.V);
            minQ = Math.Min(minQ, vertex.Q);
            maxQ = Math.Max(maxQ, vertex.Q);
        }

        public GsMaterialAuditRow ToAuditRow()
        {
            var tw = (int)((tex0 >> 26) & 0xF);
            var th = (int)((tex0 >> 30) & 0xF);
            var count = Math.Max(1, vertexCount);
            return new GsMaterialAuditRow
            {
                Key = Key,
                Primitive = Primitive,
                Prim = $"0x{prim:X16}",
                ContextIndex = contextIndex,
                TextureEnabled = textureEnabled,
                FogEnabled = fogEnabled,
                AlphaBlendEnabled = alphaBlendEnabled,
                FixedTextureCoordinates = fixedTextureCoordinates,
                Draws = Draws,
                MissingTextureDraws = MissingTextureDraws,
                PixelsWritten = PixelsWritten,
                Bounds = Bounds,
                MinR = HasVertices ? minR : 0,
                MaxR = HasVertices ? maxR : 0,
                AvgR = sumR / count,
                MinG = HasVertices ? minG : 0,
                MaxG = HasVertices ? maxG : 0,
                AvgG = sumG / count,
                MinB = HasVertices ? minB : 0,
                MaxB = HasVertices ? maxB : 0,
                AvgB = sumB / count,
                MinA = HasVertices ? minA : 0,
                MaxA = HasVertices ? maxA : 0,
                AvgA = sumA / count,
                MinU = HasVertices ? minU : 0,
                MaxU = HasVertices ? maxU : 0,
                MinV = HasVertices ? minV : 0,
                MaxV = HasVertices ? maxV : 0,
                MinQ = HasVertices ? minQ : 0,
                MaxQ = HasVertices ? maxQ : 0,
                Tex0 = $"0x{tex0:X16}",
                TextureTbp = (uint)(tex0 & 0x3FFF),
                TextureTbw = (uint)((tex0 >> 14) & 0x3F),
                TexturePsm = (uint)((tex0 >> 20) & 0x3F),
                TextureWidth = 1 << tw,
                TextureHeight = 1 << th,
                TextureTcc = (uint)((tex0 >> 34) & 0x1),
                TextureTfx = (uint)((tex0 >> 35) & 0x3),
                TextureCbp = (uint)((tex0 >> 37) & 0x3FFF),
                TextureCpsm = (uint)((tex0 >> 51) & 0xF),
                TextureCsm = (uint)((tex0 >> 55) & 0x1),
                TextureCsa = (uint)((tex0 >> 56) & 0x1F),
                TextureCld = (uint)((tex0 >> 61) & 0x7),
                Tex1 = $"0x{tex1:X16}",
                Tex1Lcm = (uint)(tex1 & 0x1),
                Tex1Mxl = (uint)((tex1 >> 2) & 0x7),
                Tex1Mmag = (uint)((tex1 >> 5) & 0x1),
                Tex1Mmin = (uint)((tex1 >> 6) & 0x7),
                Clamp = $"0x{clamp:X16}",
                ClampWms = (uint)(clamp & 0x3),
                ClampWmt = (uint)((clamp >> 2) & 0x3),
                ClampMinUOrMask = (uint)((clamp >> 4) & 0x3FF),
                ClampMaxUOrFix = (uint)((clamp >> 14) & 0x3FF),
                ClampMinVOrMask = (uint)((clamp >> 24) & 0x3FF),
                ClampMaxVOrFix = (uint)((clamp >> 34) & 0x3FF),
                Alpha = $"0x{alpha:X16}",
                AlphaA = (uint)(alpha & 0x3),
                AlphaB = (uint)((alpha >> 2) & 0x3),
                AlphaC = (uint)((alpha >> 4) & 0x3),
                AlphaD = (uint)((alpha >> 6) & 0x3),
                AlphaFix = (uint)((alpha >> 32) & 0xFF),
                Test = $"0x{test:X16}",
                AlphaTestEnabled = (test & 1) != 0,
                AlphaTestMethod = (uint)((test >> 1) & 0x7),
                AlphaRef = (uint)((test >> 4) & 0xFF),
                AlphaFailMode = (uint)((test >> 12) & 0x3),
                DestinationAlphaTestEnabled = ((test >> 14) & 1) != 0,
                DestinationAlphaTestMode = (uint)((test >> 15) & 0x1),
                DepthTestEnabled = ((test >> 16) & 1) != 0,
                DepthTestMethod = (uint)((test >> 17) & 0x3),
                Texa = $"0x{texa:X16}",
                TexaTa0 = (uint)(texa & 0xFF),
                TexaAem = ((texa >> 15) & 1) != 0,
                TexaTa1 = (uint)((texa >> 32) & 0xFF),
                FogColor = $"0x{fogColor:X16}",
                FramebufferKey = framebufferKey,
                FramebufferFbp = framebuffer.Fbp,
                FramebufferFbw = framebuffer.Fbw,
                FramebufferPsm = framebuffer.Psm,
                FramebufferMask = framebuffer.Fbmsk,
                Zbuf = $"0x{zbuf:X16}",
                Zbp = (uint)(zbuf & 0x1FF) << 5,
                Zpsm = (uint)((zbuf >> 24) & 0xF),
                Zmask = ((zbuf >> 32) & 1) != 0,
                Scissor = $"0x{scissorRaw:X16}",
                ScissorX0 = scissor.X0,
                ScissorY0 = scissor.Y0,
                ScissorX1 = scissor.X1,
                ScissorY1 = scissor.Y1,
                DitherEnabled = (dthe & 1) != 0,
                FramebufferAlphaWriteEnabled = (fba & 1) != 0
            };
        }
    }

    private sealed class MissingTextureDrawAccumulator
    {
        public MissingTextureDrawAccumulator(ulong tex0, string framebufferKey)
        {
            Tex0 = tex0;
            FramebufferKey = framebufferKey;
        }

        public ulong Tex0 { get; }
        public string FramebufferKey { get; }
        public long Draws { get; set; }
        public GsPixelBounds? Bounds { get; set; }

        public GsMissingTextureDrawAuditRow ToAuditRow()
        {
            var tw = (int)((Tex0 >> 26) & 0xF);
            var th = (int)((Tex0 >> 30) & 0xF);
            return new GsMissingTextureDrawAuditRow
            {
                Tex0 = $"0x{Tex0:X16}",
                Tbp = (uint)(Tex0 & 0x3FFF),
                Tbw = (uint)((Tex0 >> 14) & 0x3F),
                Psm = (uint)((Tex0 >> 20) & 0x3F),
                Width = 1 << tw,
                Height = 1 << th,
                Cbp = (uint)((Tex0 >> 37) & 0x3FFF),
                Cpsm = (uint)((Tex0 >> 51) & 0xF),
                FramebufferKey = FramebufferKey,
                Draws = Draws,
                Bounds = Bounds
            };
        }
    }

    private sealed class AlphaFailureAccumulator
    {
        public AlphaFailureAccumulator(ulong tex0, string framebufferKey, string failMode)
        {
            Tex0 = tex0;
            FramebufferKey = framebufferKey;
            FailMode = failMode;
        }

        public ulong Tex0 { get; }
        public string FramebufferKey { get; }
        public string FailMode { get; }
        public long Pixels { get; set; }
        public GsPixelBounds? Bounds { get; set; }

        public GsAlphaFailureAuditRow ToAuditRow()
        {
            var tw = (int)((Tex0 >> 26) & 0xF);
            var th = (int)((Tex0 >> 30) & 0xF);
            return new GsAlphaFailureAuditRow
            {
                Tex0 = $"0x{Tex0:X16}",
                Tbp = (uint)(Tex0 & 0x3FFF),
                Tbw = (uint)((Tex0 >> 14) & 0x3F),
                Psm = (uint)((Tex0 >> 20) & 0x3F),
                Width = 1 << tw,
                Height = 1 << th,
                Cbp = (uint)((Tex0 >> 37) & 0x3FFF),
                Cpsm = (uint)((Tex0 >> 51) & 0xF),
                FramebufferKey = FramebufferKey,
                FailMode = FailMode,
                Pixels = Pixels,
                Bounds = Bounds
            };
        }
    }

    private sealed class GsState
    {
        public ulong Prim { get; set; } = 3;
        public ulong PrModeCont { get; set; } = 1;
        public ulong Texa { get; set; }
        public ulong FogColor { get; set; }
        public ulong Dthe { get; set; }
        public ulong Dimx { get; set; }
        public ulong Bitbltbuf { get; set; }
        public ulong Trxpos { get; set; }
        public ulong Trxreg { get; set; }
        public ulong Trxdir { get; set; }
        public GsContext[] Contexts { get; } = [new(), new()];
        public byte ColorR { get; set; } = 128;
        public byte ColorG { get; set; } = 128;
        public byte ColorB { get; set; } = 128;
        public byte ColorA { get; set; } = 128;
        public float S { get; set; }
        public float T { get; set; }
        public float Q { get; set; } = 1f;
        public int U { get; set; }
        public int V { get; set; }
        public byte Fog { get; set; } = 255;
        public List<GsVertex> PrimitiveVertices { get; } = [];
        public int PrimType => (int)(Prim & 0x7);
        public bool Tme => ((Prim >> 4) & 1) != 0;
        public bool Fge => ((Prim >> 5) & 1) != 0;
        public bool Abe => ((Prim >> 6) & 1) != 0;
        public bool Fst => ((Prim >> 8) & 1) != 0;
        public int ContextIndex => (int)((Prim >> 9) & 1);
        public GsContext Context => Contexts[ContextIndex];
    }

    private sealed class GsContext
    {
        public ulong Tex0 { get; set; }
        public ulong Tex1 { get; set; }
        public ulong Clamp { get; set; }
        public ulong Alpha { get; set; } = 0x44;
        public ulong Test { get; set; } = 0x00050000;
        public ulong Frame { get; set; }
        public ulong Zbuf { get; set; }
        public ulong XyOffset { get; set; }
        public ulong Scissor { get; set; }
        public ulong Fba { get; set; }
    }

    private readonly record struct GsVertex(
        float X,
        float Y,
        float Z,
        byte R,
        byte G,
        byte B,
        byte A,
        float U,
        float V,
        float Q,
        byte Fog,
        bool NoKick);

    private sealed record GsTexture(int Width, int Height, byte[] Rgba);

    private readonly record struct GsTextureCacheKey(ulong Tex0, ulong Texa);

    private readonly record struct GsSample(float R, float G, float B, float A);

    private enum GsAlphaTestResult
    {
        Pass,
        FailKeep,
        FailFramebufferOnly,
        FailZBufferOnly,
        FailRgbOnly
    }

    private readonly record struct GsFramebufferTarget(uint Fbp, uint Fbw, uint Psm, uint Fbmsk);

    private readonly record struct GsImageTransferDescriptor(
        uint Dbp,
        uint Dbw,
        uint Dpsm,
        int Width,
        int Height,
        int Dsax,
        int Dsay,
        int ExpectedBytes);

    private sealed class GsImageTransfer(GsImageTransferDescriptor descriptor)
    {
        public GsImageTransferDescriptor Descriptor { get; } = descriptor;
        public byte[] Buffer { get; } = new byte[descriptor.ExpectedBytes];
        public int BytesWritten { get; set; }
        public int ExpectedBytes => Descriptor.ExpectedBytes;
    }
}
