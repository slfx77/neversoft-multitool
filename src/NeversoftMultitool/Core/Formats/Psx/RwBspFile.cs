using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

using static RwChunkReader;

/// <summary>
///     Parses RenderWare 3.x World (BSP) files for level geometry extraction.
///     Used for THPS3 PS2 level BSP files (version 0x0310).
///     Structure: World(0x0B) → MaterialList(0x08) + BSP tree of PlaneSection(0x0A) / AtomicSection(0x09).
/// </summary>
public static class RwBspFile
{
    // World format flags
    private const int WF_TEXTURED = 0x04;
    private const int WF_PRELIT = 0x08;
    private const int WF_NORMALS = 0x10;
    private const int WF_TEXTURED2 = 0x80;

    public static RwBspWorld Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static RwBspWorld Parse(byte[] data)
    {
        var offset = 0;
        var (type, size, _) = ReadChunkHeader(data, ref offset);
        if (type != RW_WORLD)
            throw new InvalidDataException($"Not an RW World (got 0x{type:X4})");

        var worldEnd = offset + (int)size;

        // World STRUCT: 52 bytes for version 0x0310
        if (!TryReadStruct(data, ref offset, worldEnd, out _, out var structSize))
            throw new InvalidDataException("Missing World Struct");

        var structStart = offset;
        // offset+4: invWorldOrigin (3 floats) — skip
        // offset+16: surfaceProperties (3 floats) — skip
        var numTriangles = BitConverter.ToInt32(data, structStart + 28);
        var numVertices = BitConverter.ToInt32(data, structStart + 32);
        // offset+36: numPlaneSectors, offset+40: numAtomicSectors, offset+44: colSectorSize — skip
        var formatFlags = BitConverter.ToInt32(data, structStart + 48);

        offset = structStart + (int)structSize;

        // Parse children: MaterialList + BSP tree
        RwMaterial[] materials = [];
        var sections = new List<RwBspSection>();

        while (offset < worldEnd && offset + 12 <= data.Length)
        {
            if (!TryReadAnyChunk(data, ref offset, worldEnd, out var childType, out var childSize))
                break;

            var childEnd = offset + (int)childSize;
            if (childEnd < offset || childEnd > data.Length) break;

            switch (childType)
            {
                case RW_MATERIAL_LIST:
                    materials = ParseMaterialList(data, ref offset, childEnd);
                    break;
                case RW_PLANE_SECTION:
                    CollectAtomicSections(data, offset, childEnd, formatFlags, sections);
                    break;
                case RW_ATOMIC_SECTION:
                    var section = ParseAtomicSection(data, offset, childEnd, formatFlags);
                    if (section != null)
                        sections.Add(section);
                    break;
            }

            offset = childEnd;
        }

        return new RwBspWorld
        {
            FormatFlags = formatFlags,
            TotalTriangles = numTriangles,
            TotalVertices = numVertices,
            Materials = materials,
            Sections = sections.ToArray()
        };
    }

    /// <summary>Check if data starts with RW_WORLD chunk type (0x000B).</summary>
    public static bool IsBspFile(byte[] data)
    {
        if (data.Length < 12) return false;
        return BitConverter.ToUInt32(data, 0) == RW_WORLD;
    }

    /// <summary>
    ///     Recursively walk the BSP tree (PlaneSection nodes) collecting AtomicSection leaves.
    /// </summary>
    private static void CollectAtomicSections(byte[] data, int offset, int endOffset,
        int formatFlags, List<RwBspSection> sections)
    {
        // PlaneSection STRUCT (24 bytes): sector type, value, leftIsAtomic, rightIsAtomic, leftValue, rightValue
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return;
        offset += (int)structSize;

        // Two children: left and right (each can be PlaneSection or AtomicSection)
        for (var i = 0; i < 2 && offset < endOffset; i++)
        {
            if (!TryReadAnyChunk(data, ref offset, endOffset, out var childType, out var childSize))
                break;

            var childEnd = offset + (int)childSize;
            if (childEnd < offset || childEnd > data.Length) break;

            switch (childType)
            {
                case RW_PLANE_SECTION:
                    CollectAtomicSections(data, offset, childEnd, formatFlags, sections);
                    break;
                case RW_ATOMIC_SECTION:
                    var section = ParseAtomicSection(data, offset, childEnd, formatFlags);
                    if (section != null)
                        sections.Add(section);
                    break;
            }

            offset = childEnd;
        }
    }

