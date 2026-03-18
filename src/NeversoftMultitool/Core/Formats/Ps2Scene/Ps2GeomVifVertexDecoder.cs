using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class Ps2GeomVifVertexDecoder
{
    private const float PositionScale = 1f / 16f;
    private const float UvScale = 1f / 4096f;

    internal static Ps2Vertex[] ExtractVerticesFromDma(byte[] data, int dmaOffset, Vector3 center)
    {
        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dmaOffset));
        var pStart = dmaOffset + 8;
        var pEnd = dmaOffset + 16 + (qwc << 4);

        if (pEnd > data.Length)
            return [];

        return ExtractVerticesFromVif(data, pStart, pEnd, center);
    }

    internal static Ps2Vertex[] ExtractVerticesFromVif(byte[] data, int pStart, int pEnd, Vector3 center)
    {
        var batches = new List<VifBatch>();
        var currentBatch = new VifBatch();
        var stmodActive = false;

        var pCode = pStart;
        while (pCode < pEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];

            if ((cmd & 0x7F) == 0x05)
            {
                stmodActive = data[pCode] == 1;
                pCode = VifNextCode(data, pCode, pEnd);
                continue;
            }

            if ((cmd & 0x7F) == 0x14)
            {
                if (currentBatch.PositionOffset >= 0)
                    batches.Add(currentBatch);

                currentBatch = new VifBatch();
                stmodActive = false;
                pCode = VifNextCode(data, pCode, pEnd);
                continue;
            }

            if ((cmd & 0x60) == 0x60)
            {
                var vn = (cmd >> 2) & 3;
                var vl = cmd & 3;
                var num = data[pCode + 2];
                var unpackDataOffset = pCode + 4;

                if (stmodActive && (cmd & 0x7E) == 0x6C)
                {
                    currentBatch.PositionOffset = unpackDataOffset;
                    currentBatch.PositionCount = num;
                    currentBatch.PositionIs16Bit = (cmd & 0x01) != 0;
                    stmodActive = false;
                }
                else if (num > 1)
                {
                    if (vn == 1)
                    {
                        currentBatch.UvOffset = unpackDataOffset;
                        currentBatch.UvCount = num;
                        currentBatch.UvIs16Bit = vl == 1;
                        currentBatch.UvIs32Bit = vl == 0;
                    }
                    else if (vn == 2 && vl == 1)
                    {
                        currentBatch.NormalOffset = unpackDataOffset;
                        currentBatch.NormalCount = num;
                    }
                    else if (vn == 3 && vl == 2)
                    {
                        currentBatch.ColorOffset = unpackDataOffset;
                        currentBatch.ColorCount = num;
                        currentBatch.ColorIs3Byte = false;
                    }
                    else if (vn == 2 && vl == 2 && currentBatch.ColorOffset < 0)
                    {
                        currentBatch.ColorOffset = unpackDataOffset;
                        currentBatch.ColorCount = num;
                        currentBatch.ColorIs3Byte = true;
                    }
                }
            }

            pCode = VifNextCode(data, pCode, pEnd);
        }

        if (currentBatch.PositionOffset >= 0)
            batches.Add(currentBatch);

        if (batches.Count == 0)
            return [];

        var allVertices = new List<Ps2Vertex>();
        foreach (var batch in batches)
        {
            if (batch.PositionCount == 0)
                continue;

            for (var i = 0; i < batch.PositionCount; i++)
            {
                var position = ReadPosition(data, batch, i, center);
                if (!position.HasValue)
                    break;

                var (px, py, pz, isStripRestart) = position.Value;
                var (u, v, hasUv) = ReadUv(data, batch, i);
                var (cr, cg, cb, ca, hasColor) = ReadColor(data, batch, i);
                var (normal, hasNormal) = ReadNormal(data, batch, i);

                allVertices.Add(new Ps2Vertex(
                    new Vector3(px, py, pz),
                    normal,
                    cr,
                    cg,
                    cb,
                    ca,
                    u,
                    v,
                    hasNormal,
                    hasColor,
                    hasUv,
                    isStripRestart));
            }
        }

        return allVertices.ToArray();
    }

    internal static int VifNextCode(byte[] data, int offset, int end)
    {
        if (offset >= end || offset + 4 > data.Length)
            return end;

        var cmd = data[offset + 3];
        if ((cmd & 0x60) != 0x60)
        {
            return (cmd & 0x7F) switch
            {
                0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                    or 0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x17 => offset + 4,
                0x20 => offset + 8,
                0x30 or 0x31 => offset + 20,
                0x4A => offset + (data[offset + 2] << 3) + 4,
                0x50 or 0x51 => offset + (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) << 4) + 4,
                _ => offset + 4
            };
        }

        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[offset + 2];
        var dimension = vn + 1;
        var bitLength = 32 >> vl;
        var dataSize = ((bitLength * dimension * num + 31) >> 5) << 2;
        return offset + 4 + dataSize;
    }

    internal static Ps2GeomGsContext ExtractGsContextFromDma(byte[] data, int dmaOffset)
    {
        if (dmaOffset + 16 > data.Length)
            return new Ps2GeomGsContext();

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dmaOffset));
        var pStart = dmaOffset + 16;
        var pEnd = dmaOffset + 16 + (qwc << 4);
        if (pEnd > data.Length)
            return new Ps2GeomGsContext();

        return ExtractGsContextFromVif(data, pStart, pEnd);
    }

    internal static Ps2GeomGsContext ExtractGsContextFromVif(byte[] data, int pStart, int pEnd)
    {
        var ctx = new Ps2GeomGsContext();

        var pCode = pStart;
        while (pCode < pEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var num = data[pCode + 2];

            if ((cmd & 0x7F) == 0x6C && num == 1)
            {
                var nextP = pCode + 4 + 16;
                if (nextP + 4 > data.Length)
                    break;

                var nextCmd = data[nextP + 3];
                var nextNum = data[nextP + 2];
                if ((nextCmd & 0x7F) != 0x68 || nextNum <= 0)
                {
                    pCode = VifNextCode(data, pCode, pEnd);
                    continue;
                }

                var regDataStart = nextP + 4;
                for (var i = 0; i < nextNum; i++)
                {
                    var off = regDataStart + i * 12;
                    if (off + 12 > data.Length)
                        break;

                    var lo32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
                    var hi32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
                    var reg = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8));
                    var value = ((ulong)hi32 << 32) | lo32;

                    switch (reg)
                    {
                        case 0x06: ctx.Tex0 = value; break;
                        case 0x14: ctx.Tex1 = value; break;
                        case 0x34: ctx.MipTbp1 = value; break;
                        case 0x36: ctx.MipTbp2 = value; break;
                        case 0x08: ctx.Clamp1 = value; break;
                        case 0x42: ctx.Alpha1 = value; break;
                        case 0x47: ctx.Test1 = value; break;
                    }
                }

                return ctx;
            }

            pCode = VifNextCode(data, pCode, pEnd);
        }

        return ctx;
    }

    private static (float X, float Y, float Z, bool IsStripRestart)? ReadPosition(
        byte[] data,
        VifBatch batch,
        int index,
        Vector3 center)
    {
        if (batch.PositionIs16Bit)
        {
            var off = batch.PositionOffset + index * 8;
            if (off + 8 > data.Length)
                return null;

            var sx = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off));
            var sy = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2));
            var sz = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 4));
            var sw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 6));
            return (sx * PositionScale + center.X, sy * PositionScale + center.Y, sz * PositionScale + center.Z,
                (sw & 0x8000) != 0);
        }

        var off32 = batch.PositionOffset + index * 16;
        if (off32 + 16 > data.Length)
            return null;

        var x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off32));
        var y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off32 + 4));
        var z = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off32 + 8));
        var w = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off32 + 12));
        return (x * PositionScale + center.X, y * PositionScale + center.Y, z * PositionScale + center.Z,
            (w & 0x8000) != 0);
    }

    private static (float U, float V, bool HasUv) ReadUv(byte[] data, VifBatch batch, int index)
    {
        if (batch.UvOffset < 0 || index >= batch.UvCount)
            return (0, 0, false);

        if (batch.UvIs16Bit)
        {
            var off = batch.UvOffset + index * 4;
            if (off + 4 > data.Length)
                return (0, 0, false);

            return (
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off)) * UvScale,
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2)) * UvScale,
                true);
        }

        if (!batch.UvIs32Bit)
            return (0, 0, false);

        var off32 = batch.UvOffset + index * 8;
        if (off32 + 8 > data.Length)
            return (0, 0, false);

        return (
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off32)) * UvScale,
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off32 + 4)) * UvScale,
            true);
    }

    private static (byte R, byte G, byte B, byte A, bool HasColor) ReadColor(byte[] data, VifBatch batch, int index)
    {
        if (batch.ColorOffset < 0 || index >= batch.ColorCount)
            return (128, 128, 128, 128, false);

        if (batch.ColorIs3Byte)
        {
            var off = batch.ColorOffset + index * 3;
            if (off + 3 > data.Length)
                return (128, 128, 128, 128, false);

            return (data[off], data[off + 1], data[off + 2], 128, true);
        }

        var off32 = batch.ColorOffset + index * 4;
        if (off32 + 4 > data.Length)
            return (128, 128, 128, 128, false);

        return (data[off32], data[off32 + 1], data[off32 + 2], data[off32 + 3], true);
    }

    private static (Vector3 Normal, bool HasNormal) ReadNormal(byte[] data, VifBatch batch, int index)
    {
        if (batch.NormalOffset < 0 || index >= batch.NormalCount)
            return (Vector3.UnitY, false);

        var off = batch.NormalOffset + index * 6;
        if (off + 6 > data.Length)
            return (Vector3.UnitY, false);

        var nx = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off));
        var ny = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2));
        var nz = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 4));
        var normal = new Vector3(nx / 32767f, ny / 32767f, nz / 32767f);
        var length = normal.Length();
        if (length > 0.001f)
            normal /= length;
        else
            normal = Vector3.UnitY;

        return (normal, true);
    }

    private struct VifBatch
    {
        public int PositionOffset;
        public int PositionCount;
        public bool PositionIs16Bit;
        public int UvOffset;
        public int UvCount;
        public bool UvIs16Bit;
        public bool UvIs32Bit;
        public int NormalOffset;
        public int NormalCount;
        public int ColorOffset;
        public int ColorCount;
        public bool ColorIs3Byte;

        public VifBatch()
        {
            PositionOffset = -1;
            UvOffset = -1;
            NormalOffset = -1;
            ColorOffset = -1;
        }
    }
}
