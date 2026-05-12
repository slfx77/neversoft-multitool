using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed record GsResolvedTexture(int Width, int Height, byte[] Rgba, uint? Checksum = null);

internal sealed class GsGifInterpretOptions
{
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 448;
    public float CoordinateScaleX { get; init; } = 1f;
    public float CoordinateScaleY { get; init; } = 1f;
    public Func<ulong, GsResolvedTexture?>? TextureResolver { get; init; }
    public Func<GsRuntimeTextureDump, string?>? TextureDumpSink { get; init; }
    public int? ProbeX { get; init; }
    public int? ProbeY { get; init; }
    public uint? ProbeFbp { get; init; }
    public Action<GsPixelProbeInfo>? PixelProbe { get; init; }
    public int? MaxVsync { get; init; }
    public int SaveRtStart { get; init; }
    public int? SaveRtCount { get; init; }
    public uint? SaveRtFbp { get; init; }
    public Action<GsDrawRtSnapshot>? SaveRtSink { get; init; }
}

internal sealed record GsDrawRtSnapshot(
    long DrawIndex,
    uint Fbp,
    uint Fbw,
    uint Psm,
    uint Fbmsk,
    int Width,
    int Height,
    byte[] Rgba);

internal sealed record GsRuntimeTextureDump(GsTextureDumpAuditRow Audit, byte[] Rgba);

internal sealed record GsPixelProbeInfo(
    int X,
    int Y,
    string Primitive,
    long DrawIndex,
    uint Fbp,
    uint Fbw,
    uint Psm,
    uint Fbmsk,
    ulong Tex0,
    ulong AlphaRegister,
    ulong TestRegister,
    bool TextureEnabled,
    bool BlendEnabled,
    bool TextureSampled,
    float SampleR,
    float SampleG,
    float SampleB,
    float SampleA,
    float VertexR,
    float VertexG,
    float VertexB,
    float VertexA,
    float SrcR,
    float SrcG,
    float SrcB,
    float SrcA,
    float Z,
    bool HasPreBlendDst,
    byte PreBlendDstR,
    byte PreBlendDstG,
    byte PreBlendDstB,
    byte PreBlendDstA,
    byte WrittenR,
    byte WrittenG,
    byte WrittenB,
    byte WrittenA);

internal sealed class GsGifInterpretation
{
    public required GsGifAudit Gif { get; init; }
    public required GsRenderAudit Render { get; init; }
    public required byte[] DirectPixels { get; init; }
    public required byte[] Pixels { get; init; }
    public required List<GsFramebufferSnapshot> FramebufferSnapshots { get; init; }
    public required Dictionary<ulong, long> XyzByTex0 { get; init; }
}

internal sealed record GsFramebufferSnapshot(
    string Key,
    uint Fbp,
    uint Fbw,
    uint Psm,
    uint Fbmsk,
    int Width,
    int Height,
    long NonBlackPixels,
    byte[] Rgba);

