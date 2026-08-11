using System.Buffers.Binary;
using System.Numerics;

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
///     64B objects : checksum, numVerts u32, numFaces u16 + pad,
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

        var version = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
        if (version != 10)
            throw new InvalidDataException($"Unsupported NGC COL version: {version}");

        var numObjects = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
        var totalVerts = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]));
        var totalFaces = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]));
        var ssRows = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]));
        var ssCols = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        if (numObjects > MaxObjects)
            throw new InvalidDataException($"Unreasonable object count: {numObjects}");

        var boundsMin = ReadVector4(data, SizeofHeader);
        var boundsMax = ReadVector4(data, SizeofHeader + 16);

        var objectsEnd = SizeofHeader + SizeofBounds + numObjects * SizeofObject;
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
                NumVerts = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 4)..])),
                NumFaces = BinaryPrimitives.ReadUInt16BigEndian(data[(o + 8)..]),
                FirstVertIndex = vertCursor,
                FirstFaceIndex = faceCursor,
                BBoxMin = ReadVector4(data, o + 16),
                BBoxMax = ReadVector4(data, o + 32),
                BspNodeOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[(o + 52)..]))
            };

            if (BinaryPrimitives.ReadUInt16BigEndian(data[(o + 10)..]) != 0)
                throw new InvalidDataException($"Object {i}: non-zero pad after face count");
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(o + 12)..]) != (uint)(faceCursor * SizeofFace))
                throw new InvalidDataException($"Object {i}: face byte offset breaks cumulative layout");
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(o + 48)..]) != 0)
                throw new InvalidDataException($"Object {i}: vertex pool pointer slot is not zero");
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(o + 56)..]) != (uint)(faceCursor * 3))
                throw new InvalidDataException($"Object {i}: corner intensity offset breaks cumulative layout");
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(o + 60)..]) != 0)
                throw new InvalidDataException($"Object {i}: non-zero trailing pad");

            records[i] = record;
            vertCursor += record.NumVerts;
            faceCursor += record.NumFaces;
        }

        if (vertCursor != totalVerts)
            throw new InvalidDataException($"Object vertex counts sum to {vertCursor}, header says {totalVerts}");
        if (faceCursor != totalFaces)
            throw new InvalidDataException($"Object face counts sum to {faceCursor}, header says {totalFaces}");

        // ── Region offsets (NxScene.cpp read_collision, NGC path) ──
        var cornerStart = objectsEnd;
        var faceStart = Align4(cornerStart + totalFaces * 3);
        var nodeSizeOffset = faceStart + totalFaces * SizeofFace + ((totalFaces & 1) != 0 ? 2 : 0);
        if (nodeSizeOffset + 4 > data.Length)
            throw new InvalidDataException("File truncated before BSP node array size");

        var cornerIntensities = data[cornerStart..(cornerStart + totalFaces * 3)].ToArray();
        var cornerUniform = true;
        foreach (var b in cornerIntensities)
        {
            if (b != 0xFF)
            {
                cornerUniform = false;
                break;
            }
        }

        var nodeSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[nodeSizeOffset..]));
        var nodeBase = nodeSizeOffset + 4;
        var poolBase = nodeBase + nodeSize;
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
                if (v0 < record.FirstVertIndex || v0 >= record.FirstVertIndex + record.NumVerts ||
                    v1 < record.FirstVertIndex || v1 >= record.FirstVertIndex + record.NumVerts ||
                    v2 < record.FirstVertIndex || v2 >= record.FirstVertIndex + record.NumVerts)
                {
                    faceIndicesContained = false;
                }

                faces[f] = new NgcColFace(flags, terrain, v0, v1, v2);
                faceOffset += SizeofFace;
            }

            var bspRoot = ParseBspNode(
                data, nodeBase, nodeSize, poolBase, poolElements,
                record.BspNodeOffset, record.NumFaces, coveredNodes, depth: 0,
                objectIndex: i);

            objects[i] = new NgcColObject
            {
                Checksum = record.Checksum,
                NumVerts = record.NumVerts,
                BBoxMin = record.BBoxMin,
                BBoxMax = record.BBoxMax,
                FirstVertIndex = record.FirstVertIndex,
                FirstFaceIndex = record.FirstFaceIndex,
                Faces = faces,
                BspRoot = bspRoot
            };
        }

        for (var slot = 0; slot < coveredNodes.Length; slot++)
        {
            if (!coveredNodes[slot])
                throw new InvalidDataException($"BSP node slot {slot} belongs to no object's tree");
        }

        return new NgcColScene
        {
            Version = version,
            SuperSectorRows = ssRows,
            SuperSectorCols = ssCols,
            SceneBoundsMin = boundsMin,
            SceneBoundsMax = boundsMax,
            Objects = objects,
            TotalVerts = totalVerts,
            TotalFaces = totalFaces,
            PoolElementCount = poolElements,
            CornerIntensities = cornerIntensities,
            CornerIntensitiesUniform = cornerUniform,
            FaceIndicesObjectContained = faceIndicesContained
        };
    }

    private static NgcColBspNode ParseBspNode(
        ReadOnlySpan<byte> data, int nodeBase, int nodeSize, int poolBase, int poolElements,
        int nodeOffset, int objectFaceCount, bool[] coveredNodes, int depth, int objectIndex)
    {
        if (depth > MaxTreeDepth)
            throw new InvalidDataException($"Object {objectIndex}: BSP tree exceeds depth {MaxTreeDepth}");
        if (nodeOffset % SizeofNode != 0)
            throw new InvalidDataException($"Object {objectIndex}: misaligned BSP node offset {nodeOffset}");
        if (nodeOffset + SizeofNode > nodeSize)
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
            var poolOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[4..]));
            if (poolOffset + numFaces > poolElements)
                throw new InvalidDataException($"Object {objectIndex}: BSP leaf list outside face-index pool");

            var indices = new ushort[numFaces];
            for (var k = 0; k < numFaces; k++)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(
                    data[(poolBase + (poolOffset + k) * 2)..]);
                if (value >= objectFaceCount)
                    throw new InvalidDataException(
                        $"Object {objectIndex}: BSP leaf face index {value} outside object face count {objectFaceCount}");
                indices[k] = value;
            }

            return new NgcColBspNode
            {
                Axis = 3,
                LeafFaceIndices = indices
            };
        }

        var splitWord = BinaryPrimitives.ReadInt32BigEndian(record);
        var childrenWord = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
        if ((childrenWord & 0x2) != 0)
            throw new InvalidDataException($"Object {objectIndex}: unexpected BSP child flag bit");
        var childOffset = checked((int)(childrenWord & ~0x3u));
        var leftIsGreater = (childrenWord & 0x1) != 0;

        var left = ParseBspNode(
            data, nodeBase, nodeSize, poolBase, poolElements,
            childOffset, objectFaceCount, coveredNodes, depth + 1, objectIndex);
        var right = ParseBspNode(
            data, nodeBase, nodeSize, poolBase, poolElements,
            childOffset + SizeofNode, objectFaceCount, coveredNodes, depth + 1, objectIndex);

        return new NgcColBspNode
        {
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

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    private readonly struct ObjectRecord
    {
        public required uint Checksum { get; init; }
        public required int NumVerts { get; init; }
        public required ushort NumFaces { get; init; }
        public required int FirstVertIndex { get; init; }
        public required int FirstFaceIndex { get; init; }
        public required Vector4 BBoxMin { get; init; }
        public required Vector4 BBoxMax { get; init; }
        public required int BspNodeOffset { get; init; }
    }
}
