using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

using static RwChunkReader;

/// <summary>
///     Parses RenderWare 3.x DFF (Clump) files for 3D mesh extraction.
///     Used for THPS3 PS2 .SKN files (version 0x0310).
///     Standard non-native geometry format (vertices, normals, UVs, triangles in cross-platform layout).
/// </summary>
public static class RwDffFile
{
    // Geometry flags (used in flag checks during parsing)
    private const int GF_TEXTURED = 0x04;
    private const int GF_PRELIT = 0x08;

    public static RwDffClump Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static RwDffClump Parse(byte[] data)
    {
        var offset = 0;
        var (type, size, version) = ReadChunkHeader(data, ref offset);
        if (type != RW_CLUMP)
            throw new InvalidDataException($"Not an RW Clump (got 0x{type:X4})");

        var clumpEnd = offset + (int)size;

        // Struct child: numAtomics
        if (!TryReadStruct(data, ref offset, clumpEnd, out _, out var structSize))
            throw new InvalidDataException("Missing Clump Struct");
        _ = BitConverter.ToInt32(data, offset);
        offset += (int)structSize;

        // Parse children
        RwFrame[]? frames = null;
        RwGeometry[]? geometries = null;
        var atomics = new List<RwAtomic>();

        while (offset < clumpEnd && offset + 12 <= data.Length)
        {
            if (!TryReadAnyChunk(data, ref offset, clumpEnd, out var childType, out var childSize))
                break;

            var childEnd = offset + (int)childSize;
            if (childEnd < offset || childEnd > data.Length) break;

            switch (childType)
            {
                case RW_FRAME_LIST:
                    frames = ParseFrameList(data, ref offset, childEnd);
                    break;
                case RW_GEOMETRY_LIST:
                    geometries = ParseGeometryList(data, ref offset, childEnd, version);
                    break;
                case RW_ATOMIC:
                    atomics.Add(ParseAtomic(data, ref offset, childEnd));
                    break;
            }

            offset = childEnd;
        }

        return new RwDffClump
        {
            Frames = frames ?? [],
            Geometries = geometries ?? [],
            Atomics = atomics.ToArray()
        };
    }

    /// <summary>Check if data starts with RW_CLUMP chunk type (0x0010).</summary>
    public static bool IsDffFile(byte[] data)
    {
        if (data.Length < 12) return false;
        return BitConverter.ToUInt32(data, 0) == RW_CLUMP;
    }

    private static RwFrame[] ParseFrameList(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return [];

        var numFrames = BitConverter.ToInt32(data, offset);
        if (numFrames <= 0 || numFrames > 10000)
            return [];

        var frames = new RwFrame[numFrames];
        var pos = offset + 4; // skip numFrames

        for (var i = 0; i < numFrames; i++)
        {
            // 3×3 rotation matrix (9 floats) + position (3 floats) + parentIndex(i32) + flags(i32) = 56 bytes
            if (pos + 56 > data.Length) break;

            var m = new Matrix4x4();
            m.M11 = BitConverter.ToSingle(data, pos);
            m.M12 = BitConverter.ToSingle(data, pos + 4);
            m.M13 = BitConverter.ToSingle(data, pos + 8);
            m.M21 = BitConverter.ToSingle(data, pos + 12);
            m.M22 = BitConverter.ToSingle(data, pos + 16);
            m.M23 = BitConverter.ToSingle(data, pos + 20);
            m.M31 = BitConverter.ToSingle(data, pos + 24);
            m.M32 = BitConverter.ToSingle(data, pos + 28);
            m.M33 = BitConverter.ToSingle(data, pos + 32);
            m.M41 = BitConverter.ToSingle(data, pos + 36); // position X
            m.M42 = BitConverter.ToSingle(data, pos + 40); // position Y
            m.M43 = BitConverter.ToSingle(data, pos + 44); // position Z
            m.M14 = 0;
            m.M24 = 0;
            m.M34 = 0;
            m.M44 = 1;

            var parentIndex = BitConverter.ToInt32(data, pos + 48);
            var flags = BitConverter.ToInt32(data, pos + 52);

            frames[i] = new RwFrame
            {
                LocalTransform = m,
                ParentIndex = parentIndex,
                Flags = flags
            };

            pos += 56;
        }

        offset += (int)structSize;

        // Skip per-frame Extension chunks
        for (var i = 0; i < numFrames && offset < endOffset; i++)
        {
            if (!TryReadAnyChunk(data, ref offset, endOffset, out _, out var cs))
                break;
            offset += (int)cs;
        }

        return frames;
    }

