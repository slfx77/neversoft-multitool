using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parser for PS2 GEOM files (.geom.ps2).
///     These contain pre-compiled CGeomNode rendering trees with embedded VIF/DMA chains.
///     Vertex data is extracted by walking VIF opcodes and decoding UNPACK instructions.
///     File format (from THUG source geomnode.cpp sProcessInPlace):
///     [0x00] u32: data_section_offset
///     [0x04] u32: hierarchy_array_offset
///     [0x08] u32: reserved
///     [0x0C] u32: hierarchy_array_count
///     At data_section_offset: u32 root_node_offset, then DMA/node data.
/// </summary>
public static class Ps2GeomFile
{
    private const float PositionScale = 1f / 16f; // SUB_INCH_PRECISION = 16.0
    private const float UvScale = 1f / 4096f;

    // CGeomNode flags
    private const uint NodeFlagLeaf = 1 << 1;

    public static Ps2GeomScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static Ps2GeomScene Parse(byte[] data)
    {
        if (data.Length < 20)
            throw new InvalidDataException($"File too small: {data.Length} bytes");

        var dataSectionOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
        if (dataSectionOffset < 0 || dataSectionOffset >= data.Length)
            throw new InvalidDataException($"Invalid data section offset: 0x{dataSectionOffset:X}");

        var baseOffset = dataSectionOffset;
        var rootNodeOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(baseOffset));

        if (rootNodeOffset < 0 || baseOffset + rootNodeOffset + 80 > data.Length)
            throw new InvalidDataException($"Invalid root node offset: 0x{rootNodeOffset:X}");

        var leaves = new List<Ps2GeomLeaf>();
        WalkNodeTree(data, baseOffset, rootNodeOffset, leaves);

