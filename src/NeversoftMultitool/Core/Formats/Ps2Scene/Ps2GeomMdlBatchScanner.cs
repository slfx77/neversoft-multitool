using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class Ps2GeomMdlBatchScanner
{
    internal static int FindMdlVifStart(byte[] data)
    {
        for (var i = 0; i < data.Length - 7; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != 0x01000101)
                continue;

            var next = i + 4;
            if (next + 4 > data.Length)
                continue;

            if ((data[next + 3] & 0x7F) == 0x6C && data[next + 2] == 1)
                return i;
        }

        return -1;
    }

    internal static List<(int Start, int End)> FindMscalBatchRanges(byte[] data, int vifStart, int vifEnd)
    {
        var ranges = new List<(int, int)>();
        var batchStart = vifStart;
        var pCode = vifStart;

        while (pCode < vifEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            if ((cmd & 0x7F) == 0x14)
            {
                var batchEnd = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, vifEnd);
                ranges.Add((batchStart, batchEnd));
                batchStart = batchEnd;
                pCode = batchEnd;
                continue;
            }

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, vifEnd);
        }

        return ranges;
    }

    internal static List<(int Start, int End)> FindRepeatedBatchSignatureRanges(byte[] data, int vifStart, int vifEnd)
    {
        var starts = new List<int>();
        for (var i = vifStart; i + 8 <= vifEnd && i + 8 <= data.Length; i += 4)
        {
            if (IsLikelyMdlMeshBatchStart(data, i, vifEnd))
                starts.Add(i);
        }

        var ranges = new List<(int Start, int End)>();
        if (starts.Count == 0)
            return ranges;

        if (starts[0] > vifStart)
            ranges.Add((vifStart, starts[0]));

        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : vifEnd;
            if (end > start)
                ranges.Add((start, end));
        }

        return ranges;
    }

    internal static Ps2GeomGsContext? ScanBatchForGsContext(byte[] data, int batchStart, int batchEnd)
    {
        var pCode = batchStart;
        while (pCode < batchEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var num = data[pCode + 2];

            if ((cmd & 0x7F) == 0x6C && num == 1)
            {
                var nextP = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
                if (nextP + 4 <= data.Length && (data[nextP + 3] & 0x7F) == 0x68 && data[nextP + 2] > 1)
                    return Ps2GeomVifVertexDecoder.ExtractGsContextFromVif(data, pCode, batchEnd);
            }

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
        }

        return null;
    }

    internal static Vector3? ScanBatchForCenter(byte[] data, int batchStart, int batchEnd)
    {
        var pCode = batchStart;
        while (pCode < batchEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var num = data[pCode + 2];

            if ((cmd & 0x7F) == 0x6C && num == 1)
            {
                var nextP = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
                if (nextP + 4 <= data.Length && (data[nextP + 3] & 0x7F) == 0x68 && data[nextP + 2] == 1)
                {
                    var centerOff = nextP + 4;
                    if (centerOff + 12 <= data.Length)
                    {
                        return new Vector3(
                            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(centerOff)),
                            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(centerOff + 4)),
                            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(centerOff + 8)));
                    }
                }
            }

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
        }

        return null;
    }

    private static bool IsLikelyMdlMeshBatchStart(byte[] data, int offset, int end)
    {
        if (offset + 8 > data.Length || offset + 8 > end)
            return false;

        if ((data[offset + 3] & 0x7F) != 0x01)
            return false;

        if ((data[offset + 7] & 0x7F) != 0x6C || data[offset + 6] != 1)
            return false;

        var pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, offset + 4, end);
        var sawVertexAttrib = false;
        var sawPosition = false;

        for (var step = 0; step < 12 && pCode + 4 <= data.Length && pCode < end && pCode < offset + 0x200; step++)
        {
            var cmd = data[pCode + 3];
            var op = cmd & 0x7F;
            var num = data[pCode + 2];

            if (op == 0x01 && !sawPosition)
                return false;

            if (op == 0x05 && data[pCode] == 1)
            {
                var next = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, end);
                if (next + 4 > data.Length)
                    return false;

                var nextCmd = data[next + 3];
                if ((nextCmd & 0x60) == 0x60 && (nextCmd & 0x7E) == 0x6C)
                {
                    sawPosition = true;
                    pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, next, end);
                    continue;
                }

                return false;
            }

            if ((cmd & 0x60) == 0x60 && num > 0 && (op & 0x7E) != 0x6C)
                sawVertexAttrib = true;

            if (op == 0x14)
                return sawVertexAttrib && sawPosition;

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, end);
        }

        return false;
    }
}
