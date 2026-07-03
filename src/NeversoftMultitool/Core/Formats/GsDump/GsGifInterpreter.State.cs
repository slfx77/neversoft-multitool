using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed partial class GsGifInterpreter
{
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
        state.S = BitConverter.ToSingle(qword[..4]);
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
        LoadClutForTex0(value);
    }

    /// <summary>
    ///     TEX2 writes only the CLUT-relevant TEX0 fields (PSM, CBP, CPSM, CSM, CSA, CLD),
    ///     preserving TBP/TBW/TW/TH/TCC/TFX — the register games use for palette swaps.
    /// </summary>
    private void SetTex2(ulong value, int contextIndex)
    {
        const ulong tex2Mask = 0xFFFFFFE000000000UL | (0x3FUL << 20);
        var index = Math.Clamp(contextIndex, 0, 1);
        var merged = (state.Contexts[index].Tex0 & ~tex2Mask) | (value & tex2Mask);
        SetTex0(merged, index);
    }

    /// <summary>
    ///     GS on-chip CLUT load per TEX0.CLD: 0 = keep current buffer, 1/2/3 = load
    ///     (2/3 also latch CBP into CBP0/CBP1), 4/5 = load only when CBP differs from
    ///     the latched CBP0/CBP1. The snapshot is the cooked palette (the same bytes the
    ///     decode consumes), taken from VRAM NOW — later VRAM overwrites of the palette
    ///     pool must not change already-loaded palettes (THAW time-multiplexes its
    ///     shadow/character LUTs through the 0x3590-0x359F pool).
    /// </summary>
    private void LoadClutForTex0(ulong tex0)
    {
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var indexed = psm is Texture.Ps2.Ps2TexPixelDecoder.PSMT8 or Texture.Ps2.Ps2TexPixelDecoder.PSMT4
            or 0x1B or 0x24 or 0x2C; // T8H, T4HL, T4HH
        if (!indexed)
            return;

        var cbp = (uint)((tex0 >> 37) & 0x3FFF);
        var cld = (uint)((tex0 >> 61) & 0x7);
        var load = cld switch
        {
            1 or 2 or 3 => true,
            4 => cbp != state.Cbp0,
            5 => cbp != state.Cbp1,
            _ => false
        };
        if (cld is 2 or 4)
            state.Cbp0 = cbp;
        if (cld is 3 or 5)
            state.Cbp1 = cbp;
        if (!load)
            return;

        // v1: snapshot 256-entry (PSMT8-family) palettes only. PSMT4 loads occupy 16-entry
        // CSA slots of the shared buffer; modeling the slot accumulation is deferred and
        // PSMT4 reads keep the live-VRAM path.
        if (psm is not (Texture.Ps2.Ps2TexPixelDecoder.PSMT8 or 0x1B))
            return;

        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var csm = (uint)((tex0 >> 55) & 0x1);
        var cooked = Texture.Ps2Scene.ZoneTex.ThawZoneTexVramSupport.FetchClut(
            vram, Texture.Ps2.Ps2TexPixelDecoder.PSMT8, cbp, cpsm, csm);
        if (cooked == null)
            return;

        state.ClutSnapshot = cooked;
        state.ClutSnapshotCpsm = cpsm;
        state.ClutGeneration++;
    }
}
