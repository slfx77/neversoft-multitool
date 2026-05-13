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
    }
}