        return new Ps2GeomScene { Leaves = leaves };
    }

    private static void WalkNodeTree(byte[] data, int baseOffset, int rootNodeOffset,
        List<Ps2GeomLeaf> leaves)
    {
        // Iterative tree walk using explicit stack
        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(rootNodeOffset);

        while (stack.Count > 0)
        {
            var nodeOffset = stack.Pop();

            if (nodeOffset == -1 || !visited.Add(nodeOffset))
                continue;

            var abs = baseOffset + nodeOffset;
            if (abs + 80 > data.Length)
                continue;

            var span = data.AsSpan(abs);

            // CGeomNode layout (80 bytes):
            // +0x00: bounding_sphere (4×f32)
            // +0x1C: flags (u32)
            // +0x20: u1 child/dma (s32)
            // +0x24: u2 transform/dma_tag (s32)
            // +0x28: sibling (s32)
            // +0x2C: group_checksum (u32)
            // +0x30: checksum (u32)
            // +0x3C: colour (u32)
            // +0x44: texture_checksum (u32)
            // +0x4C: next_lod (s32)

            var sphereX = BinaryPrimitives.ReadSingleLittleEndian(span);
            var sphereY = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
            var sphereZ = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
            var sphereR = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]);
            var u1 = BinaryPrimitives.ReadInt32LittleEndian(span[0x20..]);
            var sibling = BinaryPrimitives.ReadInt32LittleEndian(span[0x28..]);
            var groupCk = BinaryPrimitives.ReadUInt32LittleEndian(span[0x2C..]);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x30..]);
            var colour = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);
            var textureCk = BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..]);
            var nextLod = BinaryPrimitives.ReadInt32LittleEndian(span[0x4C..]);

            var isLeaf = (flags & NodeFlagLeaf) != 0;

            if (isLeaf && u1 != -1)
            {
                var dmaAbs = baseOffset + u1;
                if (dmaAbs + 8 <= data.Length)
                {
                    var center = new Vector3(sphereX, sphereY, sphereZ);
                    var vertices = ExtractVerticesFromDma(data, dmaAbs, center);
                    var gsCtx = ExtractGsContext(data, dmaAbs);

                    if (vertices.Length > 0)
                    {
                        leaves.Add(new Ps2GeomLeaf
                        {
                            Checksum = checksum,
                            TextureChecksum = textureCk,
                            GroupChecksum = groupCk,
                            Colour = colour,
                            BoundingSphere = new Vector4(sphereX, sphereY, sphereZ, sphereR),
                            Vertices = vertices,
                            DmaTex0 = gsCtx.Tex0,
                            DmaClamp1 = gsCtx.Clamp1,
                            DmaAlpha1 = gsCtx.Alpha1,
                            DmaTest1 = gsCtx.Test1
                        });
                    }
                }
            }

            // Push traversal targets (reverse order for correct processing)
            if (nextLod != -1) stack.Push(nextLod);
            if (sibling != -1) stack.Push(sibling);
            if (!isLeaf && u1 != -1) stack.Push(u1);
        }
    }

    /// <summary>
    ///     Extract vertex data from a DMA/VIF chain at the given absolute offset.
    ///     Scans for STMOD(1) + UNPACK patterns to find position data,
    ///     and identifies UV/color/normal UNPACKs by their format code.
    /// </summary>
    private static Ps2Vertex[] ExtractVerticesFromDma(byte[] data, int dmaOffset, Vector3 center)
    {
        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dmaOffset));
        var pStart = dmaOffset + 8;
        var pEnd = dmaOffset + 16 + (qwc << 4);

        if (pEnd > data.Length)
            return [];

        // First pass: collect all vertex batches
        // Each batch is a set of UNPACKs between MSCAL boundaries.
        // Within each batch, STMOD(1) marks the position UNPACK.
        var batches = new List<VifBatch>();
        var currentBatch = new VifBatch();
        var stmodActive = false;

        var pCode = pStart;
        while (pCode < pEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];

            // Check for STMOD
            if ((cmd & 0x7F) == 0x05)
            {
                stmodActive = data[pCode] == 1;
                pCode = VifNextCode(data, pCode, pEnd);
                continue;
            }

            // Check for MSCAL (VU1 kick) — ends current batch
            if ((cmd & 0x7F) == 0x14)
            {
                if (currentBatch.PositionOffset >= 0)
                {
                    batches.Add(currentBatch);
                }

                currentBatch = new VifBatch();
                stmodActive = false;
                pCode = VifNextCode(data, pCode, pEnd);
                continue;
            }

            // Check for UNPACK
            if ((cmd & 0x60) == 0x60)
            {
                var vn = (cmd >> 2) & 3; // dimension: 0=S, 1=V2, 2=V3, 3=V4
                var vl = cmd & 3; // bitwidth: 0=32, 1=16, 2=8
                var num = data[pCode + 2];
                if (num == 0) num = 0; // 0 means 256 for PS2 but unusual in practice

                var unpackDataOffset = pCode + 4;

                if (stmodActive && (cmd & 0x7E) == 0x6C) // V4_16 or V4_32
                {
                    // Position data
                    currentBatch.PositionOffset = unpackDataOffset;
                    currentBatch.PositionCount = num;
                    currentBatch.PositionIs16Bit = (cmd & 0x01) != 0;
                    stmodActive = false;
                }
                else if (num > 1) // Skip context/GIF tags (NUM=1)
                {
                    if (vn == 1) // V2 = UV coordinates
                    {
                        currentBatch.UvOffset = unpackDataOffset;
                        currentBatch.UvCount = num;
                        currentBatch.UvIs16Bit = vl == 1;
                        currentBatch.UvIs32Bit = vl == 0;
                    }
                    else if (vn == 2 && vl == 1) // V3_16 = normals
                    {
                        currentBatch.NormalOffset = unpackDataOffset;
                        currentBatch.NormalCount = num;
                    }
                    else if (vn == 3 && vl == 2) // V4_8 = colors
                    {
                        currentBatch.ColorOffset = unpackDataOffset;
                        currentBatch.ColorCount = num;
                    }
                }
            }

            pCode = VifNextCode(data, pCode, pEnd);
        }

        // Add last batch if it has positions
        if (currentBatch.PositionOffset >= 0)
            batches.Add(currentBatch);

        if (batches.Count == 0)
            return [];

        // Second pass: extract vertex data from batches
        var allVertices = new List<Ps2Vertex>();

        foreach (var batch in batches)
        {
            var count = batch.PositionCount;
            if (count == 0) continue;

            for (var i = 0; i < count; i++)
            {
                // Position (always present)
                float px, py, pz;
                bool isStripRestart;

                if (batch.PositionIs16Bit)
                {
                    var off = batch.PositionOffset + i * 8; // 4×s16 = 8 bytes
                    if (off + 8 > data.Length) break;
                    var sx = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off));
                    var sy = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2));
                    var sz = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 4));
                    var sw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 6));
                    px = sx * PositionScale + center.X;
                    py = sy * PositionScale + center.Y;
                    pz = sz * PositionScale + center.Z;
                    isStripRestart = (sw & 0x8000) != 0;
                }
                else
                {
                    var off = batch.PositionOffset + i * 16; // 4×s32 = 16 bytes
                    if (off + 16 > data.Length) break;
                    var sx = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
                    var sy = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                    var sz = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 8));
                    var sw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12));
                    px = sx * PositionScale + center.X;
                    py = sy * PositionScale + center.Y;
                    pz = sz * PositionScale + center.Z;
                    isStripRestart = (sw & 0x8000) != 0;
                }

                // UV coordinates
                float u = 0, v = 0;
                var hasUv = false;
                if (batch.UvOffset >= 0 && i < batch.UvCount)
                {
                    if (batch.UvIs16Bit)
                    {
                        var off = batch.UvOffset + i * 4; // 2×s16 = 4 bytes
                        if (off + 4 <= data.Length)
                        {
                            u = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off)) * UvScale;
                            v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2)) * UvScale;
                            hasUv = true;
                        }
                    }
                    else if (batch.UvIs32Bit)
                    {
                        // GEOM V2_32 UVs are sint32 fixed-point scaled by 4096, not IEEE float.
                        // Built via immediate.cpp/dma.h pipeline (ConvertSTToFloat divides by 4096).
                        // Distinct from MDL/SKIN which writes actual IEEE floats via VertexSTFloat.
                        var off = batch.UvOffset + i * 8; // 2×s32 = 8 bytes
                        if (off + 8 <= data.Length)
                        {
                            u = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off)) * UvScale;
                            v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4)) * UvScale;
                            hasUv = true;
                        }
                    }
                }

                // Vertex colors
                byte cr = 128, cg = 128, cb = 128, ca = 128;
                var hasColor = false;
                if (batch.ColorOffset >= 0 && i < batch.ColorCount)
                {
                    var off = batch.ColorOffset + i * 4; // 4×u8 = 4 bytes
                    if (off + 4 <= data.Length)
                    {
                        cr = data[off];
                        cg = data[off + 1];
                        cb = data[off + 2];
                        ca = data[off + 3];
                        hasColor = true;
                    }
                }

                // Normals (packed 2-component V3_16)
                var normal = Vector3.UnitY;
                var hasNormal = false;
                if (batch.NormalOffset >= 0 && i < batch.NormalCount)
                {
                    var off = batch.NormalOffset + i * 6; // 3×s16 = 6 bytes
                    if (off + 6 <= data.Length)
                    {
                        var nx = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off));
                        var ny = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2));
                        var nz = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 4));
                        normal = new Vector3(
                            nx / 32767f,
                            ny / 32767f,
                            nz / 32767f);
                        var len = normal.Length();
                        if (len > 0.001f)
                            normal /= len;
                        else
                            normal = Vector3.UnitY;
                        hasNormal = true;
                    }
                }

                allVertices.Add(new Ps2Vertex(
                    new Vector3(px, py, pz),
                    normal,
                    cr, cg, cb, ca,
                    u, v,
                    hasNormal, hasColor, hasUv,
                    isStripRestart));
            }
        }

        return allVertices.ToArray();
    }

    /// <summary>
    ///     Advance past one VIF opcode. Port of vif::NextCode from vif.cpp.
    /// </summary>
    private static int VifNextCode(byte[] data, int offset, int end)
    {
        if (offset >= end || offset + 4 > data.Length)
            return end;

        var cmd = data[offset + 3];

        if ((cmd & 0x60) != 0x60)
        {
            // Non-UNPACK commands
            return (cmd & 0x7F) switch
            {
                // 4-byte commands
                0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                    or 0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x17 => offset + 4,
                0x20 => offset + 8, // STMASK
                0x30 or 0x31 => offset + 20, // STROW/STCOL
                0x4A => offset + (data[offset + 2] << 3) + 4, // MPG
                0x50 or 0x51 => // DIRECT/DIRECTHL
                    offset + (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) << 4) + 4,
                _ => end // Unknown — bail
            };
        }

        // UNPACK
        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[offset + 2];
        if (num == 0) num = 0; // Handle edge case; 0 in practice means no data
        var dimension = vn + 1;
        var bitLength = 32 >> vl;
        var dataSize = ((bitLength * dimension * num + 31) >> 5) << 2;
        return offset + 4 + dataSize;
    }

    /// <summary>
    ///     Extract GS register values from a DMA chain's GS context.
    ///     The GS context is encoded as: UNPACK V4_32 NUM=1 (GIF tag) followed by
    ///     UNPACK V3_32 NUM=N (register writes as data_lo32, data_hi32, reg_addr triplets).
    ///     Extracts TEX0_1 (0x06), CLAMP_1 (0x08), ALPHA_1 (0x42), TEST_1 (0x47).
    /// </summary>
    private static GsContext ExtractGsContext(byte[] data, int dmaOffset)
    {
        var ctx = new GsContext();

        if (dmaOffset + 16 > data.Length)
            return ctx;

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dmaOffset));
        var pStart = dmaOffset + 16;
        var pEnd = dmaOffset + 16 + (qwc << 4);
        if (pEnd > data.Length)
            return ctx;

        var pCode = pStart;
        while (pCode < pEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var num = data[pCode + 2];

            // Look for UNPACK V4_32 NUM=1 (GIF tag)
            if ((cmd & 0x7F) == 0x6C && num == 1)
            {
                // Check next VIF code for UNPACK V3_32 (register writes)
                var nextP = pCode + 4 + 16; // after V4_32 header + 1 quadword
                if (nextP + 4 > data.Length)
                    break;

                var nextCmd = data[nextP + 3];
                var nextNum = data[nextP + 2];

                if ((nextCmd & 0x7F) == 0x68 && nextNum > 0) // V3_32
                {
                    var regDataStart = nextP + 4;
                    for (var i = 0; i < nextNum; i++)
                    {
                        var off = regDataStart + i * 12;
                        if (off + 12 > data.Length)
                            break;

                        var lo32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
                        var hi32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
                        var reg = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8));
                        var val = ((ulong)hi32 << 32) | lo32;

                        switch (reg)
                        {
                            case 0x06: ctx.Tex0 = val; break; // TEX0_1
                            case 0x08: ctx.Clamp1 = val; break; // CLAMP_1
                            case 0x42: ctx.Alpha1 = val; break; // ALPHA_1
                            case 0x47: ctx.Test1 = val; break; // TEST_1
                        }
                    }

                    // Found a GS context batch — use the first one
                    return ctx;
                }
            }

            pCode = VifNextCode(data, pCode, pEnd);
        }

        return ctx;
    }

    private struct GsContext
    {
        public ulong Tex0;
        public ulong Clamp1;
        public ulong Alpha1;
        public ulong Test1;
    }

    /// <summary>
    ///     Tracks UNPACK offsets and metadata for one vertex batch within a VIF chain.
    /// </summary>
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

        public VifBatch()
        {
            PositionOffset = -1;
            UvOffset = -1;
            NormalOffset = -1;
            ColorOffset = -1;
        }
    }
}