internal sealed class GsGifInterpreter
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

    private readonly GsGifInterpretOptions options;
    private readonly GsState state = new();
    private readonly GsGifAudit gifAudit = new();
    private readonly GsRenderAudit renderAudit = new();
    private readonly Dictionary<ulong, long> xyzByTex0 = [];
    private readonly Dictionary<ulong, long> tex0Writes = [];
    private readonly Dictionary<GsTextureCacheKey, GsTexture?> textureCache = [];
    private readonly Dictionary<string, MissingTextureDrawAccumulator> missingTextureDraws = [];
    private readonly Dictionary<string, AlphaFailureAccumulator> alphaFailureDraws = [];
    private readonly Dictionary<string, GsMaterialAccumulator> materialDraws = [];
    private readonly HashSet<string> dumpedTextureKeys = [];
    private readonly Dictionary<ulong, float[]> depthBuffers = [];
    private readonly byte[] pixels;
    private readonly Ps2GsVram vram = new();
    private GsImageTransfer? activeImageTransfer;
    private byte[]? directPixels;

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
        static ulong U64(ReadOnlySpan<byte> source, int offset) =>
            offset >= 0 && offset + 8 <= source.Length
                ? BinaryPrimitives.ReadUInt64LittleEndian(source[offset..])
                : 0;

        static uint U32(ReadOnlySpan<byte> source, int offset) =>
            offset >= 0 && offset + 4 <= source.Length
                ? BinaryPrimitives.ReadUInt32LittleEndian(source[offset..])
                : 0;

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
                    var bodyBytes = ((itemCount * 8) + 15) & ~15;
                    if (offset + bodyBytes > data.Length)
                    {
                        NoteUnsupported("truncated_reglist_gif_body");
                        return;
                    }

                    for (var i = 0; i < itemCount; i++)
                    {
                        var reg = (int)((tagHi >> ((i % nreg) * 4)) & 0xF);
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
                    ProcessPackedXyz(qword, xyzf: reg == 0x0C, forceNoKick: true);
                else
                    ProcessXyz(value, xyzf: reg == 0x0C, forceNoKick: true);
                break;
            case 0x06:
                SetTex0(value, contextIndex: 0);
                break;
            case 0x07:
                SetTex0(value, contextIndex: 1);
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
                ProcessXyz(value, xyzf: true, forceNoKick: true);
                break;
            case 0x0D:
                ProcessXyz(value, xyzf: false, forceNoKick: true);
                break;
            case 0x06:
                SetTex0(value, contextIndex: 0);
                break;
            case 0x07:
                SetTex0(value, contextIndex: 1);
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

    private void SetPrim(ulong prim)
    {
        if ((state.PrModeCont & 1) != 0)
        {
            state.Prim = prim & 0x7FF;
        }
        else
        {
            state.Prim = (state.Prim & ~0x7UL) | (prim & 0x7);
        }

        // PS2 GS spec: writing PRIM always restarts the vertex queue, regardless
        // of whether the primitive type changes. Without this, sequential TRIANGLE_STRIP
        // primitives accumulate in the same buffer and draw bridging triangles between
        // them — visible as scrambled HUD-font glyphs and similar streak artifacts.
        state.PrimitiveVertices.Clear();
    }

    private void SetPrMode(ulong prMode)
    {
        if ((state.PrModeCont & 1) != 0)
            return;

        var primType = state.Prim & 0x7;
        state.Prim = (prMode & 0x7F8) | primType;
        state.PrimitiveVertices.Clear();
    }

    private void SetPackedRgba(ReadOnlySpan<byte> qword)
    {
        state.ColorR = qword[0];
        state.ColorG = qword[4];
        state.ColorB = qword[8];
        state.ColorA = qword[12];
    }

    private void SetRgbaq(ReadOnlySpan<byte> qword)
    {
        state.ColorR = qword[0];
        state.ColorG = qword[1];
        state.ColorB = qword[2];
        state.ColorA = qword[3];
        state.Q = BitConverter.ToSingle(qword[4..8]);
    }

    private void SetRgbaq(ulong value)
    {
        state.ColorR = (byte)value;
        state.ColorG = (byte)(value >> 8);
        state.ColorB = (byte)(value >> 16);
        state.ColorA = (byte)(value >> 24);
        var qBits = (uint)(value >> 32);
        if (qBits != 0)
            state.Q = BitConverter.UInt32BitsToSingle(qBits);
    }

    private void SetSt(ReadOnlySpan<byte> qword)
    {
        state.S = BitConverter.ToSingle(qword[0..4]);
        state.T = BitConverter.ToSingle(qword[4..8]);
        state.Q = BitConverter.ToSingle(qword[8..12]);
    }

    private void SetSt(ulong value)
    {
        state.S = BitConverter.UInt32BitsToSingle((uint)value);
        state.T = BitConverter.UInt32BitsToSingle((uint)(value >> 32));
    }

    private void SetUv(ulong value)
    {
        state.U = (int)(value & 0x3FFF);
        state.V = (int)((value >> 16) & 0x3FFF);
    }

    private void SetPackedUv(ReadOnlySpan<byte> qword)
    {
        state.U = (int)(BinaryPrimitives.ReadUInt32LittleEndian(qword) & 0x3FFF);
        state.V = (int)(BinaryPrimitives.ReadUInt32LittleEndian(qword[4..]) & 0x3FFF);
    }

    private void SetFog(ulong value)
    {
        state.Fog = (byte)(value >> 56);
    }

    private void SetTex0(ulong value, int contextIndex)
    {
        AddCount(tex0Writes, value);
        state.Contexts[Math.Clamp(contextIndex, 0, 1)].Tex0 = value;
    }

    private void ProcessPackedXyz(ReadOnlySpan<byte> qword, bool xyzf, bool forceNoKick = false)
    {
        var rawX = BinaryPrimitives.ReadUInt16LittleEndian(qword);
        var rawY = BinaryPrimitives.ReadUInt16LittleEndian(qword[4..]);
        var z = BinaryPrimitives.ReadUInt32LittleEndian(qword[8..]);
        if (xyzf)
        {
            z &= 0x00FFFFFF;
            state.Fog = (byte)((BinaryPrimitives.ReadUInt32LittleEndian(qword[12..]) >> 4) & 0xFF);
        }
        var noKick = forceNoKick || (BinaryPrimitives.ReadUInt32LittleEndian(qword[12..]) & 0x8000) != 0;
        ProcessXyz(rawX, rawY, z, noKick);
    }

    private void ProcessXyz(ulong value, bool xyzf, bool forceNoKick = false)
    {
        var rawX = (int)(value & 0xFFFF);
        var rawY = (int)((value >> 16) & 0xFFFF);
        var z = xyzf ? (uint)((value >> 32) & 0x00FFFFFF) : (uint)(value >> 32);
        if (xyzf)
            state.Fog = (byte)(value >> 56);
        ProcessXyz(rawX, rawY, z, noKick: forceNoKick);
    }

    private void ProcessXyz(int rawX, int rawY, uint z, bool noKick)
    {
        gifAudit.XyzWriteCount++;
        var primName = PrimitiveNames[Math.Clamp(state.PrimType, 0, 7)];
        AddCount(gifAudit.XyzWritesByPrimitive, primName);
        AddCount(xyzByTex0, state.Context.Tex0);

        var (screenX, screenY) = Project(rawX, rawY);
        var vertex = new GsVertex(
            screenX,
            screenY,
            z,
            state.ColorR,
            state.ColorG,
            state.ColorB,
            state.ColorA,
            CurrentU(),
            CurrentV(),
            CurrentQ(),
            state.Fog,
            noKick);

        EmitVertex(vertex);
    }

    private (float X, float Y) Project(int rawX, int rawY)
    {
        var ofx = (int)(state.Context.XyOffset & 0xFFFF);
        var ofy = (int)((state.Context.XyOffset >> 32) & 0xFFFF);
        return (((rawX - ofx) / 16f) * options.CoordinateScaleX, ((rawY - ofy) / 16f) * options.CoordinateScaleY);
    }

    private float CurrentU()
    {
        if (state.Fst)
        {
            var width = Math.Max(1, 1 << (int)((state.Context.Tex0 >> 26) & 0xF));
            return (state.U / 16f) / width;
        }

        return state.S;
    }

    private float CurrentV()
    {
        if (state.Fst)
        {
            var height = Math.Max(1, 1 << (int)((state.Context.Tex0 >> 30) & 0xF));
            return (state.V / 16f) / height;
        }

        return state.T;
    }

    private float CurrentQ() => state.Fst ? 1f : state.Q;

    private static bool IsDegenerateXy(GsVertex a, GsVertex b, GsVertex c)
    {
        // Compare in fixed-point screen-space (PCSX2 uses integer XY pre-offset for this cull).
        // Using float equality at this scale is fine because Project() produces deterministic
        // values from the same integer source.
        return (a.X == b.X && a.Y == b.Y)
            || (a.X == c.X && a.Y == c.Y)
            || (b.X == c.X && b.Y == c.Y);
    }

    private void EmitVertex(GsVertex vertex)
    {
        switch (state.PrimType)
        {
            case 0:
                if (!vertex.NoKick)
                    DrawPoint(vertex);
                break;
            case 3:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count == 3)
                {
                    if (!state.PrimitiveVertices.Any(static v => v.NoKick))
                        DrawTriangle(state.PrimitiveVertices[0], state.PrimitiveVertices[1], state.PrimitiveVertices[2]);
                    state.PrimitiveVertices.Clear();
                }

                break;
            case 4:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count >= 3 && !vertex.NoKick)
                {
                    var n = state.PrimitiveVertices.Count;
                    var v0 = state.PrimitiveVertices[n - 3];
                    var v1 = state.PrimitiveVertices[n - 2];
                    var v2 = state.PrimitiveVertices[n - 1];
                    // Cull degenerate triangles (two vertices share the same XY). HUD font strips use
                    // this to restart between glyphs: a XYZ3 vertex with the same XY as a neighbour
                    // collapses the bridging triangle to zero area, which the PS2 GS culls. Without
                    // this cull, the bridge interpolates UV across atlas regions and scrambles text
                    // (e.g. SPECIAL / score meters).
                    if (!IsDegenerateXy(v0, v1, v2))
                    {
                        if ((n & 1) == 1)
                            DrawTriangle(v0, v1, v2);
                        else
                            DrawTriangle(v1, v0, v2);
                    }
                }

                break;
            case 5:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count >= 3 && !vertex.NoKick)
                {
                    var n = state.PrimitiveVertices.Count;
                    var v0 = state.PrimitiveVertices[0];
                    var v1 = state.PrimitiveVertices[n - 2];
                    var v2 = state.PrimitiveVertices[n - 1];
                    if (!IsDegenerateXy(v0, v1, v2))
                        DrawTriangle(v0, v1, v2);
                }

                break;
            case 6:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count == 2)
                {
                    if (!state.PrimitiveVertices.Any(static v => v.NoKick))
                        DrawSprite(state.PrimitiveVertices[0], state.PrimitiveVertices[1]);
                    state.PrimitiveVertices.Clear();
                }

                break;
            default:
                NoteUnsupported($"primitive_{PrimitiveNames[Math.Clamp(state.PrimType, 0, 7)].ToLowerInvariant()}");
                break;
        }
    }

    private void DrawPoint(GsVertex vertex)
    {
        renderAudit.DrawsSeen++;
        renderAudit.PointsDrawn++;

        var context = state.Context;
        var framebufferTarget = DecodeFramebufferTarget(context);
        var scissor = DecodeScissor(context);
        var x = (int)MathF.Floor(vertex.X);
        var y = (int)MathF.Floor(vertex.Y);
        if (x < scissor.X0 || x > scissor.X1 || y < scissor.Y0 || y > scissor.Y1 ||
            x < 0 || x >= options.Width || y < 0 || y >= options.Height)
        {
            return;
        }

        var texture = state.Tme ? ResolveTexture(context.Tex0) : null;
        if (state.Tme && texture == null)
        {
            renderAudit.TextureDecodeMisses++;
            RecordMissingTextureDraw(context.Tex0, framebufferTarget, x, y, x, y);
            BeginMaterialDraw("POINT", framebufferTarget, x, y, x, y, missingTexture: true, vertex, default, default, vertexCount: 1);
            NoteUnsupported("textured_draw_skipped_missing_texture");
            return;
        }

        if (state.Abe)
            NoteApproximation("gs_alpha_blend_approximated");
        if (!IsFramebufferPsmSupported(framebufferTarget.Psm))
            NoteUnsupported($"framebuffer_psm_0x{framebufferTarget.Psm:X2}");

        var material = BeginMaterialDraw("POINT", framebufferTarget, x, y, x, y, missingTexture: false, vertex, default, default, vertexCount: 1);
        RecordFramebufferDraw(framebufferTarget);
        var depthBuffer = GetDepthBuffer(context);
        var idx = y * options.Width + x;
        if (!PassesDepth(vertex.Z, idx, context, depthBuffer))
        {
            renderAudit.DepthRejectedPixels++;
            return;
        }

        var sample = Sample(
            context,
            texture,
            PointTextureCoordinate(vertex.U, vertex.Q),
            PointTextureCoordinate(vertex.V, vertex.Q));
        if (state.Tme)
        {
            if (state.Fst)
                renderAudit.FixedTexturePixels++;
            else
                renderAudit.PerspectiveTexturePixels++;
        }

        CombineTextureFunction(context, sample, vertex.R, vertex.G, vertex.B, vertex.A,
            out var srcR, out var srcG, out var srcB, out var srcA);
        if (state.Fge)
            ApplyFog(vertex.Fog, ref srcR, ref srcG, ref srcB);

        var alphaTest = EvaluateAlphaTest(srcA, context);
        if (alphaTest != GsAlphaTestResult.Pass)
        {
            renderAudit.AlphaFailedPixels++;
            RecordAlphaFailure(context.Tex0, framebufferTarget, alphaTest, x, y);
            switch (alphaTest)
            {
                case GsAlphaTestResult.FailFramebufferOnly:
                    renderAudit.AlphaFailFramebufferOnlyPixels++;
                    break;
                case GsAlphaTestResult.FailZBufferOnly:
                    renderAudit.AlphaFailZBufferOnlyPixels++;
                    if (!ZWriteMasked(context))
                    {
                        depthBuffer[idx] = vertex.Z;
                        WriteDepthToVram(context, x, y, vertex.Z);
                    }
                    return;
                case GsAlphaTestResult.FailRgbOnly:
                    renderAudit.AlphaFailRgbOnlyPixels++;
                    break;
                default:
                    return;
            }
        }

        if (alphaTest == GsAlphaTestResult.Pass && !ZWriteMasked(context))
        {
            depthBuffer[idx] = vertex.Z;
            WriteDepthToVram(context, x, y, vertex.Z);
        }

        var p = idx * 4;
        var extraFbmsk = alphaTest == GsAlphaTestResult.FailRgbOnly
            ? AlphaWriteMaskForPsm(framebufferTarget.Psm)
            : 0u;
        var probe = ShouldProbe(x, y, framebufferTarget);
        GsSample written;
        GsSample? preBlendDst = null;
        if (state.Abe)
        {
            if (probe && TryReadFramebufferPixel(framebufferTarget, x, y, out var dstSample))
                preBlendDst = dstSample;
            var blended = BlendPixel(context, framebufferTarget, x, y, p, srcR, srcG, srcB, srcA);
            written = WriteFramebufferPixel(
                framebufferTarget,
                context,
                x,
                y,
                ClampByte(blended.R),
                ClampByte(blended.G),
                ClampByte(blended.B),
                ClampByte(blended.A),
                extraFbmsk);
        }
        else
        {
            written = WriteFramebufferPixel(
                framebufferTarget,
                context,
                x,
                y,
                ClampByte(srcR),
                ClampByte(srcG),
                ClampByte(srcB),
                ClampByte(srcA),
                extraFbmsk);
        }

        pixels[p] = (byte)written.R;
        pixels[p + 1] = (byte)written.G;
        pixels[p + 2] = (byte)written.B;
        pixels[p + 3] = 255;
        if (probe)
            EmitPixelProbe(x, y, "POINT", framebufferTarget, context, state.Tme, sample,
                vertex.R, vertex.G, vertex.B, vertex.A, srcR, srcG, srcB, srcA, vertex.Z, preBlendDst, written);
        renderAudit.PixelsTouched++;
        material.PixelsWritten++;
        RecordFramebufferPixels(framebufferTarget, context.Tex0, 1, x, y, x, y);
        InvalidateTextureCacheForFramebufferWrite(framebufferTarget);
        MaybeSaveDrawRt(framebufferTarget);
    }

    private void DrawSprite(GsVertex a, GsVertex b)
    {
        renderAudit.DrawsSeen++;
        renderAudit.SpritesDrawn++;
        var v0 = a;
        var v1 = new GsVertex(b.X, a.Y, b.Z, a.R, a.G, a.B, a.A, b.U, a.V, b.Q, a.Fog, false);
        var v2 = new GsVertex(a.X, b.Y, b.Z, a.R, a.G, a.B, a.A, a.U, b.V, b.Q, a.Fog, false);
        var v3 = b;
        DrawTriangle(v0, v1, v2, countDraw: false);
        DrawTriangle(v2, v1, v3, countDraw: false);
        MaybeSaveDrawRt(DecodeFramebufferTarget(state.Context));
    }

    private void DrawTriangle(GsVertex a, GsVertex b, GsVertex c, bool countDraw = true)
    {
        if (countDraw)
            renderAudit.DrawsSeen++;

        var denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(denom) < 0.0001f)
            return;

        var minX = MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)));
        var maxX = MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)));
        var minY = MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
        var maxY = MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
        var context = state.Context;
        var framebufferTarget = DecodeFramebufferTarget(context);
        var scissor = DecodeScissor(context);
        var x0 = Math.Max(scissor.X0, Math.Clamp((int)minX, 0, options.Width - 1));
        var x1 = Math.Min(scissor.X1, Math.Clamp((int)maxX, 0, options.Width - 1));
        var y0 = Math.Max(scissor.Y0, Math.Clamp((int)minY, 0, options.Height - 1));
        var y1 = Math.Min(scissor.Y1, Math.Clamp((int)maxY, 0, options.Height - 1));
        if (x0 > x1 || y0 > y1)
            return;

        var primitiveName = PrimitiveNames[Math.Clamp(state.PrimType, 0, 7)];
        var invDenom = 1f / denom;
        var texture = state.Tme ? ResolveTexture(context.Tex0) : null;
        if (state.Tme && texture == null)
        {
            renderAudit.TextureDecodeMisses++;
            RecordMissingTextureDraw(context.Tex0, framebufferTarget, x0, y0, x1, y1);
            BeginMaterialDraw(primitiveName, framebufferTarget, x0, y0, x1, y1, missingTexture: true, a, b, c, vertexCount: 3);
            NoteUnsupported("textured_draw_skipped_missing_texture");
            return;
        }

        if (state.Abe)
            NoteApproximation("gs_alpha_blend_approximated");
        if (!IsFramebufferPsmSupported(framebufferTarget.Psm))
            NoteUnsupported($"framebuffer_psm_0x{framebufferTarget.Psm:X2}");

        var material = BeginMaterialDraw(primitiveName, framebufferTarget, x0, y0, x1, y1, missingTexture: false, a, b, c, vertexCount: 3);
        RecordFramebufferDraw(framebufferTarget);
        var depthBuffer = GetDepthBuffer(context);
        long pixelsWritten = 0;
        var writeMinX = options.Width;
        var writeMinY = options.Height;
        var writeMaxX = -1;
        var writeMaxY = -1;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;
                var w0 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) * invDenom;
                var w1 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) * invDenom;
                var w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;

                var z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                var idx = y * options.Width + x;
                if (!PassesDepth(z, idx, context, depthBuffer))
                {
                    renderAudit.DepthRejectedPixels++;
                    continue;
                }

                var sample = Sample(
                    context,
                    texture,
                    InterpolateTextureCoordinate(a.U, b.U, c.U, a.Q, b.Q, c.Q, w0, w1, w2),
                    InterpolateTextureCoordinate(a.V, b.V, c.V, a.Q, b.Q, c.Q, w0, w1, w2));
                if (state.Tme)
                {
                    if (state.Fst)
                        renderAudit.FixedTexturePixels++;
                    else
                        renderAudit.PerspectiveTexturePixels++;
                }

                var vr = a.R * w0 + b.R * w1 + c.R * w2;
                var vg = a.G * w0 + b.G * w1 + c.G * w2;
                var vb = a.B * w0 + b.B * w1 + c.B * w2;
                var va = a.A * w0 + b.A * w1 + c.A * w2;
                CombineTextureFunction(context, sample, vr, vg, vb, va,
                    out var srcR, out var srcG, out var srcB, out var srcA);
                if (state.Fge)
                {
                    var fog = a.Fog * w0 + b.Fog * w1 + c.Fog * w2;
                    ApplyFog(fog, ref srcR, ref srcG, ref srcB);
                }

                var alphaTest = EvaluateAlphaTest(srcA, context);
                if (alphaTest != GsAlphaTestResult.Pass)
                {
                    renderAudit.AlphaFailedPixels++;
                    RecordAlphaFailure(context.Tex0, framebufferTarget, alphaTest, x, y);
                    switch (alphaTest)
                    {
                        case GsAlphaTestResult.FailFramebufferOnly:
                            renderAudit.AlphaFailFramebufferOnlyPixels++;
                            break;
                        case GsAlphaTestResult.FailZBufferOnly:
                            renderAudit.AlphaFailZBufferOnlyPixels++;
                            if (!ZWriteMasked(context))
                            {
                                depthBuffer[idx] = z;
                                WriteDepthToVram(context, x, y, z);
                            }
                            continue;
                        case GsAlphaTestResult.FailRgbOnly:
                            renderAudit.AlphaFailRgbOnlyPixels++;
                            break;
                        default:
                            continue;
                    }
                }

                if (alphaTest == GsAlphaTestResult.Pass && !ZWriteMasked(context))
                {
                    depthBuffer[idx] = z;
                    WriteDepthToVram(context, x, y, z);
                }

                var p = idx * 4;
                var extraFbmsk = alphaTest == GsAlphaTestResult.FailRgbOnly
                    ? AlphaWriteMaskForPsm(framebufferTarget.Psm)
                    : 0u;
                var probe = ShouldProbe(x, y, framebufferTarget);
                if (state.Abe)
                {
                    GsSample? preBlendDst = null;
                    if (probe && TryReadFramebufferPixel(framebufferTarget, x, y, out var dstSample))
                        preBlendDst = dstSample;
                    var blended = BlendPixel(context, framebufferTarget, x, y, p, srcR, srcG, srcB, srcA);
                    var written = WriteFramebufferPixel(
                        framebufferTarget,
                        context,
                        x,
                        y,
                        ClampByte(blended.R),
                        ClampByte(blended.G),
                        ClampByte(blended.B),
                        ClampByte(blended.A),
                        extraFbmsk);
                    pixels[p] = (byte)written.R;
                    pixels[p + 1] = (byte)written.G;
                    pixels[p + 2] = (byte)written.B;
                    pixels[p + 3] = 255;
                    if (probe)
                        EmitPixelProbe(x, y, "TRIANGLE", framebufferTarget, context, state.Tme, sample,
                            vr, vg, vb, va, srcR, srcG, srcB, srcA, z, preBlendDst, written);
                }
                else
                {
                    var outR = (byte)Math.Clamp((int)MathF.Round(srcR), 0, 255);
                    var outG = (byte)Math.Clamp((int)MathF.Round(srcG), 0, 255);
                    var outB = (byte)Math.Clamp((int)MathF.Round(srcB), 0, 255);
                    var outA = (byte)Math.Clamp((int)MathF.Round(srcA), 0, 255);
                    var written = WriteFramebufferPixel(framebufferTarget, context, x, y, outR, outG, outB, outA, extraFbmsk);
                    pixels[p] = (byte)written.R;
                    pixels[p + 1] = (byte)written.G;
                    pixels[p + 2] = (byte)written.B;
                    pixels[p + 3] = 255;
                    if (probe)
                        EmitPixelProbe(x, y, "TRIANGLE", framebufferTarget, context, state.Tme, sample,
                            vr, vg, vb, va, srcR, srcG, srcB, srcA, z, null, written);
                }

                renderAudit.PixelsTouched++;
                pixelsWritten++;
                writeMinX = Math.Min(writeMinX, x);
                writeMinY = Math.Min(writeMinY, y);
                writeMaxX = Math.Max(writeMaxX, x);
                writeMaxY = Math.Max(writeMaxY, y);
            }
        }

        RecordFramebufferPixels(framebufferTarget, context.Tex0, pixelsWritten, writeMinX, writeMinY, writeMaxX, writeMaxY);
        if (pixelsWritten > 0)
            InvalidateTextureCacheForFramebufferWrite(framebufferTarget);
        material.PixelsWritten += pixelsWritten;
        renderAudit.TrianglesDrawn++;
        if (countDraw)
            MaybeSaveDrawRt(framebufferTarget);
    }

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
            r = (byte)Math.Clamp((int)r + dither, 0, 255);
            g = (byte)Math.Clamp((int)g + dither, 0, 255);
            b = (byte)Math.Clamp((int)b + dither, 0, 255);
        }

        vram.WritePixel(target.Fbp, target.Fbw, target.Psm, x, y, r, g, b, a, target.Fbmsk | extraFbmsk);
        var written = vram.ReadPixelRgba(target.Fbp, target.Fbw, target.Psm, x, y);
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
        var dst = TryReadFramebufferPixel(target, x, y, out var framebufferPixel)
            ? framebufferPixel
            : new GsSample(pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]);
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

        var rgba = vram.ReadPixelRgba(target.Fbp, target.Fbw, target.Psm, x, y);
        pixel = new GsSample(rgba.R, rgba.G, rgba.B, rgba.A);
        return true;
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
            X: x,
            Y: y,
            Primitive: primitive,
            DrawIndex: renderAudit.DrawsSeen,
            Fbp: target.Fbp,
            Fbw: target.Fbw,
            Psm: target.Psm,
            Fbmsk: target.Fbmsk,
            Tex0: context.Tex0,
            AlphaRegister: context.Alpha,
            TestRegister: context.Test,
            TextureEnabled: state.Tme,
            BlendEnabled: state.Abe,
            TextureSampled: textureSampled,
            SampleR: sample.R,
            SampleG: sample.G,
            SampleB: sample.B,
            SampleA: sample.A,
            VertexR: vertexR,
            VertexG: vertexG,
            VertexB: vertexB,
            VertexA: vertexA,
            SrcR: srcR,
            SrcG: srcG,
            SrcB: srcB,
            SrcA: srcA,
            Z: z,
            HasPreBlendDst: preBlendDst.HasValue,
            PreBlendDstR: preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.R, 0, 255) : (byte)0,
            PreBlendDstG: preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.G, 0, 255) : (byte)0,
            PreBlendDstB: preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.B, 0, 255) : (byte)0,
            PreBlendDstA: preBlendDst.HasValue ? (byte)Math.Clamp((int)preBlendDst.Value.A, 0, 255) : (byte)0,
            WrittenR: (byte)Math.Clamp((int)written.R, 0, 255),
            WrittenG: (byte)Math.Clamp((int)written.G, 0, 255),
            WrittenB: (byte)Math.Clamp((int)written.B, 0, 255),
            WrittenA: (byte)Math.Clamp((int)written.A, 0, 255));
        options.PixelProbe(info);
    }

    private static float SelectColor(int selector, float src, float dst) =>
        selector switch
        {
            0 => src,
            1 => dst,
            2 => 0f,
            _ => 0f
        };

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private void ApplyFog(float fog, ref float r, ref float g, ref float b)
    {
        var sourceWeight = Math.Clamp(fog / 256f, 0f, 1f);
        var fogR = state.FogColor & 0xFF;
        var fogG = (state.FogColor >> 8) & 0xFF;
        var fogB = (state.FogColor >> 16) & 0xFF;
        r = MathF.Truncate((float)fogR + (r - fogR) * sourceWeight);
        g = MathF.Truncate((float)fogG + (g - fogG) * sourceWeight);
        b = MathF.Truncate((float)fogB + (b - fogB) * sourceWeight);
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
        float tR = sample.R;
        float tG = sample.G;
        float tB = sample.B;
        float tA = sample.A;

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
        // PSM=bits 24..29 (6 bits), ZMSK=bit 32. The Z buffer shares its
        // stride with the colour buffer's FBW (there is no ZBW field).
        var zpsm = (uint)((context.Zbuf >> 24) & 0x3Fu);
        if (zpsm != Ps2GsVram.PSMZ32 && zpsm != Ps2GsVram.PSMZ24)
        {
            // PSMZ16 / PSMZ16S exist but the VRAM module doesn't store them yet.
            if (zpsm is 0x32 or 0x3A)
                NoteApproximation($"depth_psm_0x{zpsm:X2}_not_written_to_vram");
            return;
        }

        var fbw = (uint)((context.Frame >> 16) & 0x3Fu);
        if (fbw == 0)
            return;

        var zbp = (uint)(context.Zbuf & 0x1FFu) << 5;
        var zi = z <= 0f ? 0u : z >= 4294967295f ? 0xFFFFFFFFu : (uint)z;
        var r = (byte)(zi & 0xFFu);
        var g = (byte)((zi >> 8) & 0xFFu);
        var b = (byte)((zi >> 16) & 0xFFu);
        var a = (byte)((zi >> 24) & 0xFFu);
        vram.WritePixel(zbp, fbw, zpsm, x, y, r, g, b, a);
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

    private static bool ZWriteMasked(GsContext context) => ((context.Zbuf >> 32) & 1) != 0;

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

    private static uint AlphaWriteMaskForPsm(uint psm) =>
        psm switch
        {
            Ps2TexPixelDecoder.PSMCT32 => 0xFF000000u,
            Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => 0x8000u,
            _ => 0u
        };

    private static string AlphaFailModeName(GsAlphaTestResult result) =>
        result switch
        {
            GsAlphaTestResult.FailFramebufferOnly => "fb_only",
            GsAlphaTestResult.FailZBufferOnly => "zb_only",
            GsAlphaTestResult.FailRgbOnly => "rgb_only",
            _ => "keep"
        };

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
            return new GsSample(texture.Rgba[i], texture.Rgba[i + 1], texture.Rgba[i + 2], tcc == 0 ? 255 : texture.Rgba[i + 3]);
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
        var g = texture.Rgba[i00 + 1] * w00 + texture.Rgba[i10 + 1] * w10 + texture.Rgba[i01 + 1] * w01 + texture.Rgba[i11 + 1] * w11;
        var b = texture.Rgba[i00 + 2] * w00 + texture.Rgba[i10 + 2] * w10 + texture.Rgba[i01 + 2] * w01 + texture.Rgba[i11 + 2] * w11;
        var a = tcc == 0
            ? 255f
            : texture.Rgba[i00 + 3] * w00 + texture.Rgba[i10 + 3] * w10 + texture.Rgba[i01 + 3] * w01 + texture.Rgba[i11 + 3] * w11;
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
        return ((texel % s) + s) % s;
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

    private GsTexture? ResolveTexture(ulong tex0)
    {
        if (tex0 == 0)
            return null;

        var width = 1 << (int)((tex0 >> 26) & 0xF);
        var height = 1 << (int)((tex0 >> 30) & 0xF);
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var texaSensitive = psm is Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S
            || ((psm is Ps2TexPixelDecoder.PSMT8 or Ps2TexPixelDecoder.PSMT4) && cpsm == Ps2TexPixelDecoder.PSMCT16);
        var cacheKey = new GsTextureCacheKey(tex0, texaSensitive ? state.Texa : 0);

        if (textureCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached != null)
                renderAudit.TextureCacheHits++;
            return cached;
        }

        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            NoteUnsupported("texture_dimensions_out_of_range");
            textureCache[cacheKey] = null;
            return null;
        }

        GsTexture? texture = null;
        GsTexture? allAlphaVramTexture = null;
        string? textureSource = null;
        string? allAlphaVramTextureSource = null;
        uint? sourceChecksum = null;
        if (psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2TexPixelDecoder.PSMCT16
            or Ps2GsVram.PSMCT16S
            or Ps2TexPixelDecoder.PSMT8
            or Ps2TexPixelDecoder.PSMT4
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24)
        {
            if (psm == Ps2GsVram.PSMZ32)
                NoteApproximation("texture_psmz32_as_color");

            if (texture == null)
            {
                var pixelsFromVram = ThawZoneTexVramSupport.DecodeFromTex0(
                    vram,
                    tex0,
                    flipVertical: false,
                    fixAllZeroAlpha: false,
                    texa: texaSensitive ? state.Texa : null);
                if (pixelsFromVram != null)
                {
                    // Use VRAM decode whenever there's *any* pixel data (RGB or alpha).
                    // Distinguish two cases the renderer must handle differently:
                    //   1. Fully empty VRAM (all bytes zero) — texture was never uploaded;
                    //      let TextureResolver / ZoneTextureCatalog provide a fallback so
                    //      synthetic tests + dumps with missing uploads still see content.
                    //   2. RGB present but alpha=0 — game-intentional signal that this draw
                    //      shouldn't contribute (e.g. character rim lighting in inactive state).
                    //      PCSX2 SW uses VRAM as-is here.
                    // Prior over-aggressive external-fallback caused cyan "silver patch" on
                    // character backs by substituting static catalog RGB into case (2).
                    if (!IsAllPixelsZero(pixelsFromVram))
                    {
                        texture = new GsTexture(width, height, pixelsFromVram);
                        textureSource = IsAllAlphaZero(pixelsFromVram) ? "vram_all_alpha_zero" : "vram";
                        if (textureSource == "vram_all_alpha_zero")
                            NoteApproximation("texture_vram_all_alpha_zero");
                    }
                }
            }
        }
        else
        {
            NoteUnsupported($"texture_psm_0x{psm:X2}");
        }

        if (texture == null && options.TextureResolver != null)
        {
            var resolved = options.TextureResolver(tex0);
            if (resolved != null)
            {
                texture = new GsTexture(resolved.Width, resolved.Height, resolved.Rgba);
                textureSource = "external";
                sourceChecksum = resolved.Checksum;
            }
        }

        if (texture == null && allAlphaVramTexture != null)
        {
            texture = allAlphaVramTexture;
            textureSource = allAlphaVramTextureSource;
        }

        if (texture != null)
            RecordTextureDump(tex0, cacheKey.Texa, state.Context.Clamp, texture, textureSource ?? "unknown", sourceChecksum);

        textureCache[cacheKey] = texture;
        return texture;
    }

    private void RecordTextureDump(
        ulong tex0,
        ulong texa,
        ulong clamp,
        GsTexture texture,
        string source,
        uint? sourceChecksum)
    {
        if (options.TextureDumpSink == null)
            return;

        var region = DecodeTextureDumpRegion(clamp, texture.Width, texture.Height);
        var dumpPixels = CropTexture(texture.Rgba, texture.Width, region.X, region.Y, region.Width, region.Height);
        dumpPixels = ApplyTextureDumpAlphaPreview(tex0, dumpPixels);
        var sourceKey = ResolveTextureDumpSourceKey(tex0, source, out var classifiedSource);
        var contentHash = ComputeFnv1A32(dumpPixels);
        var key =
            $"{tex0:X16}|{texa:X16}|{classifiedSource}|{sourceKey}|{region.X},{region.Y},{region.Width}x{region.Height}|{contentHash:X8}";
        if (!dumpedTextureKeys.Add(key))
            return;

        var audit = MakeTextureDumpAuditRow(
            key,
            tex0,
            texa,
            texture,
            classifiedSource,
            sourceKey,
            contentHash,
            sourceChecksum,
            region,
            dumpPixels);
        audit.Path = options.TextureDumpSink(new GsRuntimeTextureDump(audit, dumpPixels));
        renderAudit.TextureDumps.Add(audit);
    }

    private string? ResolveTextureDumpSourceKey(ulong tex0, string source, out string classifiedSource)
    {
        classifiedSource = source;
        if (!source.StartsWith("vram", StringComparison.Ordinal))
            return null;

        if (TryFindFramebufferTextureSource(tex0, out var framebufferKey))
        {
            classifiedSource = "framebuffer";
            return framebufferKey;
        }

        if (TryFindImageUploadTextureSource(tex0, out var imageUploadKey))
        {
            classifiedSource = "image_upload";
            return imageUploadKey;
        }

        return null;
    }

    private bool TryFindFramebufferTextureSource(ulong tex0, out string sourceKey)
    {
        var tbp = (uint)(tex0 & 0x3FFF);
        var tbw = (uint)((tex0 >> 14) & 0x3F);
        var psm = (uint)((tex0 >> 20) & 0x3F);

        var row = renderAudit.FramebufferTargets.Values
            .Where(row => row.Fbp == tbp
                          && row.Fbw == tbw
                          && HasCompatibleFrameTextureLayout(row.Psm, psm))
            .OrderByDescending(row => row.PixelsWritten)
            .FirstOrDefault();
        if (row != null)
        {
            sourceKey = MakeFramebufferKey(row.Fbp, row.Fbw, row.Psm, row.Fbmsk);
            return true;
        }

        sourceKey = "";
        return false;
    }

    private bool TryFindImageUploadTextureSource(ulong tex0, out string sourceKey)
    {
        var tbp = (uint)(tex0 & 0x3FFF);
        var tbw = (uint)((tex0 >> 14) & 0x3F);
        var psm = (uint)((tex0 >> 20) & 0x3F);

        var row = renderAudit.ImageTransferTargets.Values
            .Where(row => row.Dbp == tbp
                          && row.Dbw == tbw
                          && HasCompatibleFrameTextureLayout(row.Dpsm, psm))
            .OrderByDescending(row => row.Bytes)
            .FirstOrDefault();
        if (row != null)
        {
            sourceKey =
                $"DBP={row.Dbp},DBW={row.Dbw},PSM=0x{row.Dpsm:X2},RECT={row.Width}x{row.Height}@{row.Dsax},{row.Dsay}";
            return true;
        }

        sourceKey = "";
        return false;
    }

    private static bool HasCompatibleFrameTextureLayout(uint sourcePsm, uint texturePsm)
    {
        var sourceLayout = GsPixelStorageLayout(sourcePsm);
        return sourceLayout >= 0 && sourceLayout == GsPixelStorageLayout(texturePsm);
    }

    private static int GsPixelStorageLayout(uint psm) =>
        psm switch
        {
            Ps2TexPixelDecoder.PSMCT32 or Ps2TexPixelDecoder.PSMCT24 or Ps2GsVram.PSMZ32 or Ps2GsVram.PSMZ24 => 32,
            Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => 16,
            Ps2TexPixelDecoder.PSMT8 => 8,
            Ps2TexPixelDecoder.PSMT4 => 4,
            _ => -1
        };

    private static byte[] ApplyTextureDumpAlphaPreview(ulong tex0, byte[] rgba)
    {
        var tcc = (tex0 >> 34) & 0x1;
        if (tcc != 0)
            return rgba;

        var output = rgba.ToArray();
        for (var i = 3; i < output.Length; i += 4)
            output[i] = 255;

        return output;
    }

    private static (int X, int Y, int Width, int Height) DecodeTextureDumpRegion(ulong clamp, int textureWidth, int textureHeight)
    {
        var x0 = 0;
        var y0 = 0;
        var x1 = textureWidth - 1;
        var y1 = textureHeight - 1;
        if ((clamp & 0x3) == 2)
        {
            x0 = (int)((clamp >> 4) & 0x3FF);
            x1 = (int)((clamp >> 14) & 0x3FF);
        }

        if (((clamp >> 2) & 0x3) == 2)
        {
            y0 = (int)((clamp >> 24) & 0x3FF);
            y1 = (int)((clamp >> 34) & 0x3FF);
        }

        if (x1 < x0)
            (x0, x1) = (x1, x0);
        if (y1 < y0)
            (y0, y1) = (y1, y0);

        x0 = Math.Clamp(x0, 0, textureWidth - 1);
        x1 = Math.Clamp(x1, 0, textureWidth - 1);
        y0 = Math.Clamp(y0, 0, textureHeight - 1);
        y1 = Math.Clamp(y1, 0, textureHeight - 1);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static byte[] CropTexture(byte[] rgba, int sourceWidth, int x, int y, int width, int height)
    {
        if (x == 0 && y == 0 && width == sourceWidth && rgba.Length == width * height * 4)
            return rgba;

        var output = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var src = ((y + row) * sourceWidth + x) * 4;
            var dst = row * width * 4;
            rgba.AsSpan(src, width * 4).CopyTo(output.AsSpan(dst));
        }

        return output;
    }

    private static GsTextureDumpAuditRow MakeTextureDumpAuditRow(
        string key,
        ulong tex0,
        ulong texa,
        GsTexture texture,
        string source,
        string? sourceKey,
        uint contentHash,
        uint? sourceChecksum,
        (int X, int Y, int Width, int Height) region,
        byte[] dumpPixels)
    {
        return new GsTextureDumpAuditRow
        {
            Key = key,
            Source = source,
            SourceKey = sourceKey,
            Tex0 = $"0x{tex0:X16}",
            Texa = $"0x{texa:X16}",
            ContentHash = contentHash,
            SourceChecksum = sourceChecksum,
            Tbp = (uint)(tex0 & 0x3FFF),
            Tbw = (uint)((tex0 >> 14) & 0x3F),
            Psm = (uint)((tex0 >> 20) & 0x3F),
            TextureWidth = texture.Width,
            TextureHeight = texture.Height,
            RegionX = region.X,
            RegionY = region.Y,
            Width = region.Width,
            Height = region.Height,
            Tcc = (uint)((tex0 >> 34) & 0x1),
            Tfx = (uint)((tex0 >> 35) & 0x3),
            Cbp = (uint)((tex0 >> 37) & 0x3FFF),
            Cpsm = (uint)((tex0 >> 51) & 0xF),
            Csm = (uint)((tex0 >> 55) & 0x1),
            Csa = (uint)((tex0 >> 56) & 0x1F),
            Cld = (uint)((tex0 >> 61) & 0x7),
            AllAlphaZero = IsAllAlphaZero(dumpPixels)
        };
    }

    private static uint ComputeFnv1A32(ReadOnlySpan<byte> data)
    {
        var hash = 2166136261u;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    private static bool IsAllAlphaZero(byte[] rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0)
                return false;
        }

        return true;
    }

    private static bool IsAllPixelsZero(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i++)
        {
            if (rgba[i] != 0)
                return false;
        }

        return true;
    }

    private void WriteImageData(ReadOnlySpan<byte> data)
    {
        var xdir = (int)(state.Trxdir & 0x3);
        if (xdir != 0)
        {
            NoteUnsupported($"image_transfer_xdir_{xdir}");
            return;
        }

        var dbp = (uint)((state.Bitbltbuf >> 32) & 0x3FFF);
        var dbw = (uint)((state.Bitbltbuf >> 48) & 0x3F);
        var dpsm = (uint)((state.Bitbltbuf >> 56) & 0x3F);
        var rrw = (int)(state.Trxreg & 0xFFF);
        var rrh = (int)((state.Trxreg >> 32) & 0xFFF);
        var dsax = (int)((state.Trxpos >> 32) & 0x7FF);
        var dsay = (int)((state.Trxpos >> 48) & 0x7FF);
        if (rrw <= 0 || rrh <= 0)
        {
            NoteUnsupported("image_transfer_missing_trxreg");
            return;
        }

        var expectedBytes = ThawZoneTexVramSupport.GetTransferSizeBytes(dpsm, rrw, rrh);
        if (expectedBytes <= 0)
        {
            NoteUnsupported("image_transfer_empty");
            return;
        }

        var descriptor = new GsImageTransferDescriptor(dbp, dbw, dpsm, rrw, rrh, dsax, dsay, expectedBytes);
        if (activeImageTransfer == null || activeImageTransfer.Descriptor != descriptor)
        {
            if (activeImageTransfer is { BytesWritten: > 0 } interrupted)
                NoteUnsupported($"image_transfer_interrupted_{interrupted.BytesWritten}_of_{interrupted.ExpectedBytes}");

            activeImageTransfer = new GsImageTransfer(descriptor);
            renderAudit.ImageTransfersStarted++;
        }

        var transfer = activeImageTransfer;
        var remaining = transfer.ExpectedBytes - transfer.BytesWritten;
        var bytesToCopy = Math.Min(remaining, data.Length);
        data[..bytesToCopy].CopyTo(transfer.Buffer.AsSpan(transfer.BytesWritten));
        transfer.BytesWritten += bytesToCopy;
        renderAudit.ImageTransferBytes += bytesToCopy;

        if (data.Length > bytesToCopy)
            NoteUnsupported("image_transfer_extra_data_ignored");

        if (transfer.BytesWritten < transfer.ExpectedBytes)
            return;

        vram.WriteRect(dbp, dbw, dpsm, rrw, rrh, transfer.Buffer, dsax, dsay);
        RecordImageTransfer(descriptor);
        renderAudit.ImageTransfersCompleted++;
        activeImageTransfer = null;
        textureCache.Clear();
    }

    private void PresentFromDisplayBuffer(GsDumpFile dump)
    {
        // Need at least through BGCOLOR (offset 224 + 8). The previous threshold was 168
        // (a single circuit's DISPFB+DISPLAY) which masked the dual-circuit case.
        if (dump.Registers.Length < 232)
        {
            NoteUnsupported("display_registers_missing");
            return;
        }

        static ulong U64(ReadOnlySpan<byte> source, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);

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

        var circuit1 = TryReadCircuit(regs, circuitIndex: 0, dispfbOffset: 112, displayOffset: 128, enabled: en1);
        var circuit2 = TryReadCircuit(regs, circuitIndex: 1, dispfbOffset: 144, displayOffset: 160, enabled: en2);
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
                    outR = c2R; outG = c2G; outB = c2B;
                }
                else if (en1 && !en2 && !slbg)
                {
                    outR = c1R; outG = c1G; outB = c1B;
                }
                else
                {
                    var backR = (slbg || !en2) ? bgR : c2R;
                    var backG = (slbg || !en2) ? bgG : c2G;
                    var backB = (slbg || !en2) ? bgB : c2B;
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

    private readonly record struct CircuitLayer(byte[]? Rgba, GsPresentedCircuitAudit Audit);

    private CircuitLayer TryReadCircuit(ReadOnlySpan<byte> regs, int circuitIndex, int dispfbOffset, int displayOffset, bool enabled)
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
            Magv = magv,
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
            DrawIndex: drawIndex,
            Fbp: target.Fbp,
            Fbw: target.Fbw,
            Psm: target.Psm,
            Fbmsk: target.Fbmsk,
            Width: width,
            Height: height,
            Rgba: rgba));
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

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    private void AddRegWrite(string name) => AddCount(gifAudit.RegisterWrites, name);

    private void NoteUnsupported(string name) => AddCount(renderAudit.UnsupportedStates, name);

    private void NoteApproximation(string name) => AddCount(renderAudit.Approximations, name);

    private static GsFramebufferTarget DecodeFramebufferTarget(GsContext context) =>
        new(
            (uint)(context.Frame & 0x1FF) << 5,
            (uint)((context.Frame >> 16) & 0x3F),
            (uint)((context.Frame >> 24) & 0x3F),
            (uint)(context.Frame >> 32));

    private static bool IsFramebufferPsmSupported(uint psm) =>
        psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2TexPixelDecoder.PSMCT16
            or Ps2GsVram.PSMCT16S
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24;

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

    private string MakeMaterialKey(string primitiveName, GsContext context, GsFramebufferTarget target) =>
        string.Join(
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

    private static string MakeFramebufferKey(GsFramebufferTarget target) =>
        MakeFramebufferKey(target.Fbp, target.Fbw, target.Psm, target.Fbmsk);

    private static string MakeFramebufferKey(uint fbp, uint fbw, uint psm, uint fbmsk) =>
        $"FBP={fbp},FBW={fbw},PSM=0x{psm:X2},FBMSK=0x{fbmsk:X8}";

    private static string MakeImageTransferKey(GsImageTransferDescriptor descriptor) =>
        $"DBP={descriptor.Dbp},DBW={descriptor.Dbw},PSM=0x{descriptor.Dpsm:X2},RECT={descriptor.Width}x{descriptor.Height}@{descriptor.Dsax},{descriptor.Dsay}";

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

    private sealed class GsMaterialAccumulator
    {
        private readonly ulong prim;
        private readonly int contextIndex;
        private readonly bool textureEnabled;
        private readonly bool fogEnabled;
        private readonly bool alphaBlendEnabled;
        private readonly bool fixedTextureCoordinates;
        private readonly ulong tex0;
        private readonly ulong tex1;
        private readonly ulong clamp;
        private readonly ulong alpha;
        private readonly ulong test;
        private readonly ulong texa;
        private readonly ulong fogColor;
        private readonly ulong zbuf;
        private readonly ulong scissorRaw;
        private readonly ulong dthe;
        private readonly ulong fba;
        private readonly string framebufferKey;
        private readonly GsFramebufferTarget framebuffer;
        private readonly (int X0, int Y0, int X1, int Y1) scissor;
        private double minR = double.PositiveInfinity;
        private double maxR = double.NegativeInfinity;
        private double sumR;
        private double minG = double.PositiveInfinity;
        private double maxG = double.NegativeInfinity;
        private double sumG;
        private double minB = double.PositiveInfinity;
        private double maxB = double.NegativeInfinity;
        private double sumB;
        private double minA = double.PositiveInfinity;
        private double maxA = double.NegativeInfinity;
        private double sumA;
        private double minU = double.PositiveInfinity;
        private double maxU = double.NegativeInfinity;
        private double minV = double.PositiveInfinity;
        private double maxV = double.NegativeInfinity;
        private double minQ = double.PositiveInfinity;
        private double maxQ = double.NegativeInfinity;
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

        private bool HasVertices => vertexCount > 0;
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
