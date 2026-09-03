using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     Parses Neversoft collision (.col) files for THPS4/THUG/THUG2/THAW.
///     Binary format: file header (32B) + per-object headers (64B each)
///     + vertex data (fixed 6B or float 12B) + intensity data (1B per vert) + face data.
///     THPS4 version 8 instead stores every float vertex in 16 bytes, with four
///     RGBA intensity bytes inline, and uses 12-byte large faces.
///     X360 version 10 mirrors the later layout in big-endian byte order.
///     Vertices: fixed-point 3×u16 (×0.0625 + bbox_min) or float 3×f32.
///     Faces: small (u16 flags + u16 terrain + 3×u8 indices + pad) or large (+ 3×u16 indices).
///     Reference: io_thps_scene import_thps4.py/import_thug2.py (denetii/io_thps_scene).
/// </summary>
public static class ColFile
{
    private const int SizeofHeader = 32; // 8 × i32
    private const int SizeofObject = 64; // per-object header
    private const int SizeofFloatVert = 12; // 3 × f32
    private const int SizeofFixedVert = 6; // 3 × u16
    private const int SizeofSmallFace = 8; // flags:u16 + terrain:u16 + 3×u8 + pad:u8
    private const int SizeofLargeFace = 10; // flags:u16 + terrain:u16 + 3×u16
    private const int SizeofThps4FloatVert = 16; // 3 × f32 + RGBA intensity
    private const int SizeofThps4LargeFace = 12; // v8 adds a trailing u16 pad
    private const int SizeofThps4BspNode = 16; // axis:u32 + split:f32 + 2 × child-offset:u32
    private const int SizeofThps4BspLeaf = 20; // marker/count + bounds + sentinels + face-list offset