    /// <summary>
    ///     Parse an AtomicSection's STRUCT to extract geometry data.
    ///     Layout: header(44B: matBase+tris+verts+bbox+reserved) + positions(N×12) +
    ///     normals(N×4 if NORMALS) + colors(N×4 if PRELIT) + UV0(N×8) +
    ///     UV1(N×8 if TEXTURED2) + triangles(M×8).
    /// </summary>
    private static RwBspSection? ParseAtomicSection(byte[] data, int offset, int endOffset,
        int formatFlags)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out _))
            return null;

        var pos = offset;
        if (pos + 44 > data.Length) return null;

        var matListWindowBase = BitConverter.ToInt32(data, pos);
        var numTriangles = BitConverter.ToInt32(data, pos + 4);
        var numVertices = BitConverter.ToInt32(data, pos + 8);
        pos += 12;

        if (numVertices == 0) return null;
        if (numVertices < 0 || numVertices > 100000) return null;
        if (numTriangles < 0 || numTriangles > 200000) return null;

        // Bounding box (6 floats = 24 bytes) — skip
        pos += 24;

        // Reserved/unknown (8 bytes, always zero in THPS3) — skip
        pos += 8;

        // Positions: N × 12 bytes (3 floats)
        var vertices = new Vector3[numVertices];
        for (var i = 0; i < numVertices; i++)
        {
            if (pos + 12 > data.Length) return null;
            vertices[i] = new Vector3(
                BitConverter.ToSingle(data, pos),
                BitConverter.ToSingle(data, pos + 4),
                BitConverter.ToSingle(data, pos + 8));
            pos += 12;
        }

        // Normals: N × 4 bytes (packed: nx:i8, ny:i8, nz:i8, flags:u8)
        Vector3[]? normals = null;
        if ((formatFlags & WF_NORMALS) != 0)
        {
            normals = new Vector3[numVertices];
            for (var i = 0; i < numVertices; i++)
            {
                if (pos + 4 > data.Length) break;
                var nx = (sbyte)data[pos];
                var ny = (sbyte)data[pos + 1];
                var nz = (sbyte)data[pos + 2];
                // data[pos + 3] = flags/padding
                var n = new Vector3(nx / 127f, ny / 127f, nz / 127f);
                var len = n.Length();
                normals[i] = len > 0.001f ? n / len : Vector3.UnitY;
                pos += 4;
            }
        }

        // Prelit vertex colors: N × 4 bytes (RGBA)
        RwVertexColor[]? colors = null;
        if ((formatFlags & WF_PRELIT) != 0)
        {
            colors = new RwVertexColor[numVertices];
            for (var i = 0; i < numVertices; i++)
            {
                if (pos + 4 > data.Length) break;
                colors[i] = new RwVertexColor(data[pos], data[pos + 1], data[pos + 2], data[pos + 3]);
                pos += 4;
            }
        }

        // UV coordinates: N × 8 bytes per set (2 floats)
        var hasUV = (formatFlags & WF_TEXTURED) != 0 || (formatFlags & WF_TEXTURED2) != 0;
        Vector2[]? uvs = null;
        if (hasUV)
        {
            uvs = new Vector2[numVertices];
            for (var i = 0; i < numVertices; i++)
            {
                if (pos + 8 > data.Length) break;
                var u = BitConverter.ToSingle(data, pos);
                var v = BitConverter.ToSingle(data, pos + 4);
                uvs[i] = new Vector2(u, v);
                pos += 8;
            }

            // Skip second UV set (TEXTURED2 = lightmap UVs, not needed for glTF)
            if ((formatFlags & WF_TEXTURED2) != 0)
                pos += numVertices * 8;
        }

        // Triangles: M × 8 bytes (matId:u16, v0:u16, v1:u16, v2:u16)
        // THPS3 BSP: global material index first, then vertex indices.
        // Confirmed via hex analysis: field[0] values (17-117) match material indices,
        // fields[1-3] are always valid vertex indices (<numVertices).
        var triangles = new RwTriangle[numTriangles];
        for (var i = 0; i < numTriangles; i++)
        {
            if (pos + 8 > data.Length) break;
            var matId = BitConverter.ToUInt16(data, pos);
            var v0 = BitConverter.ToUInt16(data, pos + 2);
            var v1 = BitConverter.ToUInt16(data, pos + 4);
            var v2 = BitConverter.ToUInt16(data, pos + 6);
            triangles[i] = new RwTriangle(v0, v1, v2, matId);
            pos += 8;
        }

        return new RwBspSection
        {
            MatListWindowBase = matListWindowBase,
            Vertices = vertices,
            Normals = normals,
            Colors = colors,
            UVs = uvs,
            Triangles = triangles
        };
    }

    /// <summary>
    ///     Parse the shared MaterialList from the World chunk.
    ///     Reuses the same material/texture parsing logic as DFF files.
    /// </summary>
    private static RwMaterial[] ParseMaterialList(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return [];

        var numMaterials = BitConverter.ToInt32(data, offset);
        if (numMaterials <= 0 || numMaterials > 2000)
            return [];

        // Skip material indices array (numMaterials × i32, all -1 for World)
        offset += (int)structSize;

        var materials = new List<RwMaterial>(numMaterials);
        for (var i = 0; i < numMaterials && offset < endOffset; i++)
        {
            if (!TryReadAnyChunk(data, ref offset, endOffset, out var mType, out var mSize))
                break;

            var matEnd = offset + (int)mSize;
            if (matEnd > data.Length) break;

            if (mType == RW_MATERIAL)
                materials.Add(ParseMaterial(data, ref offset, matEnd));

            offset = matEnd;
        }

        return materials.ToArray();
    }

    private static RwMaterial ParseMaterial(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return DefaultMaterial();

        var pos = offset;
        pos += 4; // flags
        var r = data[pos];
        var g = data[pos + 1];
        var b = data[pos + 2];
        var a = data[pos + 3];
        pos += 4;
        pos += 4; // unused
        var isTextured = BitConverter.ToInt32(data, pos) != 0;
        pos += 4;
        var ambient = BitConverter.ToSingle(data, pos);
        pos += 4;
        var specular = BitConverter.ToSingle(data, pos);
        pos += 4;
        var diffuse = BitConverter.ToSingle(data, pos);

        offset += (int)structSize;

        string? textureName = null;
        string? maskName = null;

        if (isTextured)
        {
            while (offset < endOffset && offset + 12 <= data.Length)
            {
                if (!TryReadAnyChunk(data, ref offset, endOffset, out var childType, out var childSize))
                    break;

                var childEnd = offset + (int)childSize;
                if (childType == RW_TEXTURE)
                {
                    (textureName, maskName) = ParseTexture(data, ref offset, childEnd);
                    offset = childEnd;
                    break;
                }

                offset = childEnd;
            }
        }

        // Parse Extension chunk for Neversoft material plugin (blend mode)
        byte gsAlpha = 0;
        byte gsAlphaFix = 0;
        while (offset < endOffset && offset + 12 <= data.Length)
        {
            if (!TryReadAnyChunk(data, ref offset, endOffset, out var extType, out var extSize))
                break;

            var extEnd = offset + (int)extSize;
            if (extType == RW_EXTENSION)
            {
                // Walk extension children looking for NS material plugin
                var extOffset = offset;
                while (extOffset + 12 <= extEnd)
                {
                    if (!TryReadAnyChunk(data, ref extOffset, extEnd, out var plgType, out var plgSize))
                        break;

                    if (plgType == RW_NS_MATERIAL_PLG && plgSize >= 44)
                    {
                        // NS plugin header: word[9] byte 1 = GS ALPHA blend mode,
                        // word[10] byte 1 = FIX value
                        gsAlpha = data[extOffset + 37];
                        gsAlphaFix = data[extOffset + 41];
                    }

                    extOffset += (int)plgSize;
                }

                break;
            }

            offset = extEnd;
        }

        return new RwMaterial
        {
            R = r, G = g, B = b, A = a,
            TextureName = textureName,
            MaskName = maskName,
            Ambient = ambient,
            Specular = specular,
            Diffuse = diffuse,
            GsAlpha = gsAlpha,
            GsAlphaFix = gsAlphaFix
        };
    }

    private static (string? name, string? mask) ParseTexture(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return (null, null);
        offset += (int)structSize;

        string? name = null;
        if (TryReadChunk(data, ref offset, endOffset, RW_STRING, out var nameSize))
        {
            name = ReadNullTerminatedString(data, offset, (int)nameSize);
            offset += (int)nameSize;
        }

        string? mask = null;
        if (TryReadChunk(data, ref offset, endOffset, RW_STRING, out var maskSize))
        {
            mask = ReadNullTerminatedString(data, offset, (int)maskSize);
            if (string.IsNullOrEmpty(mask)) mask = null;
            offset += (int)maskSize;
        }

        return (name, mask);
    }

    private static RwMaterial DefaultMaterial() => new()
    {
        R = 255, G = 255, B = 255, A = 255,
        TextureName = null, MaskName = null
    };
}