    private static RwGeometry[] ParseGeometryList(byte[] data, ref int offset, int endOffset, uint version)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return [];

        var numGeometries = BitConverter.ToInt32(data, offset);
        if (numGeometries <= 0 || numGeometries > 1000)
            return [];

        offset += (int)structSize;

        var geometries = new RwGeometry[numGeometries];
        for (var i = 0; i < numGeometries && offset < endOffset; i++)
        {
            if (!TryReadAnyChunk(data, ref offset, endOffset, out var gType, out var gSize))
                break;

            var geomEnd = offset + (int)gSize;
            if (gType == RW_GEOMETRY)
                geometries[i] = ParseGeometry(data, ref offset, geomEnd, version);

            offset = geomEnd;
        }

        return geometries;
    }

    private static RwGeometry ParseGeometry(byte[] data, ref int offset, int endOffset, uint version)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return EmptyGeometry();

        var pos = offset;
        var flags = BitConverter.ToUInt16(data, pos);
        var texCountField = BitConverter.ToUInt16(data, pos + 2);
        var numTriangles = BitConverter.ToInt32(data, pos + 4);
        var numVertices = BitConverter.ToInt32(data, pos + 8);
        var numMorphTargets = BitConverter.ToInt32(data, pos + 12);
        pos += 16;

        // For version < 0x34000, UV count comes from flags, not texCountField
        int numUVSets = texCountField;
        if (version < 0x34000)
            numUVSets = (flags & GF_TEXTURED) != 0 ? 1 : 0;

        // Surface properties (ambient, specular, diffuse) for version < 0x34000
        if (version < 0x34000)
            pos += 12;

        // Prelit vertex colors
        RwVertexColor[]? colors = null;
        if ((flags & GF_PRELIT) != 0)
        {
            colors = new RwVertexColor[numVertices];
            for (var i = 0; i < numVertices && pos + 4 <= data.Length; i++)
            {
                colors[i] = new RwVertexColor(data[pos], data[pos + 1], data[pos + 2], data[pos + 3]);
                pos += 4;
            }
        }

        // UV coordinates (only first set used)
        Vector2[]? uvs = null;
        if (numUVSets > 0)
        {
            uvs = new Vector2[numVertices];
            for (var i = 0; i < numVertices && pos + 8 <= data.Length; i++)
            {
                var u = BitConverter.ToSingle(data, pos);
                var v = BitConverter.ToSingle(data, pos + 4);
                uvs[i] = new Vector2(u, v);
                pos += 8;
            }

            // Skip additional UV sets beyond the first
            for (var set = 1; set < numUVSets; set++)
                pos += numVertices * 8;
        }

        // Triangles: (v2:u16, v1:u16, matId:u16, v3:u16) = 8 bytes each
        var triangles = new RwTriangle[numTriangles];
        for (var i = 0; i < numTriangles && pos + 8 <= data.Length; i++)
        {
            var v2 = BitConverter.ToUInt16(data, pos);
            var v1 = BitConverter.ToUInt16(data, pos + 2);
            var matId = BitConverter.ToUInt16(data, pos + 4);
            var v3 = BitConverter.ToUInt16(data, pos + 6);
            var vertex0 = v1;
            var vertex1 = v2;
            var vertex2 = v3;
            triangles[i] = new RwTriangle(vertex0, vertex1, vertex2, matId);
            pos += 8;
        }

        // Morph targets — read first target for positions and normals
        Vector3[] vertices = [];
        Vector3[]? normals = null;
        var boundingSphere = Vector4.Zero;

        for (var mt = 0; mt < numMorphTargets; mt++)
        {
            if (pos + 24 > data.Length) break;

            var bx = BitConverter.ToSingle(data, pos);
            var by = BitConverter.ToSingle(data, pos + 4);
            var bz = BitConverter.ToSingle(data, pos + 8);
            var br = BitConverter.ToSingle(data, pos + 12);
            var hasVerts = BitConverter.ToInt32(data, pos + 16) != 0;
            var hasNorms = BitConverter.ToInt32(data, pos + 20) != 0;
            pos += 24;

            if (mt == 0)
                boundingSphere = new Vector4(bx, by, bz, br);

            if (hasVerts)
            {
                if (mt == 0)
                {
                    vertices = new Vector3[numVertices];
                    for (var i = 0; i < numVertices; i++)
                    {
                        vertices[i] = new Vector3(
                            BitConverter.ToSingle(data, pos),
                            BitConverter.ToSingle(data, pos + 4),
                            BitConverter.ToSingle(data, pos + 8));
                        pos += 12;
                    }
                }
                else
                {
                    pos += numVertices * 12; // skip additional morph targets
                }
            }

            if (hasNorms)
            {
                if (mt == 0)
                {
                    normals = new Vector3[numVertices];
                    for (var i = 0; i < numVertices; i++)
                    {
                        normals[i] = new Vector3(
                            BitConverter.ToSingle(data, pos),
                            BitConverter.ToSingle(data, pos + 4),
                            BitConverter.ToSingle(data, pos + 8));
                        pos += 12;
                    }
                }
                else
                {
                    pos += numVertices * 12;
                }
            }
        }

        offset += (int)structSize;

        // MaterialList child
        RwMaterial[] materials = [];
        while (offset < endOffset && offset + 12 <= data.Length)
        {
            if (!TryReadAnyChunk(data, ref offset, endOffset, out var childType, out var childSize))
                break;

            var childEnd = offset + (int)childSize;
            if (childType == RW_MATERIAL_LIST)
                materials = RwDffDataSections.ParseMaterialList(data, ref offset, childEnd);

            offset = childEnd;
        }

        return new RwGeometry
        {
            Flags = flags,
            Vertices = vertices,
            Normals = normals,
            UVs = uvs,
            Colors = colors,
            Triangles = triangles,
            Materials = materials,
            BoundingSphere = boundingSphere
        };
    }

    private static RwAtomic ParseAtomic(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return new RwAtomic { FrameIndex = 0, GeometryIndex = 0, Flags = 0 };

        var frameIndex = BitConverter.ToInt32(data, offset);
        var geomIndex = BitConverter.ToInt32(data, offset + 4);
        var flags = BitConverter.ToInt32(data, offset + 8);
        offset += (int)structSize;

        // Scan Extension child for Skin PLG (0x0116)
        RwSkinData? skinData = null;
        if (TryReadChunk(data, ref offset, endOffset, RW_EXTENSION, out var extSize))
        {
            var extEnd = offset + (int)extSize;
            while (offset < extEnd && offset + 12 <= data.Length)
            {
                if (!TryReadAnyChunk(data, ref offset, extEnd, out var plgType, out var plgSize))
                    break;
                if (plgType == RW_SKIN_PLG)
                    skinData = RwDffDataSections.ParseSkinPlg(data, offset, (int)plgSize);
                offset += (int)plgSize;
            }
        }

        return new RwAtomic
        {
            FrameIndex = frameIndex, GeometryIndex = geomIndex,
            Flags = flags, SkinData = skinData
        };
    }

    private static RwGeometry EmptyGeometry()
    {
        return new RwGeometry
        {
            Flags = 0,
            Vertices = [],
            Normals = null,
            UVs = null,
            Colors = null,
            Triangles = [],
            Materials = [],
            BoundingSphere = Vector4.Zero
        };
    }
}
