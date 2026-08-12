using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     Strict structural parser for GameCube collision files (.col.ngc,
///     THAW GC). All fields are big-endian. Layout transcribed from the THUG
///     source's <c>__PLAT_NGC__</c> paths (<c>NxScene.cpp read_collision</c>,
///     <c>CollTriData.h/.cpp</c>) and corpus-verified byte-exact:
///     <code>
///     24B header  : version=10, numObjects, totalVerts, totalFaces,
///                   superSectorRows, superSectorCols
///     32B bounds  : scene bbox min(x,y,z,1) max(x,y,z,1)
///     64B objects : checksum, flags u16, numVerts u16, numFaces u16,
///                   useSmallFaces u8, useFixedVerts u8,
///                   faceByteOffset u32, bboxMin 4f, bboxMax 4f,
///                   vertPoolPtrSlot (0), bspNodeByteOffset u32,
///                   cornerIntensityByteOffset u32 (= 3 * cumulative faces), pad
///     corner region: totalFaces * 3 bytes (per-corner intensity)
///     faces       : align4, then totalFaces x 10B (flags, terrain, i0, i1, i2)
///     node size   : 2-byte pad when totalFaces is odd, then u32
///     nodes       : 8B BSP records (leaf when byte 3 == 3)
///     pool        : u16 face-index elements to end of file
///     </code>
///     Vertex positions are NOT stored: the engine binds
///     <c>mp_raw_vert_pos</c> to the render scene's vertex pool at load, so
///     this parser is inspection-only by design.
/// </summary>
public static class NgcColFile
{
    private const int SizeofHeader = 24;
    private const int SizeofBounds = 32;
    private const int SizeofObject = 64;
    private const int SizeofFace = 10;
    private const int SizeofNode = 8;
    private const int MaxObjects = 100_000;
    private const int MaxTreeDepth = 64;

    /// <summary>Returns true when the data starts like a big-endian v10 collision file.</summary>
    public static bool IsNgcColFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeofHeader + SizeofBounds) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != 10) return false;
        var numObjects = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        return numObjects <= MaxObjects;
    }

    public static NgcColScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static NgcColScene Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeofHeader + SizeofBounds)
            throw new InvalidDataException("File too small for NGC COL header");

        var version = ReadInt32Count(data, 0, "version");
        if (version != 10)
            throw new InvalidDataException($"Unsupported NGC COL version: {version}");

        var numObjects = ReadInt32Count(data, 4, "object count");
        var totalVerts = ReadInt32Count(data, 8, "vertex count");
        var totalFaces = ReadInt32Count(data, 12, "face count");
        var ssRows = ReadInt32Count(data, 16, "supersector row count");
        var ssCols = ReadInt32Count(data, 20, "supersector column count");
        if (numObjects > MaxObjects)
            throw new InvalidDataException($"Unreasonable object count: {numObjects}");

        var boundsMin = ReadVector4(data, SizeofHeader);
        var boundsMax = ReadVector4(data, SizeofHeader + 16);
        ValidateBounds(boundsMin, boundsMax, "Scene", totalVerts == 0 && totalFaces == 0);

        var objectsEnd = CheckedSectionEnd(
            SizeofHeader + SizeofBounds, numObjects, SizeofObject, "object records");
        if (data.Length < objectsEnd)
            throw new InvalidDataException("File truncated inside object records");

        // ── Object records ──
        var records = new ObjectRecord[numObjects];
        var vertCursor = 0;
        var faceCursor = 0;
        for (var i = 0; i < numObjects; i++)
        {
            var o = SizeofHeader + SizeofBounds + i * SizeofObject;
            var record = new ObjectRecord
            {
                Checksum = BinaryPrimitives.ReadUInt32BigEndian(data[o..]),
                Flags = BinaryPrimitives.ReadUInt16BigEndian(data[(o + 4)..]),
                NumVerts = BinaryPrimitives.ReadUInt16BigEndian(data[(o + 6)..]),
                NumFaces = BinaryPrimitives.ReadUInt16BigEndian(data[(o + 8)..]),
                UsesSmallFaces = data[o + 10] != 0,
                UsesFixedVertices = data[o + 11] != 0,
                CumulativeDeclaredVertexBase = vertCursor,
                FirstFaceIndex = faceCursor,
                BBoxMin = ReadVector4(data, o + 16),
                BBoxMax = ReadVector4(data, o + 32),
                BspNodeOffset = ReadInt32Offset(data, o + 52, $"Object {i} BSP node"),
                CornerIntensityOffset = ReadInt32Offset(
                    data, o + 56, $"Object {i} corner intensity")
            };

            if (record.UsesSmallFaces)
                throw new InvalidDataException($"Object {i}: small-face encoding is not valid in THAW GC v10");
            if (record.UsesFixedVertices)
                throw new InvalidDataException($"Object {i}: fixed-vertex encoding is not valid in THAW GC v10");
            RequireSerializedOffset(
                BinaryPrimitives.ReadUInt32BigEndian(data[(o + 12)..]),
                (long)faceCursor * SizeofFace,
                $"Object {i}: face byte offset breaks cumulative layout");
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(o + 48)..]) != 0)
                throw new InvalidDataException($"Object {i}: vertex pool pointer slot is not zero");
            RequireSerializedOffset(
                BinaryPrimitives.ReadUInt32BigEndian(data[(o + 56)..]),
                (long)faceCursor * 3,
                $"Object {i}: corner intensity offset breaks cumulative layout");
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(o + 60)..]) != 0)
                throw new InvalidDataException($"Object {i}: non-zero trailing pad");

            ValidateBounds(
                record.BBoxMin, record.BBoxMax, $"Object {i}",
                record.NumVerts == 0 && record.NumFaces == 0);

            records[i] = record;
            vertCursor = CheckedAdd(vertCursor, record.NumVerts, "object vertex-count sum");
            faceCursor = CheckedAdd(faceCursor, record.NumFaces, "object face-count sum");
        }

        if (vertCursor != totalVerts)
            throw new InvalidDataException($"Object vertex counts sum to {vertCursor}, header says {totalVerts}");
        if (faceCursor != totalFaces)
            throw new InvalidDataException($"Object face counts sum to {faceCursor}, header says {totalFaces}");

        // ── Region offsets (NxScene.cpp read_collision, NGC path) ──
        var cornerStart = objectsEnd;
        var cornerEnd = CheckedSectionEnd(cornerStart, totalFaces, 3, "corner-intensity region");
        var faceStart = Align4Checked(cornerEnd, "corner-intensity alignment");
        var faceEnd = CheckedSectionEnd(faceStart, totalFaces, SizeofFace, "face records");
        var nodeSizeOffset = CheckedAdd(
            faceEnd, (totalFaces & 1) != 0 ? 2 : 0, "face alignment");
        var nodeBase = CheckedAdd(nodeSizeOffset, 4, "BSP node-size field");
        if (nodeBase > data.Length)
            throw new InvalidDataException("File truncated before BSP node array size");
        RequireZeroBytes(data[cornerEnd..faceStart], "corner-intensity alignment padding");
        RequireZeroBytes(data[faceEnd..nodeSizeOffset], "face alignment padding");

        var cornerIntensities = data[cornerStart..cornerEnd].ToArray();
        var cornerUniform = cornerIntensities.All(static value => value == 0xFF);

        var nodeSize = ReadInt32Count(data, nodeSizeOffset, "BSP node-array byte count");
        var poolBase = CheckedAdd(nodeBase, nodeSize, "BSP node array");
        if (nodeSize % SizeofNode != 0)
            throw new InvalidDataException($"BSP node array size {nodeSize} is not a multiple of 8");
        if (poolBase > data.Length)
            throw new InvalidDataException("BSP node array extends past end of file");
        var poolBytes = data.Length - poolBase;
        if ((poolBytes & 1) != 0)
            throw new InvalidDataException("Face-index pool has an odd byte count");
        var poolElements = poolBytes / 2;

        // ── Faces ──
        var faceIndicesContained = true;
        var objects = new NgcColObject[numObjects];
        var faceOffset = faceStart;
        var coveredNodes = new bool[nodeSize / SizeofNode];
        var coveredPoolElements = new bool[poolElements];
        for (var i = 0; i < numObjects; i++)
        {
            var record = records[i];
            var faces = new NgcColFace[record.NumFaces];
            for (var f = 0; f < faces.Length; f++)
            {
                var flags = BinaryPrimitives.ReadUInt16BigEndian(data[faceOffset..]);
                var terrain = BinaryPrimitives.ReadUInt16BigEndian(data[(faceOffset + 2)..]);
                var v0 = BinaryPrimitives.ReadUInt16BigEndian(data[(faceOffset + 4)..]);
                var v1 = BinaryPrimitives.ReadUInt16BigEndian(data[(faceOffset + 6)..]);
                var v2 = BinaryPrimitives.ReadUInt16BigEndian(data[(faceOffset + 8)..]);
                if (v0 >= totalVerts || v1 >= totalVerts || v2 >= totalVerts)
                    throw new InvalidDataException(
                        $"Object {i} face {f}: vertex index outside the file's vertex numbering");
                var declaredVertexEnd = (long)record.CumulativeDeclaredVertexBase + record.NumVerts;
                if (v0 < record.CumulativeDeclaredVertexBase || v0 >= declaredVertexEnd ||
                    v1 < record.CumulativeDeclaredVertexBase || v1 >= declaredVertexEnd ||
                    v2 < record.CumulativeDeclaredVertexBase || v2 >= declaredVertexEnd)
                {
                    faceIndicesContained = false;
                }

                faces[f] = new NgcColFace(flags, terrain, v0, v1, v2);
                faceOffset += SizeofFace;
            }

            var bspRoot = ParseBspNode(
                data, nodeBase, nodeSize, poolBase, poolElements,
                record.BspNodeOffset, record.NumFaces, coveredNodes, coveredPoolElements, depth: 0,
                objectIndex: i);

            objects[i] = new NgcColObject
            {
                Checksum = record.Checksum,
                Flags = record.Flags,
                NumVerts = record.NumVerts,
                BBoxMin = record.BBoxMin,
                BBoxMax = record.BBoxMax,
                CumulativeDeclaredVertexBase = record.CumulativeDeclaredVertexBase,
                FirstFaceIndex = record.FirstFaceIndex,
                UsesSmallFaces = record.UsesSmallFaces,
                UsesFixedVertices = record.UsesFixedVertices,
                BspNodeByteOffset = record.BspNodeOffset,
                CornerIntensityByteOffset = record.CornerIntensityOffset,
                Faces = faces,
                BspRoot = bspRoot
            };
        }

        for (var slot = 0; slot < coveredNodes.Length; slot++)
        {
            if (!coveredNodes[slot])
                throw new InvalidDataException($"BSP node slot {slot} belongs to no object's tree");
        }

        for (var element = 0; element < coveredPoolElements.Length; element++)
        {
            if (!coveredPoolElements[element])
                throw new InvalidDataException($"BSP face-index pool element {element} belongs to no leaf");
        }

        return new NgcColScene
        {
            SerializedSize = data.Length,
            SerializedSha256 = Hash(data),
            Version = version,
            SuperSectorRows = ssRows,
            SuperSectorCols = ssCols,
            SceneBoundsMin = boundsMin,
            SceneBoundsMax = boundsMax,
            Objects = objects,
            TotalVerts = totalVerts,
            TotalFaces = totalFaces,
            PoolElementCount = poolElements,
            BspNodeByteCount = nodeSize,
            BspNodeSha256 = Hash(data.Slice(nodeBase, nodeSize)),
            FaceIndexPoolSha256 = Hash(data[poolBase..]),
            CornerIntensities = cornerIntensities,
            CornerIntensitiesUniform = cornerUniform,
            CornerIntensitiesSha256 = Hash(cornerIntensities),
            FaceIndicesWithinCumulativeDeclaredVertexRanges = faceIndicesContained
        };
    }

    private static NgcColBspNode ParseBspNode(
        ReadOnlySpan<byte> data, int nodeBase, int nodeSize, int poolBase, int poolElements,
        int nodeOffset, int objectFaceCount, bool[] coveredNodes, bool[] coveredPoolElements,
        int depth, int objectIndex)
    {
        if (depth > MaxTreeDepth)
            throw new InvalidDataException($"Object {objectIndex}: BSP tree exceeds depth {MaxTreeDepth}");
        if (nodeOffset % SizeofNode != 0)
            throw new InvalidDataException($"Object {objectIndex}: misaligned BSP node offset {nodeOffset}");
        if (nodeOffset < 0 || (long)nodeOffset + SizeofNode > nodeSize)
            throw new InvalidDataException($"Object {objectIndex}: BSP node offset {nodeOffset} outside node array");
        var slot = nodeOffset / SizeofNode;
        if (coveredNodes[slot])
            throw new InvalidDataException($"Object {objectIndex}: BSP node {slot} referenced twice");
        coveredNodes[slot] = true;

        var record = data.Slice(nodeBase + nodeOffset, SizeofNode);
        var axis = record[3] & 0x3;
        if (axis == 3)
        {
            if (record[3] != 3)
                throw new InvalidDataException($"Object {objectIndex}: BSP leaf axis byte has high bits set");
            var numFaces = BinaryPrimitives.ReadUInt16BigEndian(record);
            if (record[2] != 0)
                throw new InvalidDataException($"Object {objectIndex}: non-zero BSP leaf pad");
            var poolOffsetRaw = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
            if (poolOffsetRaw > int.MaxValue)
                throw new InvalidDataException($"Object {objectIndex}: BSP leaf pool offset is too large");
            var poolOffset = (int)poolOffsetRaw;
            if ((long)poolOffset + numFaces > poolElements)
                throw new InvalidDataException($"Object {objectIndex}: BSP leaf list outside face-index pool");

            var indices = new ushort[numFaces];
            for (var k = 0; k < numFaces; k++)
            {
                var poolElement = poolOffset + k;
                if (coveredPoolElements[poolElement])
                    throw new InvalidDataException(
                        $"Object {objectIndex}: BSP face-index pool element {poolElement} referenced twice");
                coveredPoolElements[poolElement] = true;
                var value = BinaryPrimitives.ReadUInt16BigEndian(
                    data[(poolBase + poolElement * 2)..]);
                if (value >= objectFaceCount)
                    throw new InvalidDataException(
                        $"Object {objectIndex}: BSP leaf face index {value} outside object face count {objectFaceCount}");
                indices[k] = value;
            }

            return new NgcColBspNode
            {
                NodeByteOffset = nodeOffset,
                Axis = 3,
                LeafPoolElementOffset = poolOffset,
                LeafFaceIndices = indices
            };
        }

        var splitWord = BinaryPrimitives.ReadInt32BigEndian(record);
        var childrenWord = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
        if ((childrenWord & 0x2) != 0)
            throw new InvalidDataException($"Object {objectIndex}: unexpected BSP child flag bit");
        var childOffsetRaw = childrenWord & ~0x3u;
        if (childOffsetRaw > int.MaxValue)
            throw new InvalidDataException($"Object {objectIndex}: BSP child offset is too large");
        var childOffset = (int)childOffsetRaw;
        var leftIsGreater = (childrenWord & 0x1) != 0;

        var left = ParseBspNode(
            data, nodeBase, nodeSize, poolBase, poolElements,
            childOffset, objectFaceCount, coveredNodes, coveredPoolElements, depth + 1, objectIndex);
        var right = ParseBspNode(
            data, nodeBase, nodeSize, poolBase, poolElements,
            CheckedAdd(childOffset, SizeofNode, "BSP sibling node offset"), objectFaceCount,
            coveredNodes, coveredPoolElements, depth + 1, objectIndex);

        return new NgcColBspNode
        {
            NodeByteOffset = nodeOffset,
            Axis = axis,
            RawSplitWord = splitWord,
            SplitPoint = (splitWord >> 2) / 16.0f,
            LeftIsGreater = leftIsGreater,
            Less = leftIsGreater ? right : left,
            Greater = leftIsGreater ? left : right
        };
    }

    private static Vector4 ReadVector4(ReadOnlySpan<byte> data, int offset)
    {
        return new Vector4(
            BinaryPrimitives.ReadSingleBigEndian(data[offset..]),
            BinaryPrimitives.ReadSingleBigEndian(data[(offset + 4)..]),
            BinaryPrimitives.ReadSingleBigEndian(data[(offset + 8)..]),
            BinaryPrimitives.ReadSingleBigEndian(data[(offset + 12)..]));
    }

    private static int ReadInt32Count(ReadOnlySpan<byte> data, int offset, string field)
    {
        var raw = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        if (raw > int.MaxValue)
            throw new InvalidDataException($"{field} exceeds the supported in-memory range");
        return (int)raw;
    }

    private static int ReadInt32Offset(ReadOnlySpan<byte> data, int offset, string field)
    {
        var raw = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        if (raw > int.MaxValue)
            throw new InvalidDataException($"{field} offset exceeds the supported in-memory range");
        return (int)raw;
    }

    private static int CheckedSectionEnd(int start, int count, int stride, string field)
    {
        var end = (long)start + (long)count * stride;
        if (start < 0 || count < 0 || stride < 0 || end > int.MaxValue)
            throw new InvalidDataException($"{field} size overflows the supported in-memory range");
        return (int)end;
    }

    private static int CheckedAdd(int left, int right, string field)
    {
        var sum = (long)left + right;
        if (left < 0 || right < 0 || sum > int.MaxValue)
            throw new InvalidDataException($"{field} overflows the supported in-memory range");
        return (int)sum;
    }

    private static int Align4Checked(int value, string field)
    {
        var aligned = ((long)value + 3) & ~3L;
        if (value < 0 || aligned > int.MaxValue)
            throw new InvalidDataException($"{field} overflows the supported in-memory range");
        return (int)aligned;
    }

    private static void RequireSerializedOffset(uint actual, long expected, string message)
    {
        if (expected is < 0 or > uint.MaxValue || actual != (uint)expected)
            throw new InvalidDataException(message);
    }

    private static void RequireZeroBytes(ReadOnlySpan<byte> bytes, string field)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
                throw new InvalidDataException($"Non-zero {field}");
        }
    }

    private static void ValidateBounds(Vector4 min, Vector4 max, string owner, bool allowEmptySentinel)
    {
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(min.W) || !float.IsFinite(max.X) || !float.IsFinite(max.Y) ||
            !float.IsFinite(max.Z) || !float.IsFinite(max.W))
        {
            throw new InvalidDataException($"{owner}: bounds contain a non-finite value");
        }

        if ((min.X > max.X || min.Y > max.Y || min.Z > max.Z) &&
            !(allowEmptySentinel && IsEmptyBoundsSentinel(min, max)))
        {
            throw new InvalidDataException($"{owner}: bounds minimum exceeds maximum");
        }
        if (!IsExactOne(min.W) || !IsExactOne(max.W))
            throw new InvalidDataException($"{owner}: bounds W components must both be 1");
    }

    private static bool IsEmptyBoundsSentinel(Vector4 min, Vector4 max)
    {
        return HasBits(min.X, 1000.0f) && HasBits(min.Y, 1000.0f) && HasBits(min.Z, 1000.0f) &&
               HasBits(max.X, -1000.0f) && HasBits(max.Y, -1000.0f) && HasBits(max.Z, -1000.0f);
    }

    private static bool IsExactOne(float value)
    {
        return HasBits(value, 1.0f);
    }

    private static bool HasBits(float value, float expected)
    {
        return BitConverter.SingleToInt32Bits(value) == BitConverter.SingleToInt32Bits(expected);
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private readonly struct ObjectRecord
    {
        public required uint Checksum { get; init; }
        public required ushort Flags { get; init; }
        public required int NumVerts { get; init; }
        public required ushort NumFaces { get; init; }
        public required bool UsesSmallFaces { get; init; }
        public required bool UsesFixedVertices { get; init; }
        public required int CumulativeDeclaredVertexBase { get; init; }
        public required int FirstFaceIndex { get; init; }
        public required Vector4 BBoxMin { get; init; }
        public required Vector4 BBoxMax { get; init; }
        public required int BspNodeOffset { get; init; }
        public required int CornerIntensityOffset { get; init; }
    }
}
