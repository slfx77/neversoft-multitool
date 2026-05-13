using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

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
        // 4-byte-aligned brute-force scan: this catches more real batch starts
        // than walking via VifNextCode (the walker skips over large blocks of
        // raw vertex data it mis-interprets as UNPACK-V4_5 NUM=255, missing real
        // MSCALs embedded in that data). The cost is false positives, but the
        // per-batch sanity filter in Ps2GeomVifVertexDecoder drops batches whose
        // decoded positions land outside a sane world-space envelope.
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

    internal static Ps2GeomGsContextScan? ScanBatchForGsContext(byte[] data, int batchStart, int batchEnd)
    {
        // Primary pattern: V4_32 num=1 (GIF tag) followed by V3_32 num>=1 (register writes).
        // This is the standard GEOM/MDL GS context setup block. The register list count
        // is normally 5 (TEX0/TEX1/CLAMP/ALPHA/TEST), but THAW worldzone leaves can
        // also emit short register lists or a single NOP placeholder when all GS state
        // should be inherited from the previous draw.
        var pCode = batchStart;
        while (pCode < batchEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var num = data[pCode + 2];

            if ((cmd & 0x7F) == 0x6C && num == 1)
            {
                var nextP = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
                if (nextP + 4 <= data.Length && (data[nextP + 3] & 0x7F) == 0x68 && data[nextP + 2] >= 1)
                    return TryExtractRegistersFromV332(data, nextP, data[nextP + 2]);
            }

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
        }

        // Fallback: standalone V3_32 blocks with GS register writes (TEX0, ALPHA, etc.)
        // found in THAW PAK MDL files where texture switches appear as bare register
        // write blocks not preceded by V4_32 GIF tag setup.
        return ScanBatchForStandaloneGsRegisters(data, batchStart, batchEnd);
    }

    /// <summary>
    ///     Scan for standalone UNPACK V3_32 blocks containing GS register writes.
    ///     These appear in THAW PAK MDL files between vertex data sections, where
    ///     texture changes are encoded as bare register write blocks without the
    ///     standard V4_32 num=1 GIF tag prefix.
    /// </summary>
    private static Ps2GeomGsContextScan? ScanBatchForStandaloneGsRegisters(byte[] data, int batchStart, int batchEnd)
    {
        Ps2GeomGsContextScan? lastCtx = null;
        var pCode = batchStart;

        while (pCode < batchEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var num = data[pCode + 2];

            // V3_32 (opcode 0x68) with num >= 1: potential GS register write block.
            // Each entry is 12 bytes: lo32 + hi32 + reg_addr. Even single-entry blocks
            // are valid (THAW worldzone leaves frequently write only TEX0_1).
            // Validate by checking that at least one entry has a known GS register address.
            if ((cmd & 0x7F) == 0x68 && num >= 1)
            {
                var ctx = TryExtractRegistersFromV332(data, pCode, num);
                if (ctx is { HasRegisters: true })
                    lastCtx = ctx;
            }

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, batchEnd);
        }

        return lastCtx;
    }

    private static Ps2GeomGsContextScan? TryExtractRegistersFromV332(byte[] data, int vifOffset, int num)
    {
        var regDataStart = vifOffset + 4;
        var ctx = new Ps2GeomGsContext();
        var present = GsRegisterMask.None;

        for (var i = 0; i < num; i++)
        {
            var off = regDataStart + i * 12;
            if (off + 12 > data.Length)
                return null;

            var lo32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            var hi32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            var reg = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8));
            var value = ((ulong)hi32 << 32) | lo32;

            switch (reg)
            {
                case 0x06:
                    ctx.Tex0 = value;
                    present |= GsRegisterMask.Tex0;
                    break;
                case 0x14:
                    ctx.Tex1 = value;
                    present |= GsRegisterMask.Tex1;
                    break;
                case 0x34:
                    ctx.MipTbp1 = value;
                    present |= GsRegisterMask.MipTbp1;
                    break;
                case 0x36:
                    ctx.MipTbp2 = value;
                    present |= GsRegisterMask.MipTbp2;
                    break;
                case 0x08:
                    ctx.Clamp1 = value;
                    present |= GsRegisterMask.Clamp1;
                    break;
                case 0x42:
                    ctx.Alpha1 = value;
                    present |= GsRegisterMask.Alpha1;
                    break;
                case 0x47:
                    ctx.Test1 = value;
                    present |= GsRegisterMask.Test1;
                    break;
                case 0x3B:
                    ctx.Texa = value;
                    present |= GsRegisterMask.Texa;
                    break;
                case 0x4C:
                    ctx.Frame1 = value;
                    present |= GsRegisterMask.Frame1;
                    break;
            }
        }

        return new Ps2GeomGsContextScan(ctx, present);
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

        // Signature: STCYCL (0x01) followed by UNPACK-V4_32 NUM=1 (0x6C ~ GIF tag upload).
        if ((data[offset + 3] & 0x7F) != 0x01)
            return false;

        if ((data[offset + 7] & 0x7F) != 0x6C || data[offset + 6] != 1)
            return false;

        // Either a full setup batch (STMOD-gated position UNPACK + other attribs + MSCAL)
        // or a minimal continuation sub-chunk (STMOD=1 + V4_32/V4_16 position UNPACK
        // + MSCAL, no extra attributes). Both are valid boundaries for glTF extraction.
        var pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, offset + 4, end);
        var sawPosition = false;
        var sawAnyUnpack = false;

        for (var step = 0; step < 16 && pCode + 4 <= data.Length && pCode < end && pCode < offset + 0x400; step++)
        {
            var cmd = data[pCode + 3];
            var op = cmd & 0x7F;

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
                    sawAnyUnpack = true;
                    pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, next, end);
                    continue;
                }

                return false;
            }

            if ((cmd & 0x60) == 0x60)
                sawAnyUnpack = true;

            if (op == 0x14)
                return sawAnyUnpack;

            pCode = Ps2GeomVifVertexDecoder.VifNextCode(data, pCode, end);
        }

        return false;
    }
}