    /// <summary>
    ///     Returns true for a supported little-endian version 8/9/10 header or
    ///     the big-endian X360 version 10 header.
    /// </summary>
    public static bool IsColFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeofHeader) return false;
        var version = BinaryPrimitives.ReadInt32LittleEndian(data);
        return version is 8 or 9 or 10 ||
               BinaryPrimitives.ReadInt32BigEndian(data) == 10;
    }

    public static ColScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static ColScene Parse(byte[] data)
    {
        return Parse(data.AsSpan());
    }

    public static ColScene Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeofHeader)
            throw new InvalidDataException("File too small for COL header");

        // ── File header ──
        var version = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (version is not (8 or 9 or 10))
        {
            var bigEndianVersion = BinaryPrimitives.ReadInt32BigEndian(data);
            if (bigEndianVersion == 10)
                return ParseXen(data);

            throw new InvalidDataException(
                $"Unsupported COL version: little-endian {version}, big-endian {bigEndianVersion}");
        }

        var numObjects = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var totalVerts = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var totalLargeFaces = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var totalSmallFaces = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        var totalLargeVerts = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        var totalSmallVerts = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);

        if (numObjects < 0 || numObjects > 100_000)
            throw new InvalidDataException($"Unreasonable object count: {numObjects}");

        EnsureNonNegative(totalVerts, "total vertex");
        EnsureNonNegative(totalLargeFaces, "total large-face");
        EnsureNonNegative(totalSmallFaces, "total small-face");
        EnsureNonNegative(totalLargeVerts, "total large-vertex");
        EnsureNonNegative(totalSmallVerts, "total small-vertex");

        if (version == 8)
        {
            return ParseThps4(
                data,
                numObjects,
                totalVerts,
                totalLargeFaces,
                totalSmallFaces,
                totalLargeVerts,
                totalSmallVerts);
        }

        // THAW-generation files insert a 48-byte supersector block between the
        // file header and the object headers: marker(u32=0) + unknown(u32) +
        // rows(u32) + cols(u32) + sceneBBoxMin(4xf32) + sceneBBoxMax(4xf32).
        // THUG2-generation files start object headers (non-zero checksum)
        // immediately. Reference: NxTools fmt_thcol_import.py.
        var objectBase = SizeofHeader;
        if (data.Length >= SizeofHeader + 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(data[SizeofHeader..]) == 0)
        {
            objectBase += 48;
        }

        // ── Offset calculations ──
        var baseVertOffset = Align16(objectBase + SizeofObject * numObjects);
        var baseIntensityOffset = baseVertOffset +
                                  totalLargeVerts * SizeofFloatVert +
                                  totalSmallVerts * SizeofFixedVert;
        var baseFaceOffset = Align4(baseIntensityOffset + totalVerts);

        // ── Parse per-object headers + geometry ──
        var objects = new ColObject[numObjects];
        var headerOffset = objectBase;

        for (var i = 0; i < numObjects; i++)
        {
            objects[i] = ParseObject(data, headerOffset, baseVertOffset, baseIntensityOffset, baseFaceOffset);
            headerOffset += SizeofObject;
        }

        return new ColScene
        {
            Version = version,
            Objects = objects
        };
    }

    private static ColObject ParseObject(
        ReadOnlySpan<byte> data, int hdr,
        int baseVertOffset, int baseIntensityOffset, int baseFaceOffset)
    {
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[hdr..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 4)..]);
        var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 6)..]);
        var numFaces = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 8)..]);
        var useSmallFaces = data[hdr + 10] != 0;
        var useFixed = data[hdr + 11] != 0;
        var firstFaceOffset = BinaryPrimitives.ReadInt32LittleEndian(data[(hdr + 12)..]);

        var bboxMin = new Vector3(
            BitConverter.ToSingle(data[(hdr + 16)..]),
            BitConverter.ToSingle(data[(hdr + 20)..]),
            BitConverter.ToSingle(data[(hdr + 24)..])
        );
        var bboxMax = new Vector3(
            BitConverter.ToSingle(data[(hdr + 32)..]),
            BitConverter.ToSingle(data[(hdr + 36)..]),
            BitConverter.ToSingle(data[(hdr + 40)..])
        );

        var firstVertOffset = BinaryPrimitives.ReadInt32LittleEndian(data[(hdr + 48)..]);
        var intensityOffset = BinaryPrimitives.ReadInt32LittleEndian(data[(hdr + 56)..]);

        // ── Vertices ──
        var vertices = new Vector3[numVerts];
        var vertexStride = useFixed ? SizeofFixedVert : SizeofFloatVert;
        var absVertOffset = GetRequiredRegionOffset(
            data.Length,
            baseVertOffset,
            firstVertOffset,
            numVerts,
            vertexStride,
            "vertex");

        if (useFixed)
        {
            for (var v = 0; v < numVerts; v++)
            {
                var off = absVertOffset + v * SizeofFixedVert;
                var rx = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
                var ry = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
                var rz = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
                vertices[v] = new Vector3(
                    rx * 0.0625f + bboxMin.X,
                    ry * 0.0625f + bboxMin.Y,
                    rz * 0.0625f + bboxMin.Z
                );
            }
        }
        else
        {
            for (var v = 0; v < numVerts; v++)
            {
                var off = absVertOffset + v * SizeofFloatVert;
                vertices[v] = new Vector3(
                    BitConverter.ToSingle(data[off..]),
                    BitConverter.ToSingle(data[(off + 4)..]),
                    BitConverter.ToSingle(data[(off + 8)..])
                );
            }
        }

        // ── Intensities ──
        var intensities = new byte[numVerts];
        if (intensityOffset >= 0)
        {
            var absIntensityOffset = baseIntensityOffset + intensityOffset;
            for (var v = 0; v < numVerts && absIntensityOffset + v < data.Length; v++)
                intensities[v] = data[absIntensityOffset + v];
        }

        // ── Faces ──
        var faces = new ColFace[numFaces];
        var faceStride = useSmallFaces ? SizeofSmallFace : SizeofLargeFace;
        var absFaceOffset = GetRequiredRegionOffset(
            data.Length,
            baseFaceOffset,
            firstFaceOffset,
            numFaces,
            faceStride,
            "face");

        if (useSmallFaces)
        {
            for (var f = 0; f < numFaces; f++)
            {
                var off = absFaceOffset + f * SizeofSmallFace;
                var faceFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
                var terrain = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
                faces[f] = new ColFace(faceFlags, terrain, data[off + 4], data[off + 5], data[off + 6]);
            }
        }
        else
        {
            for (var f = 0; f < numFaces; f++)
            {
                var off = absFaceOffset + f * SizeofLargeFace;
                var faceFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
                var terrain = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
                var v0 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
                var v1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 6)..]);
                var v2 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 8)..]);
                faces[f] = new ColFace(faceFlags, terrain, v0, v1, v2);
            }
        }

        return new ColObject
        {
            Checksum = checksum,
            Flags = flags,
            BBoxMin = bboxMin,
            BBoxMax = bboxMax,
            Vertices = vertices,
            Faces = faces,
            Intensities = intensities
        };
    }

    private static ColScene ParseThps4(
        ReadOnlySpan<byte> data,
        int numObjects,
        int totalVerts,
        int totalLargeFaces,
        int totalSmallFaces,
        int totalLargeVerts,
        int totalSmallVerts)
    {
        var objectBytes = checked((long)numObjects * SizeofObject);
        EnsureAbsoluteRegion(data.Length, SizeofHeader, objectBytes, "object-header");

        var baseVertOffset = Align16((long)SizeofHeader + objectBytes);
        var vertexBytes = checked((long)totalVerts * SizeofThps4FloatVert);
        EnsureAbsoluteRegion(data.Length, baseVertOffset, vertexBytes, "vertex");

        var baseFaceOffset = checked(baseVertOffset + vertexBytes);
        var faceBytes = checked(
            (long)totalLargeFaces * SizeofThps4LargeFace +
            (long)totalSmallFaces * SizeofSmallFace);
        EnsureAbsoluteRegion(data.Length, baseFaceOffset, faceBytes, "face");

        // Retail THPS4 PS2 writes both aggregate vertex-kind counts as zero even
        // though every vertex is float. The reference exporter alternatively
        // writes the float count explicitly, so accept precisely those two known
        // encodings and reject profiles whose storage layout we cannot establish.
        if (!((totalLargeVerts == 0 && totalSmallVerts == 0) ||
              (totalLargeVerts == totalVerts && totalSmallVerts == 0)))
        {
            throw new InvalidDataException(
                "COL v8 has unsupported aggregate vertex-kind counts: " +
                $"large={totalLargeVerts}, small={totalSmallVerts}, total={totalVerts}");
        }

        var headers = new Thps4ObjectHeader[numObjects];
        var vertexRanges = new List<(long Start, long End)>(numObjects);
        var faceRanges = new List<(long Start, long End)>(numObjects);
        long declaredVerts = 0;
        long declaredLargeFaces = 0;
        long declaredSmallFaces = 0;

        for (var i = 0; i < numObjects; i++)
        {
            var hdr = SizeofHeader + i * SizeofObject;
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[hdr..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 4)..]);
            var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 6)..]);
            var numFaces = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 8)..]);
            var smallFaceByte = data[hdr + 10];
            var fixedVertByte = data[hdr + 11];
            if (smallFaceByte > 1)
                throw new InvalidDataException($"COL v8 object {i} has invalid small-face flag {smallFaceByte}");
            if (fixedVertByte > 1)
                throw new InvalidDataException($"COL v8 object {i} has invalid fixed-vertex flag {fixedVertByte}");
            if (fixedVertByte != 0)
            {
                throw new InvalidDataException(
                    $"COL v8 object {i} uses fixed vertices, whose v8 storage layout is unsupported");
            }

            var useSmallFaces = smallFaceByte != 0;
            var firstFaceOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(hdr + 12)..]);
            var bboxMin = ReadFiniteVector4(data, hdr + 16, $"object {i} bounding-box minimum");
            var bboxMax = ReadFiniteVector4(data, hdr + 32, $"object {i} bounding-box maximum");
            var firstVert = BinaryPrimitives.ReadUInt32LittleEndian(data[(hdr + 48)..]);
            var bspRootOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(hdr + 52)..]);

            var vertexEnd = checked((long)firstVert + numVerts);
            if (vertexEnd > totalVerts)
            {
                throw new InvalidDataException(
                    $"COL v8 object {i} vertex ordinals [{firstVert}, {vertexEnd}) " +
                    $"exceed aggregate vertex count {totalVerts}");
            }

            var faceStride = useSmallFaces ? SizeofSmallFace : SizeofThps4LargeFace;
            var faceEnd = checked((long)firstFaceOffset + (long)numFaces * faceStride);
            if (faceEnd > faceBytes)
            {
                throw new InvalidDataException(
                    $"COL v8 object {i} face byte range [{firstFaceOffset}, {faceEnd}) " +
                    $"exceeds aggregate face byte count {faceBytes}");
            }

            if (numVerts != 0)
                vertexRanges.Add((firstVert, vertexEnd));
            if (numFaces != 0)
                faceRanges.Add((firstFaceOffset, faceEnd));

            declaredVerts += numVerts;
            if (useSmallFaces)
                declaredSmallFaces += numFaces;
            else
                declaredLargeFaces += numFaces;

            headers[i] = new Thps4ObjectHeader(
                checksum,
                flags,
                numVerts,
                numFaces,
                useSmallFaces,
                firstVert,
                firstFaceOffset,
                bspRootOffset,
                new Vector3(bboxMin.X, bboxMin.Y, bboxMin.Z),
                new Vector3(bboxMax.X, bboxMax.Y, bboxMax.Z));
        }

        EnsureAggregateMatches(declaredVerts, totalVerts, "vertex");
        EnsureAggregateMatches(declaredLargeFaces, totalLargeFaces, "large-face");
        EnsureAggregateMatches(declaredSmallFaces, totalSmallFaces, "small-face");
        EnsurePartition(vertexRanges, totalVerts, "vertex ordinal");
        EnsurePartition(faceRanges, faceBytes, "face byte");
        ValidateThps4BspTail(data, headers, checked(baseFaceOffset + faceBytes));

        var objects = new ColObject[numObjects];
        for (var i = 0; i < headers.Length; i++)
            objects[i] = ParseThps4Object(data, headers[i], i, baseVertOffset, baseFaceOffset);

        return new ColScene
        {
            Version = 8,
            Objects = objects
        };
    }

    private static void ValidateThps4BspTail(
        ReadOnlySpan<byte> data,
        IReadOnlyList<Thps4ObjectHeader> headers,
        long tailOffset)
    {
        EnsureAbsoluteRegion(data.Length, tailOffset, sizeof(uint), "v8 BSP-size");
        var bspSizeOffset = checked((int)tailOffset);
        var nodeByteCount = BinaryPrimitives.ReadUInt32LittleEndian(data[bspSizeOffset..]);
        var nodeBaseOffset = checked(tailOffset + sizeof(uint));
        EnsureAbsoluteRegion(data.Length, nodeBaseOffset, nodeByteCount, "v8 BSP-node");

        var faceIndexBaseOffset = checked(nodeBaseOffset + nodeByteCount);
        var faceIndexBytes = data.Length - faceIndexBaseOffset;
        if ((faceIndexBytes & 1) != 0)
        {
            throw new InvalidDataException(
                $"COL v8 BSP face-index data has odd byte length {faceIndexBytes}");
        }

        var faceIndexCount = faceIndexBytes / sizeof(ushort);
        var visitedNodes = new HashSet<uint>();
        var nodeRanges = new List<(long Start, long End)>();
        var faceIndexRanges = new List<(long Start, long End, int ObjectIndex)>();

        for (var objectIndex = 0; objectIndex < headers.Count; objectIndex++)
        {
            var pending = new Stack<uint>();
            pending.Push(headers[objectIndex].BspRootOffset);

            while (pending.Count != 0)
            {
                var relativeOffset = pending.Pop();
                if ((relativeOffset & 3) != 0 || relativeOffset >= nodeByteCount)
                {
                    throw new InvalidDataException(
                        $"COL v8 object {objectIndex} BSP node offset {relativeOffset} " +
                        $"is outside the {nodeByteCount}-byte aligned node region");
                }

                if (!visitedNodes.Add(relativeOffset))
                {
                    throw new InvalidDataException(
                        $"COL v8 object {objectIndex} BSP node {relativeOffset} " +
                        "is cyclic or shared by multiple branches");
                }

                var absoluteOffset = checked((int)(nodeBaseOffset + relativeOffset));
                if (data[absoluteOffset] == byte.MaxValue)
                {
                    var end = checked((long)relativeOffset + SizeofThps4BspLeaf);
                    if (end > nodeByteCount)
                    {
                        throw new InvalidDataException(
                            $"COL v8 object {objectIndex} BSP leaf at {relativeOffset} is truncated");
                    }

                    var leafPad = data[absoluteOffset + 1];
                    if (leafPad != 0)
                    {
                        throw new InvalidDataException(
                            $"COL v8 object {objectIndex} BSP leaf at {relativeOffset} " +
                            $"has non-zero pad byte {leafPad}");
                    }

                    var faceCount = BinaryPrimitives.ReadUInt16LittleEndian(
                        data[(absoluteOffset + 2)..]);
                    var leftSentinel = BinaryPrimitives.ReadUInt32LittleEndian(
                        data[(absoluteOffset + 8)..]);
                    var rightSentinel = BinaryPrimitives.ReadUInt32LittleEndian(
                        data[(absoluteOffset + 12)..]);
                    if (leftSentinel != uint.MaxValue || rightSentinel != uint.MaxValue)
                    {
                        throw new InvalidDataException(
                            $"COL v8 object {objectIndex} BSP leaf at {relativeOffset} " +
                            "does not contain the leaf child sentinels");
                    }

                    var firstFaceIndex = BinaryPrimitives.ReadUInt32LittleEndian(
                        data[(absoluteOffset + 16)..]);
                    var faceEnd = checked((long)firstFaceIndex + faceCount);
                    if (faceEnd > faceIndexCount)
                    {
                        throw new InvalidDataException(
                            $"COL v8 object {objectIndex} BSP leaf face-index range " +
                            $"[{firstFaceIndex}, {faceEnd}) exceeds pool count {faceIndexCount}");
                    }

                    if (faceCount != 0)
                        faceIndexRanges.Add((firstFaceIndex, faceEnd, objectIndex));
                    nodeRanges.Add((relativeOffset, end));
                    continue;
                }

                var nodeEnd = checked((long)relativeOffset + SizeofThps4BspNode);
                if (nodeEnd > nodeByteCount)
                {
                    throw new InvalidDataException(
                        $"COL v8 object {objectIndex} BSP node at {relativeOffset} is truncated");
                }

                var axis = BinaryPrimitives.ReadUInt32LittleEndian(data[absoluteOffset..]);
                if (axis > 2)
                {
                    throw new InvalidDataException(
                        $"COL v8 object {objectIndex} BSP node at {relativeOffset} " +
                        $"has invalid split axis {axis}");
                }

                var split = BitConverter.ToSingle(data[(absoluteOffset + 4)..]);
                if (!float.IsFinite(split))
                {
                    throw new InvalidDataException(
                        $"COL v8 object {objectIndex} BSP node at {relativeOffset} " +
                        "has a non-finite split distance");
                }

                pending.Push(BinaryPrimitives.ReadUInt32LittleEndian(data[(absoluteOffset + 12)..]));
                pending.Push(BinaryPrimitives.ReadUInt32LittleEndian(data[(absoluteOffset + 8)..]));
                nodeRanges.Add((relativeOffset, nodeEnd));
            }
        }

        EnsurePartition(nodeRanges, nodeByteCount, "COL v8 BSP", "node byte");

        var referencedFacesByObject = new bool[headers.Count][];
        var referencedFaceCounts = new int[headers.Count];
        for (var objectIndex = 0; objectIndex < headers.Count; objectIndex++)
            referencedFacesByObject[objectIndex] = new bool[headers[objectIndex].NumFaces];

        faceIndexRanges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        long cursor = 0;
        foreach (var range in faceIndexRanges)
        {
            if (range.Start != cursor)
            {
                var problem = range.Start < cursor ? "overlaps" : "leaves a gap after";
                throw new InvalidDataException(
                    $"COL v8 BSP face-index range [{range.Start}, {range.End}) " +
                    $"{problem} offset {cursor}");
            }

            for (var faceIndex = range.Start; faceIndex < range.End; faceIndex++)
            {
                var absoluteOffset = checked((int)(faceIndexBaseOffset + faceIndex * sizeof(ushort)));
                var objectFaceIndex = BinaryPrimitives.ReadUInt16LittleEndian(data[absoluteOffset..]);
                if (objectFaceIndex >= headers[range.ObjectIndex].NumFaces)
                {
                    throw new InvalidDataException(
                        $"COL v8 object {range.ObjectIndex} BSP face index {objectFaceIndex} " +
                        $"is outside [0, {headers[range.ObjectIndex].NumFaces})");
                }

                if (!referencedFacesByObject[range.ObjectIndex][objectFaceIndex])
                {
                    referencedFacesByObject[range.ObjectIndex][objectFaceIndex] = true;
                    referencedFaceCounts[range.ObjectIndex]++;
                }
            }

            cursor = range.End;
        }

        if (cursor != faceIndexCount)
        {
            throw new InvalidDataException(
                $"COL v8 BSP face-index ranges end at {cursor}, expected {faceIndexCount}");
        }

        for (var objectIndex = 0; objectIndex < headers.Count; objectIndex++)
        {
            if (referencedFaceCounts[objectIndex] == headers[objectIndex].NumFaces)
                continue;

            var referencedFaces = referencedFacesByObject[objectIndex];
            var firstMissingFace = 0;
            while (referencedFaces[firstMissingFace])
                firstMissingFace++;

            throw new InvalidDataException(
                $"COL v8 object {objectIndex} BSP does not reference face {firstMissingFace}; " +
                $"{referencedFaceCounts[objectIndex]}/{headers[objectIndex].NumFaces} faces are reachable");
        }
    }

    private static ColObject ParseThps4Object(
        ReadOnlySpan<byte> data,
        Thps4ObjectHeader header,
        int objectIndex,
        long baseVertOffset,
        long baseFaceOffset)
    {
        var vertices = new Vector3[header.NumVerts];
        var intensities = new byte[header.NumVerts];
        var vertexColors = new byte[header.NumVerts * 4];
        var absVertOffset = checked(baseVertOffset + (long)header.FirstVert * SizeofThps4FloatVert);

        for (var v = 0; v < header.NumVerts; v++)
        {
            var off = checked((int)(absVertOffset + (long)v * SizeofThps4FloatVert));
            var vertex = new Vector3(
                BitConverter.ToSingle(data[off..]),
                BitConverter.ToSingle(data[(off + 4)..]),
                BitConverter.ToSingle(data[(off + 8)..]));
            if (!IsFinite(vertex))
                throw new InvalidDataException($"COL v8 object {objectIndex} vertex {v} is not finite");

            vertices[v] = vertex;
            data.Slice(off + 12, 4).CopyTo(vertexColors.AsSpan(v * 4, 4));
            intensities[v] = (byte)((data[off + 12] + data[off + 13] + data[off + 14]) / 3);
        }

        var faces = new ColFace[header.NumFaces];
        var faceStride = header.UseSmallFaces ? SizeofSmallFace : SizeofThps4LargeFace;
        var absFaceOffset = checked(baseFaceOffset + header.FirstFaceOffset);
        for (var f = 0; f < header.NumFaces; f++)
        {
            var off = checked((int)(absFaceOffset + (long)f * faceStride));
            var faceFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
            var terrain = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
            int v0;
            int v1;
            int v2;
            if (header.UseSmallFaces)
            {
                v0 = data[off + 4];
                v1 = data[off + 5];
                v2 = data[off + 6];
            }
            else
            {
                v0 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
                v1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 6)..]);
                v2 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 8)..]);
            }

            if ((uint)v0 >= header.NumVerts ||
                (uint)v1 >= header.NumVerts ||
                (uint)v2 >= header.NumVerts)
            {
                throw new InvalidDataException(
                    $"COL v8 object {objectIndex} face {f} has vertex indices " +
                    $"({v0}, {v1}, {v2}) outside [0, {header.NumVerts})");
            }

            faces[f] = new ColFace(faceFlags, terrain, v0, v1, v2);
        }

        return new ColObject
        {
            Checksum = header.Checksum,
            Flags = header.Flags,
            BBoxMin = header.BBoxMin,
            BBoxMax = header.BBoxMax,
            Vertices = vertices,
            Faces = faces,
            Intensities = intensities,
            VertexColorsRgba = vertexColors
        };
    }

    private static ColScene ParseXen(ReadOnlySpan<byte> data)
    {
        var numObjects = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        var totalVerts = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        var totalLargeFaces = BinaryPrimitives.ReadInt32BigEndian(data[12..]);
        var totalSmallFaces = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
        var totalLargeVerts = BinaryPrimitives.ReadInt32BigEndian(data[20..]);
        var totalSmallVerts = BinaryPrimitives.ReadInt32BigEndian(data[24..]);

        if (numObjects < 0 || numObjects > 100_000)
            throw new InvalidDataException($"Unreasonable X360 COL object count: {numObjects}");
        EnsureNonNegative(totalVerts, "total vertex");
        EnsureNonNegative(totalLargeFaces, "total large-face");
        EnsureNonNegative(totalSmallFaces, "total small-face");
        EnsureNonNegative(totalLargeVerts, "total large-vertex");
        EnsureNonNegative(totalSmallVerts, "total small-vertex");

        var objectBase = SizeofHeader;
        if (data.Length >= SizeofHeader + 4 &&
            BinaryPrimitives.ReadUInt32BigEndian(data[SizeofHeader..]) == 0)
        {
            EnsureAbsoluteRegion(data.Length, SizeofHeader, 48, "X360 supersector-header");
            ReadFiniteVector4BigEndian(data, SizeofHeader + 16, "X360 scene bounding-box minimum");
            ReadFiniteVector4BigEndian(data, SizeofHeader + 32, "X360 scene bounding-box maximum");
            objectBase += 48;
        }

        var objectBytes = checked((long)numObjects * SizeofObject);
        EnsureAbsoluteRegion(data.Length, objectBase, objectBytes, "X360 object-header");

        var baseVertOffset = Align16((long)objectBase + objectBytes);
        var largeVertexBytes = checked((long)totalLargeVerts * SizeofFloatVert);
        var vertexBytes = checked(largeVertexBytes + (long)totalSmallVerts * SizeofFixedVert);
        EnsureAbsoluteRegion(data.Length, baseVertOffset, vertexBytes, "X360 vertex");

        var baseIntensityOffset = checked(baseVertOffset + vertexBytes);
        EnsureAbsoluteRegion(data.Length, baseIntensityOffset, totalVerts, "X360 intensity");
        var baseFaceOffset = Align4(checked(baseIntensityOffset + totalVerts));
        var faceBytes = checked(
            (long)totalLargeFaces * SizeofLargeFace +
            (long)totalSmallFaces * SizeofSmallFace);
        EnsureAbsoluteRegion(data.Length, baseFaceOffset, faceBytes, "X360 face");

        var headers = new XenObjectHeader[numObjects];
        var vertexRanges = new List<(long Start, long End)>(numObjects);
        var intensityRanges = new List<(long Start, long End)>(numObjects);
        var faceRanges = new List<(long Start, long End)>(numObjects);
        long declaredVerts = 0;
        long declaredLargeVerts = 0;
        long declaredSmallVerts = 0;
        long declaredLargeFaces = 0;
        long declaredSmallFaces = 0;

        for (var i = 0; i < numObjects; i++)
        {
            var hdr = objectBase + i * SizeofObject;
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(data[hdr..]);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data[(hdr + 4)..]);
            var numVerts = BinaryPrimitives.ReadUInt16BigEndian(data[(hdr + 6)..]);
            var numFaces = BinaryPrimitives.ReadUInt16BigEndian(data[(hdr + 8)..]);
            var smallFaceByte = data[hdr + 10];
            var fixedVertByte = data[hdr + 11];
            if (smallFaceByte > 1)
                throw new InvalidDataException($"X360 COL object {i} has invalid small-face flag {smallFaceByte}");
            if (fixedVertByte > 1)
                throw new InvalidDataException($"X360 COL object {i} has invalid fixed-vertex flag {fixedVertByte}");

            var useSmallFaces = smallFaceByte != 0;
            var useFixedVerts = fixedVertByte != 0;
            var firstFaceOffset = BinaryPrimitives.ReadUInt32BigEndian(data[(hdr + 12)..]);
            var bboxMin = ReadFiniteVector4BigEndian(data, hdr + 16, $"X360 object {i} bounding-box minimum");
            var bboxMax = ReadFiniteVector4BigEndian(data, hdr + 32, $"X360 object {i} bounding-box maximum");
            var firstVertOffset = BinaryPrimitives.ReadUInt32BigEndian(data[(hdr + 48)..]);
            var intensityOffset = BinaryPrimitives.ReadUInt32BigEndian(data[(hdr + 56)..]);

            var vertexStride = useFixedVerts ? SizeofFixedVert : SizeofFloatVert;
            var vertexEnd = checked((long)firstVertOffset + (long)numVerts * vertexStride);
            if (vertexEnd > vertexBytes)
            {
                throw new InvalidDataException(
                    $"X360 COL object {i} vertex byte range [{firstVertOffset}, {vertexEnd}) " +
                    $"exceeds aggregate vertex byte count {vertexBytes}");
            }
            if (firstVertOffset % (uint)vertexStride != 0)
            {
                throw new InvalidDataException(
                    $"X360 COL object {i} vertex byte offset {firstVertOffset} is not {vertexStride}-byte aligned");
            }
            if (!useFixedVerts && vertexEnd > largeVertexBytes ||
                useFixedVerts && firstVertOffset < largeVertexBytes)
            {
                throw new InvalidDataException(
                    $"X360 COL object {i} vertex range is outside its " +
                    $"{(useFixedVerts ? "fixed" : "float")} vertex pool");
            }

            var intensityEnd = checked((long)intensityOffset + numVerts);
            if (intensityEnd > totalVerts)
            {
                throw new InvalidDataException(
                    $"X360 COL object {i} intensity range [{intensityOffset}, {intensityEnd}) " +
                    $"exceeds aggregate intensity count {totalVerts}");
            }

            var faceStride = useSmallFaces ? SizeofSmallFace : SizeofLargeFace;
            var faceEnd = checked((long)firstFaceOffset + (long)numFaces * faceStride);
            if (faceEnd > faceBytes)
            {
                throw new InvalidDataException(
                    $"X360 COL object {i} face byte range [{firstFaceOffset}, {faceEnd}) " +
                    $"exceeds aggregate face byte count {faceBytes}");
            }
            if ((firstFaceOffset & 1) != 0)
                throw new InvalidDataException($"X360 COL object {i} face byte offset {firstFaceOffset} is not aligned");

            if (numVerts != 0)
            {
                vertexRanges.Add((firstVertOffset, vertexEnd));
                intensityRanges.Add((intensityOffset, intensityEnd));
            }
            if (numFaces != 0)
                faceRanges.Add((firstFaceOffset, faceEnd));

            declaredVerts += numVerts;
            if (useFixedVerts)
                declaredSmallVerts += numVerts;
            else
                declaredLargeVerts += numVerts;
            if (useSmallFaces)
                declaredSmallFaces += numFaces;
            else
                declaredLargeFaces += numFaces;

            headers[i] = new XenObjectHeader(
                checksum,
                flags,
                numVerts,
                numFaces,
                useSmallFaces,
                useFixedVerts,
                firstVertOffset,
                firstFaceOffset,
                intensityOffset,
                new Vector3(bboxMin.X, bboxMin.Y, bboxMin.Z),
                new Vector3(bboxMax.X, bboxMax.Y, bboxMax.Z));
        }

        EnsureAggregateMatches(declaredVerts, totalVerts, "X360 COL", "vertex");
        EnsureAggregateMatches(declaredLargeVerts, totalLargeVerts, "X360 COL", "large-vertex");
        EnsureAggregateMatches(declaredSmallVerts, totalSmallVerts, "X360 COL", "small-vertex");
        EnsureAggregateMatches(declaredLargeFaces, totalLargeFaces, "X360 COL", "large-face");
        EnsureAggregateMatches(declaredSmallFaces, totalSmallFaces, "X360 COL", "small-face");
        EnsurePartition(vertexRanges, vertexBytes, "X360 COL", "vertex byte");
        EnsurePartition(intensityRanges, totalVerts, "X360 COL", "intensity");
        EnsurePartition(faceRanges, faceBytes, "X360 COL", "face byte");

        var objects = new ColObject[numObjects];
        for (var i = 0; i < headers.Length; i++)
        {
            objects[i] = ParseXenObject(
                data,
                headers[i],
                i,
                baseVertOffset,
                baseIntensityOffset,
                baseFaceOffset);
        }

        return new ColScene
        {
            Version = 10,
            Objects = objects
        };
    }

    private static ColObject ParseXenObject(
        ReadOnlySpan<byte> data,
        XenObjectHeader header,
        int objectIndex,
        long baseVertOffset,
        long baseIntensityOffset,
        long baseFaceOffset)
    {
        var vertices = new Vector3[header.NumVerts];
        var vertexStride = header.UseFixedVerts ? SizeofFixedVert : SizeofFloatVert;
        var absVertOffset = checked(baseVertOffset + header.FirstVertOffset);
        for (var v = 0; v < header.NumVerts; v++)
        {
            var off = checked((int)(absVertOffset + (long)v * vertexStride));
            Vector3 vertex;
            if (header.UseFixedVerts)
            {
                vertex = new Vector3(
                    BinaryPrimitives.ReadUInt16BigEndian(data[off..]) * 0.0625f + header.BBoxMin.X,
                    BinaryPrimitives.ReadUInt16BigEndian(data[(off + 2)..]) * 0.0625f + header.BBoxMin.Y,
                    BinaryPrimitives.ReadUInt16BigEndian(data[(off + 4)..]) * 0.0625f + header.BBoxMin.Z);
            }
            else
            {
                vertex = new Vector3(
                    ReadSingleBigEndian(data[off..]),
                    ReadSingleBigEndian(data[(off + 4)..]),
                    ReadSingleBigEndian(data[(off + 8)..]));
            }

            if (!IsFinite(vertex))
                throw new InvalidDataException($"X360 COL object {objectIndex} vertex {v} is not finite");
            vertices[v] = vertex;
        }

        var intensities = new byte[header.NumVerts];
        var absIntensityOffset = checked((int)(baseIntensityOffset + header.IntensityOffset));
        data.Slice(absIntensityOffset, header.NumVerts).CopyTo(intensities);

        var faces = new ColFace[header.NumFaces];
        var faceStride = header.UseSmallFaces ? SizeofSmallFace : SizeofLargeFace;
        var absFaceOffset = checked(baseFaceOffset + header.FirstFaceOffset);
        for (var f = 0; f < header.NumFaces; f++)
        {
            var off = checked((int)(absFaceOffset + (long)f * faceStride));
            var faceFlags = BinaryPrimitives.ReadUInt16BigEndian(data[off..]);
            var terrain = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 2)..]);
            int v0;
            int v1;
            int v2;
            if (header.UseSmallFaces)
            {
                v0 = data[off + 4];
                v1 = data[off + 5];
                v2 = data[off + 6];
            }
            else
            {
                v0 = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 4)..]);
                v1 = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 6)..]);
                v2 = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 8)..]);
            }

            if ((uint)v0 >= header.NumVerts ||
                (uint)v1 >= header.NumVerts ||
                (uint)v2 >= header.NumVerts)
            {
                throw new InvalidDataException(
                    $"X360 COL object {objectIndex} face {f} has vertex indices " +
                    $"({v0}, {v1}, {v2}) outside [0, {header.NumVerts})");
            }

            faces[f] = new ColFace(faceFlags, terrain, v0, v1, v2);
        }

        return new ColObject
        {
            Checksum = header.Checksum,
            Flags = header.Flags,
            BBoxMin = header.BBoxMin,
            BBoxMax = header.BBoxMax,
            Vertices = vertices,
            Faces = faces,
            Intensities = intensities
        };
    }

    private static int GetRequiredRegionOffset(
        int dataLength,
        int baseOffset,
        int relativeOffset,
        int count,
        int stride,
        string regionName)
    {
        if (count == 0)
            return 0;

        var start = (long)baseOffset + relativeOffset;
        var length = (long)count * stride;
        if (start < 0 || start > dataLength || length > dataLength - start)
        {
            throw new InvalidDataException(
                $"COL {regionName} data is truncated: offset {start}, length {length}, file size {dataLength}");
        }

        return (int)start;
    }

    private static void EnsureAbsoluteRegion(
        int dataLength,
        long start,
        long length,
        string regionName)
    {
        if (start < 0 || start > dataLength || length < 0 || length > dataLength - start)
        {
            throw new InvalidDataException(
                $"COL {regionName} data is truncated: offset {start}, length {length}, file size {dataLength}");
        }
    }

    private static Vector4 ReadFiniteVector4(ReadOnlySpan<byte> data, int offset, string fieldName)
    {
        var value = new Vector4(
            BitConverter.ToSingle(data[offset..]),
            BitConverter.ToSingle(data[(offset + 4)..]),
            BitConverter.ToSingle(data[(offset + 8)..]),
            BitConverter.ToSingle(data[(offset + 12)..]));
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) ||
            !float.IsFinite(value.W))
        {
            throw new InvalidDataException($"COL v8 {fieldName} is not finite");
        }

        return value;
    }

    private static Vector4 ReadFiniteVector4BigEndian(
        ReadOnlySpan<byte> data,
        int offset,
        string fieldName)
    {
        var value = new Vector4(
            ReadSingleBigEndian(data[offset..]),
            ReadSingleBigEndian(data[(offset + 4)..]),
            ReadSingleBigEndian(data[(offset + 8)..]),
            ReadSingleBigEndian(data[(offset + 12)..]));
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) ||
            !float.IsFinite(value.W))
        {
            throw new InvalidDataException($"COL {fieldName} is not finite");
        }

        return value;
    }

    private static float ReadSingleBigEndian(ReadOnlySpan<byte> data)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data));
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static void EnsureAggregateMatches(long declared, int aggregate, string regionName)
    {
        EnsureAggregateMatches(declared, aggregate, "COL v8", regionName);
    }

    private static void EnsureAggregateMatches(
        long declared,
        int aggregate,
        string formatName,
        string regionName)
    {
        if (declared != aggregate)
        {
            throw new InvalidDataException(
                $"{formatName} object {regionName} count {declared} does not match aggregate count {aggregate}");
        }
    }

    private static void EnsurePartition(
        List<(long Start, long End)> ranges,
        long expectedLength,
        string regionName)
    {
        EnsurePartition(ranges, expectedLength, "COL v8", regionName);
    }

    private static void EnsurePartition(
        List<(long Start, long End)> ranges,
        long expectedLength,
        string formatName,
        string regionName)
    {
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        long cursor = 0;
        foreach (var range in ranges)
        {
            if (range.Start != cursor)
            {
                var problem = range.Start < cursor ? "overlaps" : "leaves a gap after";
                throw new InvalidDataException(
                    $"{formatName} {regionName} range [{range.Start}, {range.End}) {problem} offset {cursor}");
            }

            cursor = range.End;
        }

        if (cursor != expectedLength)
        {
            throw new InvalidDataException(
                $"{formatName} {regionName} ranges end at {cursor}, expected {expectedLength}");
        }
    }

    private static long Align16(long value)
    {
        return checked((value + 15) & ~15L);
    }

    private static int Align16(int value)
    {
        return (value + 15) & ~15;
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    private static long Align4(long value)
    {
        return checked((value + 3) & ~3L);
    }

    private static void EnsureNonNegative(int value, string fieldName)
    {
        if (value < 0)
            throw new InvalidDataException($"COL {fieldName} count is negative: {value}");
    }

    private readonly record struct Thps4ObjectHeader(
        uint Checksum,
        ushort Flags,
        ushort NumVerts,
        ushort NumFaces,
        bool UseSmallFaces,
        uint FirstVert,
        uint FirstFaceOffset,
        uint BspRootOffset,
        Vector3 BBoxMin,
        Vector3 BBoxMax);

    private readonly record struct XenObjectHeader(
        uint Checksum,
        ushort Flags,
        ushort NumVerts,
        ushort NumFaces,
        bool UseSmallFaces,
        bool UseFixedVerts,
        uint FirstVertOffset,
        uint FirstFaceOffset,
        uint IntensityOffset,
        Vector3 BBoxMin,
        Vector3 BBoxMax);
}
