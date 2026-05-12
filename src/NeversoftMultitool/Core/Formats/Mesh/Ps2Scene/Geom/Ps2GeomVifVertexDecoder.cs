using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

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

    /// <summary>
    ///     Per-batch result for the level-MDL path. Each batch corresponds to one MSCAL kick
    ///     in the VIF stream; level-MDL leaves with 2+ MSCALs need to be emitted as separate
    ///     Ps2GeomLeaf objects so each batch can carry its own GS context / TEX0.
    /// </summary>
    internal readonly record struct Ps2GeomBatch(Ps2Vertex[] Vertices, int VifStart, int VifEnd);

    /// <summary>
    ///     Walk the VIF stream and emit one <see cref="Ps2GeomBatch"/> per MSCAL boundary,
    ///     preserving the per-batch VIF byte range so callers can extract a fresh GS context
    ///     per batch.
    /// </summary>
    internal static List<Ps2GeomBatch> ExtractBatchesFromVif(
        byte[] data, int pStart, int pEnd, Vector3 center)
    {
        var decoded = new List<Ps2GeomBatch>();
        var batches = ParseBatches(data, pStart, pEnd);
        foreach (var (batch, batchStart, batchEnd) in batches)
        {
            if (batch.PositionCount == 0)
                continue;
            var verts = BuildVertices(data, batch, center, forceFirstRestart: false);
            if (verts.Length > 0)
                decoded.Add(new Ps2GeomBatch(verts, batchStart, batchEnd));
        }
        return decoded;
    }

    /// <summary>
    ///     Detect and decode a level-MDL billboard leaf (Format B). These leaves carry 4 V4_32
    ///     float quadwords without STMOD; they're not real vertices but parametric billboard
    ///     descriptors consumed by a VU1 microprogram (vu1code.dsm: ScreenAlignedBillboards,
    ///     LongAxisBillboards, ShortAxisBillboards). Layout:
    ///     <c>[0] = pvw (anchor)</c>, <c>[1] = (width, height, _, _)</c>,
    ///     <c>[2] = pvl (pivot-local offset)</c>, <c>[3] = axis (zero for screen-aligned)</c>.
    ///     We approximate each as a 4-vertex quad (axis-rotated for axis-aligned, XY-plane
    ///     for screen-aligned) centred at <c>anchor</c>; the Blender importer applies a
    ///     Track-To constraint to make screen/axis billboards actually face the camera.
    ///     Returns null if the leaf doesn't match the Format B signature.
    /// </summary>
    internal static (Ps2Vertex[] Vertices, int VifStart, int VifEnd, Ps2BillboardDescriptor Descriptor)? ExtractBillboardFromVif(
        byte[] data, int pStart, int pEnd)
    {
        // Walk the stream looking for: a V4_32 UNPACK with num==4 (positions), NOT gated by
        // STMOD(1), followed by MSCAL. Capture the address of the position UNPACK data.
        var pCode = pStart;
        var stmodOn = false;
        var positionDataOffset = -1;
        var mscalSeen = false;
        while (pCode < pEnd && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];
            var cmd7 = cmd & 0x7F;

            if (cmd7 == 0x05)
            {
                var imm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pCode));
                stmodOn = (imm & 3) == 1;
                pCode = VifNextCode(data, pCode, pEnd);
                continue;
            }

            if (cmd7 == 0x14)
            {
                mscalSeen = true;
                break;
            }

            if ((cmd & 0x60) == 0x60)
            {
                var num = data[pCode + 2];
                // V4_32 (cmd byte 0x6C) with num == 4, no STMOD gating = Format B positions.
                if (cmd == 0x6C && num == 4 && !stmodOn)
                {
                    positionDataOffset = pCode + 4;
                }
            }

            pCode = VifNextCode(data, pCode, pEnd);
        }

        if (!mscalSeen || positionDataOffset < 0 || positionDataOffset + 64 > data.Length)
            return null;

        // Read all four V4_32 quadwords: anchor, size, pvl, axis.
        var anchorX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset));
        var anchorY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 4));
        var anchorZ = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 8));
        var width = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 16));
        var height = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 20));
        var pvlX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 32));
        var pvlY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 36));
        var pvlZ = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 40));
        var axisX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 48));
        var axisY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 52));
        var axisZ = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(positionDataOffset + 56));

        if (!float.IsFinite(anchorX) || !float.IsFinite(anchorY) || !float.IsFinite(anchorZ)
            || !float.IsFinite(width) || !float.IsFinite(height)
            || !float.IsFinite(pvlX) || !float.IsFinite(pvlY) || !float.IsFinite(pvlZ)
            || !float.IsFinite(axisX) || !float.IsFinite(axisY) || !float.IsFinite(axisZ)
            || width <= 0f || height <= 0f || width > 10000f || height > 10000f)
        {
            return null;
        }

        var anchor = new Vector3(anchorX, anchorY, anchorZ);
        var size = new Vector2(width, height);
        var pvl = new Vector3(pvlX, pvlY, pvlZ);
        var axis = new Vector3(axisX, axisY, axisZ);

        // P1 diagnostic on z_sm (tools/diagnostics/thaw_billboard_classify.py) found 145/145
        // axis-aligned with axis = (0, 1, 0) — every Format-B leaf in the corpus rotates around
        // world Y. The screen-aligned case isn't observed but the kind is preserved so a future
        // dataset that uses it can be handled without a decoder change.
        var axisLen2 = axis.LengthSquared();
        var kind = axisLen2 < 1e-6f
            ? Ps2BillboardKind.ScreenAligned
            : Ps2BillboardKind.LongAxis;
        var descriptor = new Ps2BillboardDescriptor(anchor, size, pvl, axis, kind);

        // pvl ("pivot-local") is the offset from the world anchor to the rendered quad
        // centre, expressed in the (udir, vdir, wdir) basis built per-frame by the VU1
        // billboard microprogram. For axis-aligned variants wdir = axis, so the
        // axis-aligned component (pvl.z) is the only one we can statically bake; udir
        // and vdir are camera-dependent and only resolved at render time. Observed on
        // z_sm: lamp-post light flares have anchor at the lamp head and positive pvl.z
        // along axis = +Y up, so the quad must sit ABOVE the anchor to land inside the
        // glass housing rather than below it.
        var pivotCenter = anchor;
        if (kind == Ps2BillboardKind.LongAxis && axisLen2 > 1e-6f)
        {
            var axisNorm = Vector3.Normalize(axis);
            pivotCenter += axisNorm * pvl.Z;
        }

        var hw = size.X * 0.5f;
        var hh = size.Y * 0.5f;
        // Axis-aligned XY quad centred on the pivot. The Blender importer rotates this around
        // the leaf's axis vector via a Track-To constraint at scene load; the static .glb keeps
        // this orientation (acceptable for street-level cameras since every observed billboard
        // has axis = world Y so the quad's local up is already correct).
        var verts = new[]
        {
            MakeBillboardVertex(pivotCenter + new Vector3(-hw, -hh, 0), 0f, 0f, isStripRestart: true),
            MakeBillboardVertex(pivotCenter + new Vector3( hw, -hh, 0), 1f, 0f, isStripRestart: true),
            MakeBillboardVertex(pivotCenter + new Vector3(-hw,  hh, 0), 0f, 1f, isStripRestart: false),
            MakeBillboardVertex(pivotCenter + new Vector3( hw,  hh, 0), 1f, 1f, isStripRestart: false),
        };
        return (verts, pStart, pEnd, descriptor);
    }

    private static Ps2Vertex MakeBillboardVertex(Vector3 position, float u, float v, bool isStripRestart)
    {
        return new Ps2Vertex(
            position,
            Vector3.UnitZ,
            128, 128, 128, 128,
            u, v,
            hasNormal: false, hasColor: false, hasUV: true,
            isStripRestart: isStripRestart);
    }

    internal static Ps2Vertex[] ExtractVerticesFromVif(byte[] data, int pStart, int pEnd, Vector3 center)
    {
        var batches = ParseBatches(data, pStart, pEnd);
        if (batches.Count == 0)
            return [];

        var allVertices = new List<Ps2Vertex>();
        var firstBatchEmitted = false;
        foreach (var (batch, _, _) in batches)
        {
            if (batch.PositionCount == 0)
                continue;

            // Force strip restart on the first vertex of every batch after the first so
            // we never form a phantom triangle that bridges two MSCAL batches.
            var verts = BuildVertices(data, batch, center, forceFirstRestart: firstBatchEmitted);
            if (verts.Length == 0)
                continue;

            allVertices.AddRange(verts);
            firstBatchEmitted = true;
        }

        return allVertices.ToArray();
    }

    /// <summary>
    ///     Parse the VIF stream into a list of batches split by MSCAL. Each batch entry
    ///     records its VIF byte range (start/end, with end pointing just past the MSCAL
    ///     that closed it) so callers can scan for a per-batch GS context.
    /// </summary>
    private static List<(VifBatch Batch, int BatchStart, int BatchEnd)> ParseBatches(
        byte[] data, int pStart, int pEnd)
    {
        var batches = new List<(VifBatch, int, int)>();
        var currentBatch = new VifBatch();
        var stmodActive = false;
        var currentBatchStart = pStart;
        // Level-MDL sub-chunks share one VU1 setup and emit position-only UNPACKs
        // between MSCALs. Once we've seen ONE position UNPACK (cmd byte X), later
        // UNPACKs with the same cmd byte at the START of a new sub-chunk are also
        // positions — even without a fresh STMOD. Tracking the cmd byte keeps us
        // safe for car MDLs where positions are always V4_32 and subsequent
        // UNPACKs of different cmd bytes (colors/UVs/normals) still fall through.
        byte lastPositionUnpackCmd = 0;

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
                var batchEnd = VifNextCode(data, pCode, pEnd);
                if (currentBatch.PositionOffset >= 0)
                    batches.Add((currentBatch, currentBatchStart, batchEnd));

                currentBatch = new VifBatch();
                stmodActive = false;
                currentBatchStart = batchEnd;
                pCode = batchEnd;
                continue;
            }

            if ((cmd & 0x60) == 0x60)
            {
                var vn = (cmd >> 2) & 3;
                var vl = cmd & 3;
                var num = data[pCode + 2];
                var unpackAddress = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pCode)) & 0x03FF;
                var unpackDataOffset = pCode + 4;

                var matchesLastPositionCmd =
                    lastPositionUnpackCmd != 0 &&
                    (cmd & 0x7E) == 0x6C &&
                    currentBatch.PositionOffset < 0 &&
                    currentBatch.ColorOffset < 0 &&
                    currentBatch.UvOffset < 0 &&
                    currentBatch.NormalOffset < 0 &&
                    num > 1;

                if (stmodActive && (cmd & 0x7E) == 0x6C)
                {
                    currentBatch.PositionOffset = unpackDataOffset;
                    currentBatch.PositionCount = num;
                    currentBatch.PositionIs16Bit = (cmd & 0x01) != 0;
                    lastPositionUnpackCmd = cmd;
                    stmodActive = false;
                }
                else if (matchesLastPositionCmd)
                {
                    currentBatch.PositionOffset = unpackDataOffset;
                    currentBatch.PositionCount = num;
                    currentBatch.PositionIs16Bit = (cmd & 0x01) != 0;
                }
                else if (num > 1)
                {
                    if (vn == 1)
                    {
                        currentBatch.UvOffset = unpackDataOffset;
                        currentBatch.UvCount = num;
                        currentBatch.UvIs16Bit = vl == 1;
                        currentBatch.UvIs32Bit = vl == 0;
                        currentBatch.UvComponentCount = 2;
                    }
                    else if (vn == 2 && vl == 1 && unpackAddress == 0x007 && currentBatch.UvOffset < 0)
                    {
                        // Some THAW worldzone batches store texture coordinates as V3_16
                        // at VU address 0x007: S, T, and Q/unused. Treat the first two
                        // components as UVs. Without this, Santa Monica boardwalk/shadow
                        // leaves render with no UVs and smear a single texel across the mesh.
                        currentBatch.UvOffset = unpackDataOffset;
                        currentBatch.UvCount = num;
                        currentBatch.UvIs16Bit = true;
                        currentBatch.UvIs32Bit = false;
                        currentBatch.UvComponentCount = 3;
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
            batches.Add((currentBatch, currentBatchStart, pEnd));

        return batches;
    }

    /// <summary>
    ///     Read one batch's vertex attributes into <see cref="Ps2Vertex"/> records. Positions
    ///     outside a sane envelope (±200k units) are treated as a sign of misidentified UNPACKs
    ///     and drop the whole batch.
    /// </summary>
    private static Ps2Vertex[] BuildVertices(byte[] data, VifBatch batch, Vector3 center, bool forceFirstRestart)
    {
        if (batch.PositionCount == 0)
            return [];

        // World z_bh fits in ±20,000 units; ±200k gives 10x headroom for outlier
        // billboards while still rejecting wrong-attribute decodes (UV sint16 / 16
        // peaked at ~2k, packed normal sint16 / 16 ~2k — both well inside ±200k, so
        // we're depending on positional sanity AFTER the GIF-tag prologue skip lands
        // the right UNPACK). The original ±1M envelope was sized to mask the
        // wrong-attribute bug.
        const float SanityLimit = 200_000f;
        for (var i = 0; i < batch.PositionCount; i++)
        {
            var preview = ReadPosition(data, batch, i, center);
            if (!preview.HasValue)
                return [];
            var (px0, py0, pz0, _) = preview.Value;
            if (!float.IsFinite(px0) || !float.IsFinite(py0) || !float.IsFinite(pz0) ||
                Math.Abs(px0) > SanityLimit || Math.Abs(py0) > SanityLimit || Math.Abs(pz0) > SanityLimit)
            {
                return [];
            }
        }

        var verts = new List<Ps2Vertex>(batch.PositionCount);
        for (var i = 0; i < batch.PositionCount; i++)
        {
            var position = ReadPosition(data, batch, i, center);
            if (!position.HasValue)
                break;

            var (px, py, pz, isStripRestart) = position.Value;
            var (u, v, hasUv) = ReadUv(data, batch, i);
            var (cr, cg, cb, ca, hasColor) = ReadColor(data, batch, i);
            var (normal, hasNormal) = ReadNormal(data, batch, i);

            if (i == 0 && forceFirstRestart)
                isStripRestart = true;

            verts.Add(new Ps2Vertex(
                new Vector3(px, py, pz), normal,
                cr, cg, cb, ca, u, v,
                hasNormal, hasColor, hasUv, isStripRestart));
        }

        return verts.ToArray();
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
                        case 0x3B: ctx.Texa = value; break;
                        case 0x4C: ctx.Frame1 = value; break;
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
            var componentCount = Math.Max(batch.UvComponentCount, 2);
            var off = batch.UvOffset + index * componentCount * 2;
            if (off + 4 > data.Length)
                return (0, 0, false);

            return (
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off)) * UvScale,
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 2)) * UvScale,
                true);
        }

        if (!batch.UvIs32Bit)
            return (0, 0, false);

        var componentCount32 = Math.Max(batch.UvComponentCount, 2);
        var off32 = batch.UvOffset + index * componentCount32 * 4;
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
        public int UvComponentCount;
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
