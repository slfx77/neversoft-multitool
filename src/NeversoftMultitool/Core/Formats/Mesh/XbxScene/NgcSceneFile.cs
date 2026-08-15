using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Parser for THAW GameCube scene files (.skin.ngc, .mdl.ngc, .scn.ngc).
///     All fields big-endian. The container follows THUG GC (Gfx/NGC/p_nx.cpp
///     s_plat_load_scene_guts + p_nxsector.cpp LoadFromFile) with a 64-byte
///     extended scene header (THUG's 32-byte sSceneHeader + a 32-byte THAW
///     extension carrying the 0xAAFFEEFF sentinel at +0x2C).
///     Geometry ships as GX display lists: CP loads set the vertex descriptor
///     (VCD_LO/VCD_HI), draw commands carry indexed vertex tuples. Index
///     arrays resolve into scene pools (positions f32, normals s16/16384,
///     colors RGBA8, UVs s16 where u=a/1024, v=1-b/1024) or, for skinned
///     objects, into the skin vertex list (positions s16/32 — THAW halved
///     THUG's 1.9.6 shift to double the range, normals s16/16384). Skin data
///     is single/double/add bone lists per NX/mesh.cpp ApplyMeshScaling and
///     NX/instance.cpp (doubles pack two palette indices as mtx &amp; 255 and
///     (mtx &gt;&gt; 8) &amp; 255; add lists accumulate a third bone onto existing verts).
///     Material passes reference textures by INDEX into the companion
///     .tex.ngc dictionary (record order); XbxPass.TextureChecksum stores
///     index+1 so 0 keeps meaning "untextured".
///     Validated against PC/PS2 Rosetta pairs (anl_pigeon, ped_baller):
///     positions/triangles exact, UVs and normals match the PS2 decode.
/// </summary>
public static class NgcSceneFile
{
    private const uint Sentinel = 0xAAFFEEFF;
    private const int HeaderSize = 64;
    private const int ObjectHeaderSize = 64;
    private const int DlHeaderSize = 64;
    public static readonly string[] SupportedExtensions = [".skin.ngc", ".mdl.ngc", ".scn.ngc"];

    public static bool IsNgcScene(byte[] data)
    {
        return data.Length >= HeaderSize
               && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x2C)) == Sentinel;
    }

    public static XbxScene Parse(byte[] data)
    {
        if (!IsNgcScene(data))
            throw new InvalidDataException("Not a THAW GameCube scene file (missing 0xAAFFEEFF sentinel)");

        var span = data.AsSpan();

        // sSceneHeader (THUG layout, first 32 bytes of the 64-byte header).
        var numPosRaw = BinaryPrimitives.ReadUInt32BigEndian(span);
        var numNrm = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x06..]);
        var numCol = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x08..]);
        var numTex = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x0A..]);
        var numPoolBytesRaw = BinaryPrimitives.ReadUInt32BigEndian(span[0x0C..]);
        var numObjects = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x10..]);
        var numMaterials = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x12..]);
        var numBlendDls = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x18..]);
        var numVcWibbles = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x1A..]);
        var numUvWibbles = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x1C..]);
        var numPassItems = (int)BinaryPrimitives.ReadUInt16BigEndian(span[0x1E..]);

        // sMaterialDL (8B) + sTextureDL (24B) tables, padded together to 32.
        // Validate their complete framing before reading individual entries.
        var tableBytes = (long)numBlendDls * 8 + (long)numMaterials * 24;
        var paddedTableBytes = tableBytes + (32 - tableBytes % 32) % 32;
        RequireRange(span.Length, HeaderSize, paddedTableBytes,
            "NGC scene material display-list tables");

        var offset = HeaderSize;
        long blendDlBytes = 0;
        for (var i = 0; i < numBlendDls; i++)
        {
            blendDlBytes += BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            offset += 8;
        }

        long textureDlBytes = 0;
        var materialTextured = new bool[numMaterials];
        for (var i = 0; i < numMaterials; i++)
        {
            textureDlBytes += BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            // m_tex_offset[0] >= 0 means the DL contains a texture address to patch.
            materialTextured[i] = BinaryPrimitives.ReadInt16BigEndian(span[(offset + 8)..]) >= 0;
            offset += 24;
        }

        offset = checked(HeaderSize + (int)paddedTableBytes);
        RequireRange(span.Length, offset, blendDlBytes + textureDlBytes,
            "NGC scene material display-list data");
        offset = checked(offset + (int)(blendDlBytes + textureDlBytes));

        var poolStart = offset;

        // The declared pool length can run a little past the physical end,
        // because it counts the region's own trailing alignment: 26 of the 1,612
        // THAW GC .mdl.ngc files overrun by 4-16 bytes, and every one of them
        // declares zero objects, so nothing ever addresses the region. Allow
        // less than one alignment unit of slack — beyond that a declared size is
        // real corruption, not padding — and bound the in-pool checks by what is
        // physically present, leaving each array's own RequireRange to catch a
        // pool genuinely truncated under content.
        const int poolAlignment = 32;
        var declaredPoolEnd = (long)poolStart + numPoolBytesRaw;
        if (declaredPoolEnd - span.Length >= poolAlignment)
        {
            throw new InvalidDataException(
                $"NGC scene pool size {numPoolBytesRaw} exceeds the " +
                $"{span.Length - poolStart} remaining bytes");
        }

        var poolEnd = (int)Math.Min(declaredPoolEnd, span.Length);

        // VC wibble keys precede the material headers inside the pool.
        for (var i = 0; i < numVcWibbles; i++)
        {
            RequireRange(poolEnd, offset, 8, $"NGC scene VC-wibble {i} header");
            var frames = BinaryPrimitives.ReadInt32BigEndian(span[offset..]);
            if (frames < 0)
                throw new InvalidDataException($"NGC scene VC-wibble {i} has negative frame count {frames}");

            var wibbleBytes = 8L + (long)frames * 8;
            RequireRange(poolEnd, offset, wibbleBytes, $"NGC scene VC-wibble {i}");
            offset += (int)wibbleBytes;
        }

        var fixedPoolBytes = (long)numMaterials * 32
                             + (long)numUvWibbles * 32
                             + (long)numPassItems * 32
                             + (long)numPosRaw * 12
                             + (long)numCol * 4
                             + (long)numTex * 4
                             + (long)numNrm * 6;
        RequireRange(poolEnd, offset, fixedPoolBytes, "NGC scene fixed pool arrays");

        var numPos = checked((int)numPosRaw);
        var materialOffsets = offset;
        offset += numMaterials * 32;
        offset += numUvWibbles * 32;
        var passOffsets = offset;
        offset += numPassItems * 32;

        var posPoolOffset = offset;
        offset += numPos * 12;
        var colPoolOffset = offset;
        offset += numCol * 4;
        var texPoolOffset = offset;
        offset += numTex * 4;
        var nrmPoolOffset = offset;

        var pools = new PoolLayout(
            posPoolOffset, numPos,
            colPoolOffset, numCol,
            texPoolOffset, numTex,
            nrmPoolOffset, numNrm);

        var materials = ReadMaterials(
            span, materialOffsets, numMaterials, passOffsets, numPassItems, materialTextured);

        // Objects follow the DECLARED pool region. A scene with no objects never
        // addresses it, so only require it to be in bounds when there is
        // something to read there.
        var sectors = new List<XbxSector>(numObjects);
        if (numObjects > 0)
        {
            RequireRange(span.Length, (int)declaredPoolEnd, (long)numObjects * ObjectHeaderSize,
                "NGC scene object headers");
            offset = (int)declaredPoolEnd;
            for (var obj = 0; obj < numObjects; obj++)
                offset = ReadObject(span, offset, obj, pools, sectors);
        }
        else
        {
            offset = poolEnd;
        }

        var links = ReadHierarchy(span, offset);

        return new XbxScene
        {
            Materials = materials,
            Sectors = sectors.ToArray(),
            Links = links
        };
    }

    private static XbxMaterial[] ReadMaterials(
        ReadOnlySpan<byte> span,
        int materialOffset,
        int numMaterials,
        int passOffset,
        int numPassItems,
        bool[] materialTextured)
    {
        var materials = new XbxMaterial[numMaterials];
        for (var i = 0; i < numMaterials; i++)
        {
            var mo = materialOffset + i * 32;
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(span[mo..]);
            var numPasses = span[mo + 4];
            var alphaCutoff = (int)span[mo + 5];
            var drawOrder = BinaryPrimitives.ReadSingleBigEndian(span[(mo + 8)..]);
            var passItem = (int)BinaryPrimitives.ReadUInt16BigEndian(span[(mo + 12)..]);
            var nameChecksum = BinaryPrimitives.ReadUInt32BigEndian(span[(mo + 24)..]);

            // Runtime doubles the stored cutoff (>=128 saturates to 255).
            alphaCutoff = alphaCutoff >= 128 ? 255 : alphaCutoff * 2;

            var passes = new List<XbxPass>(numPasses);
            for (var p = 0; p < numPasses && passItem + p < numPassItems; p++)
            {
                var po = passOffset + (passItem + p) * 32;
                var textureIndex = BinaryPrimitives.ReadUInt32BigEndian(span[po..]);
                var blendMode = (uint)span[po + 6];
                var textured = p == 0 ? materialTextured[i] : textureIndex != 0;
                passes.Add(new XbxPass
                {
                    // Texture INDEX into the companion .tex.ngc, +1 so 0 = none.
                    TextureChecksum = textured ? textureIndex + 1 : 0,
                    BlendMode = blendMode
                });
            }

            materials[i] = new XbxMaterial
            {
                Checksum = checksum,
                NameChecksum = nameChecksum,
                NumPasses = numPasses,
                AlphaCutoff = alphaCutoff,
                DrawOrder = drawOrder,
                Passes = passes.ToArray()
            };
        }

        return materials;
    }

    private static int ReadObject(
        ReadOnlySpan<byte> span,
        int offset,
        int objectIndex,
        PoolLayout pools,
        List<XbxSector> sectors)
    {
        RequireRange(span.Length, offset, ObjectHeaderSize,
            $"NGC scene object {objectIndex} header");
        var numMeshes = (int)BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
        var skinBytesRaw = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
        var numSkinVerts = (int)BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 8)..]);
        var numDoubleLists = (int)BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 10)..]);
        var numSingleLists = (int)span[offset + 12];
        var numAddLists = (int)span[offset + 13];
        var boneIndex = BinaryPrimitives.ReadInt16BigEndian(span[(offset + 14)..]);
        var sphere = ReadVector4(span, offset + 48);

        var skinStart = offset + ObjectHeaderSize;
        RequireRange(span.Length, skinStart, skinBytesRaw,
            $"NGC scene object {objectIndex} skin data");
        var skinBytes = (int)skinBytesRaw;
        var skinEnd = skinStart + skinBytes;
        var skinVerts = ParseSkin(
            span, skinStart, skinEnd,
            numSingleLists, numDoubleLists, numAddLists, numSkinVerts,
            objectIndex);

        offset = skinEnd;

        var meshes = new List<XbxMesh>(numMeshes);
        uint sectorChecksum = 0;
        for (var m = 0; m < numMeshes; m++)
        {
            RequireRange(span.Length, offset, DlHeaderSize,
                $"NGC scene object {objectIndex} mesh {m} header");
            var dlSizeRaw = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            var materialChecksum = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            var flags = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 8)..]);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 12)..]);
            var meshSphere = ReadVector4(span, offset + 16);
            if (sectorChecksum == 0)
                sectorChecksum = checksum;

            var dlStart = offset + DlHeaderSize;
            RequireRange(span.Length, dlStart, dlSizeRaw,
                $"NGC scene object {objectIndex} mesh {m} display list");
            var dlSize = (int)dlSizeRaw;
            if (dlSize > 0)
            {
                var mesh = ParseMeshDisplayList(
                    span, dlStart, dlSize, skinVerts, pools,
                    materialChecksum, flags, meshSphere,
                    $"NGC scene object {objectIndex} mesh {m} display list");
                if (mesh != null)
                    meshes.Add(mesh);
            }

            offset = dlStart + dlSize;
        }

        var sectorFlags = 0;
        foreach (var vertices in meshes.Select(static mesh => mesh.Vertices))
        {
            if (vertices.Length == 0) continue;
            if (vertices[0].HasNormal) sectorFlags |= 0x04;
            if (vertices[0].HasColor) sectorFlags |= 0x02;
            sectorFlags |= 0x01;
        }

        if (skinVerts.Length > 0)
            sectorFlags |= 0x10;

        sectors.Add(new XbxSector
        {
            Checksum = sectorChecksum,
            BoneIndex = boneIndex,
            Flags = sectorFlags,
            BsphereCenter = new Vector3(sphere.X, sphere.Y, sphere.Z),
            BsphereRadius = sphere.W,
            Meshes = meshes.ToArray()
        });

        return offset;
    }

    /// <summary>
    ///     Skin vertex lists per NX/mesh.cpp: singles {n, mtx, n×6s16}, doubles
    ///     {n, mtx0|mtx1&lt;&lt;8, n×6s16, n×2s16 weights}, adds {n, mtx, n×6s16,
    ///     n×s16 weights, n×u16 target indices} (accumulate onto existing verts).
    ///     Positions s16/32, normals s16/16384, weights s16/16384.
    /// </summary>
    private static SkinVertex[] ParseSkin(
        ReadOnlySpan<byte> span,
        int offset,
        int end,
        int numSingleLists,
        int numDoubleLists,
        int numAddLists,
        int numSkinVerts,
        int objectIndex)
    {
        var verts = new List<SkinVertex>(numSkinVerts);

        for (var list = 0; list < numSingleLists; list++)
        {
            RequireRange(end, offset, 8,
                $"NGC scene object {objectIndex} single-skin list {list} header");
            var countRaw = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            var mtx = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            offset += 8;
            var recordBytes = (long)countRaw * 12;
            RequireRange(end, offset, recordBytes,
                $"NGC scene object {objectIndex} single-skin list {list} vertices");
            var count = (int)countRaw;
            for (var i = 0; i < count; i++)
            {
                var (pos, nrm) = ReadSkinPosNormal(span, offset + i * 12);
                verts.Add(new SkinVertex(pos, nrm, mtx, 0, 1f, 0f, 0, 0f));
            }

            offset += (int)recordBytes;
        }

        for (var list = 0; list < numDoubleLists; list++)
        {
            RequireRange(end, offset, 8,
                $"NGC scene object {objectIndex} double-skin list {list} header");
            var countRaw = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            var mtx = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            offset += 8;
            var recordBytes = (long)countRaw * 16;
            RequireRange(end, offset, recordBytes,
                $"NGC scene object {objectIndex} double-skin list {list} vertices and weights");
            var count = (int)countRaw;
            var weightOffset = offset + count * 12;
            for (var i = 0; i < count; i++)
            {
                var (pos, nrm) = ReadSkinPosNormal(span, offset + i * 12);
                var w0 = BinaryPrimitives.ReadInt16BigEndian(span[(weightOffset + i * 4)..]) / 16384f;
                var w1 = BinaryPrimitives.ReadInt16BigEndian(span[(weightOffset + i * 4 + 2)..]) / 16384f;
                verts.Add(new SkinVertex(pos, nrm, mtx & 255, (mtx >> 8) & 255, w0, w1, 0, 0f));
            }

            offset += (int)recordBytes;
        }

        var result = verts.ToArray();

        for (var list = 0; list < numAddLists; list++)
        {
            RequireRange(end, offset, 8,
                $"NGC scene object {objectIndex} add-skin list {list} header");
            var countRaw = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            var mtx = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            offset += 8;
            var recordBytes = (long)countRaw * 16;
            RequireRange(end, offset, recordBytes,
                $"NGC scene object {objectIndex} add-skin list {list} vertices, weights, and indices");
            var count = (int)countRaw;
            var weightOffset = offset + count * 12;
            var indexOffset = weightOffset + count * 2;
            for (var i = 0; i < count; i++)
            {
                var weight = BinaryPrimitives.ReadInt16BigEndian(span[(weightOffset + i * 2)..]) / 16384f;
                var target = (int)BinaryPrimitives.ReadUInt16BigEndian(span[(indexOffset + i * 2)..]);
                if (target < result.Length)
                    result[target] = result[target] with { Bone2 = mtx, Weight2 = weight };
            }

            offset += (int)recordBytes;
        }

        return result;
    }

    private static (Vector3 Pos, Vector3 Normal) ReadSkinPosNormal(ReadOnlySpan<byte> span, int offset)
    {
        var pos = new Vector3(
            BinaryPrimitives.ReadInt16BigEndian(span[offset..]) / 32f,
            BinaryPrimitives.ReadInt16BigEndian(span[(offset + 2)..]) / 32f,
            BinaryPrimitives.ReadInt16BigEndian(span[(offset + 4)..]) / 32f);
        var nrm = new Vector3(
            BinaryPrimitives.ReadInt16BigEndian(span[(offset + 6)..]) / 16384f,
            BinaryPrimitives.ReadInt16BigEndian(span[(offset + 8)..]) / 16384f,
            BinaryPrimitives.ReadInt16BigEndian(span[(offset + 10)..]) / 16384f);
        return (pos, nrm);
    }

    private static VertexDescriptor DecodeVcd(uint vcdLo, uint vcdHi, int dlOffset)
    {
        var matrixBytes = 0;
        if ((vcdLo & 1) != 0)
            matrixBytes++;
        for (var t = 0; t < 8; t++)
        {
            if (((vcdLo >> (1 + t)) & 1) != 0)
                matrixBytes++;
        }

        var extraTexBytes = 0;
        for (var t = 1; t < 8; t++)
        {
            var mode = DecodeAttrMode((vcdHi >> (2 * t)) & 3, $"tex{t}", dlOffset);
            extraTexBytes += mode switch
            {
                AttrMode.Index8 => 1,
                AttrMode.Index16 => 2,
                _ => 0
            };
        }

        return new VertexDescriptor(
            matrixBytes,
            DecodeAttrMode((vcdLo >> 9) & 3, "position", dlOffset),
            DecodeAttrMode((vcdLo >> 11) & 3, "normal", dlOffset),
            DecodeAttrMode((vcdLo >> 13) & 3, "color0", dlOffset),
            DecodeAttrMode((vcdLo >> 15) & 3, "color1", dlOffset),
            DecodeAttrMode(vcdHi & 3, "tex0", dlOffset),
            extraTexBytes);
    }

    private static AttrMode DecodeAttrMode(uint value, string attribute, int dlOffset)
    {
        return value switch
        {
            0 => AttrMode.None,
            2 => AttrMode.Index8,
            3 => AttrMode.Index16,
            _ => throw new InvalidDataException(
                $"Direct (non-indexed) {attribute} attribute in GX display list at 0x{dlOffset:X}")
        };
    }

    private static XbxMesh? ParseMeshDisplayList(
        ReadOnlySpan<byte> span,
        int dlStart,
        int dlSize,
        SkinVertex[] skinVerts,
        PoolLayout pools,
        uint materialChecksum,
        uint flags,
        Vector4 meshSphere,
        string context)
    {
        var skinned = skinVerts.Length > 0;
        var vertices = new List<XbxVertex>();
        var faces = new List<ushort>();
        var tupleMap = new Dictionary<(int Pos, int Nrm, int Col, int Tex), ushort>();

        var offset = dlStart;
        var end = dlStart + dlSize;
        uint vcdLo = 0, vcdHi = 0;

        while (offset < end)
        {
            var op = span[offset];
            switch (op)
            {
                case 0x00: // NOP / padding
                    offset++;
                    continue;
                case 0x08: // CP register load
                    RequireRange(end, offset, 6, $"{context} CP command");
                    var reg = span[offset + 1];
                    var value = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 2)..]);
                    if (reg == 0x50) vcdLo = value;
                    else if (reg == 0x60) vcdHi = value;
                    offset += 6;
                    continue;
                case 0x10: // XF register load: header packs (count-1) << 16 | address
                    RequireRange(end, offset, 5, $"{context} XF command header");
                    var xfHeader = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 1)..]);
                    var xfCount = (int)((xfHeader >> 16) & 0xFFFF) + 1;
                    RequireRange(end, offset, 5L + (long)xfCount * 4,
                        $"{context} XF command");
                    offset += 5 + xfCount * 4;
                    continue;
                case 0x61: // BP register load
                    RequireRange(end, offset, 5, $"{context} BP command");
                    offset += 5;
                    continue;
            }

            var drawOp = op & 0xF8;
            if (drawOp is not (0x80 or 0x90 or 0x98 or 0xA0))
                throw new InvalidDataException($"Unknown GX display list opcode 0x{op:X2} at 0x{offset:X}");

            RequireRange(end, offset, 3, $"{context} draw header");
            var descriptor = DecodeVcd(vcdLo, vcdHi, offset);
            var count = (int)BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 1)..]);
            offset += 3;
            RequireRange(end, offset, (long)count * descriptor.Stride,
                $"{context} draw tuples");

            var primIndices = new ushort[count];
            for (var v = 0; v < count; v++)
            {
                var vo = offset + descriptor.MatrixIndexBytes;
                var posIdx = ReadAttrIndex(span, ref vo, descriptor.Position);
                var nrmIdx = ReadAttrIndex(span, ref vo, descriptor.Normal);
                var colIdx = ReadAttrIndex(span, ref vo, descriptor.Color0);
                ReadAttrIndex(span, ref vo, descriptor.Color1);
                var texIdx = ReadAttrIndex(span, ref vo, descriptor.Tex0);

                var key = (posIdx, nrmIdx, colIdx, texIdx);
                if (!tupleMap.TryGetValue(key, out var vertexIndex))
                {
                    vertexIndex = (ushort)vertices.Count;
                    vertices.Add(BuildVertex(
                        span, skinned, skinVerts,
                        key, descriptor, pools));
                    tupleMap[key] = vertexIndex;
                }

                primIndices[v] = vertexIndex;
                offset += descriptor.Stride;
            }

            EmitTriangles(faces, primIndices, drawOp);
        }

        if (faces.Count < 3)
            return null;

        return new XbxMesh
        {
            BsphereCenter = new Vector3(meshSphere.X, meshSphere.Y, meshSphere.Z),
            BsphereRadius = meshSphere.W,
            MeshFlags = flags,
            MaterialChecksum = materialChecksum,
            Vertices = vertices.ToArray(),
            FaceIndices = faces.ToArray(),
            IsPreTriangulated = true
        };
    }

    private static int ReadAttrIndex(ReadOnlySpan<byte> span, ref int offset, AttrMode mode)
    {
        switch (mode)
        {
            case AttrMode.Index8:
                return span[offset++];
            case AttrMode.Index16:
                var value = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
                offset += 2;
                return value;
            default:
                return -1;
        }
    }

    private static XbxVertex BuildVertex(
        ReadOnlySpan<byte> span,
        bool skinned,
        SkinVertex[] skinVerts,
        (int Pos, int Nrm, int Col, int Tex) indices,
        VertexDescriptor descriptor,
        PoolLayout pools)
    {
        var vertex = new XbxVertex { Color = Vector4.One };
        var (posIdx, nrmIdx, colIdx, texIdx) = indices;

        if (skinned && posIdx >= 0 && posIdx < skinVerts.Length)
        {
            var sv = skinVerts[posIdx];
            vertex.Position = sv.Position;
            vertex.HasSkinData = true;
            vertex.BoneIndex0 = sv.Bone0;
            vertex.BoneIndex1 = sv.Bone1;
            vertex.BoneIndex2 = sv.Bone2;
            vertex.BoneWeight0 = sv.Weight0;
            vertex.BoneWeight1 = sv.Weight1;
            vertex.BoneWeight2 = sv.Weight2;
            if (descriptor.Normal != AttrMode.None && nrmIdx >= 0 && nrmIdx < skinVerts.Length)
            {
                vertex.Normal = skinVerts[nrmIdx].Normal;
                vertex.HasNormal = true;
            }
        }
        else if (posIdx >= 0 && posIdx < pools.NumPos)
        {
            vertex.Position = new Vector3(
                BinaryPrimitives.ReadSingleBigEndian(span[(pools.PosOffset + posIdx * 12)..]),
                BinaryPrimitives.ReadSingleBigEndian(span[(pools.PosOffset + posIdx * 12 + 4)..]),
                BinaryPrimitives.ReadSingleBigEndian(span[(pools.PosOffset + posIdx * 12 + 8)..]));
            if (descriptor.Normal != AttrMode.None && nrmIdx >= 0 && nrmIdx < pools.NumNrm)
            {
                vertex.Normal = new Vector3(
                    BinaryPrimitives.ReadInt16BigEndian(span[(pools.NrmOffset + nrmIdx * 6)..]) / 16384f,
                    BinaryPrimitives.ReadInt16BigEndian(span[(pools.NrmOffset + nrmIdx * 6 + 2)..]) / 16384f,
                    BinaryPrimitives.ReadInt16BigEndian(span[(pools.NrmOffset + nrmIdx * 6 + 4)..]) / 16384f);
                vertex.HasNormal = true;
            }
        }

        if (descriptor.Color0 != AttrMode.None && colIdx >= 0 && colIdx < pools.NumCol)
        {
            var co = pools.ColOffset + colIdx * 4;
            vertex.Color = new Vector4(
                span[co] / 128f,
                span[co + 1] / 128f,
                span[co + 2] / 128f,
                span[co + 3] / 128f);
            vertex.HasColor = true;
        }

        if (descriptor.Tex0 != AttrMode.None && texIdx >= 0 && texIdx < pools.NumTex)
        {
            var to = pools.TexOffset + texIdx * 4;
            vertex.TexCoord = new Vector2(
                BinaryPrimitives.ReadInt16BigEndian(span[to..]) / 1024f,
                1f - BinaryPrimitives.ReadInt16BigEndian(span[(to + 2)..]) / 1024f);
        }

        return vertex;
    }

    private static void EmitTriangles(List<ushort> faces, ushort[] indices, int drawOp)
    {
        switch (drawOp)
        {
            case 0x90: // triangles
                for (var i = 0; i + 2 < indices.Length; i += 3)
                    AddTriangle(faces, indices[i], indices[i + 1], indices[i + 2]);
                break;
            case 0x98: // triangle strip
                for (var i = 2; i < indices.Length; i++)
                {
                    if (i % 2 == 0)
                        AddTriangle(faces, indices[i - 2], indices[i - 1], indices[i]);
                    else
                        AddTriangle(faces, indices[i - 1], indices[i - 2], indices[i]);
                }

                break;
            case 0xA0: // triangle fan
                for (var i = 2; i < indices.Length; i++)
                    AddTriangle(faces, indices[0], indices[i - 1], indices[i]);
                break;
            case 0x80: // quads
                for (var i = 0; i + 3 < indices.Length; i += 4)
                {
                    AddTriangle(faces, indices[i], indices[i + 1], indices[i + 2]);
                    AddTriangle(faces, indices[i], indices[i + 2], indices[i + 3]);
                }

                break;
        }
    }

    private static void AddTriangle(List<ushort> faces, ushort a, ushort b, ushort c)
    {
        if (a == b || b == c || a == c)
            return;
        faces.Add(a);
        faces.Add(b);
        faces.Add(c);
    }

    /// <summary>
    ///     Hierarchy tail: u32 count + 80-byte CHierarchyObject entries
    ///     {checksum, parent checksum, parent index i16, pad, 4x4 f32 matrix at +16}.
    ///     Any CAS footer after the hierarchy is ignored.
    /// </summary>
    private static XbxLink[] ReadHierarchy(ReadOnlySpan<byte> span, int offset)
    {
        if (offset + 4 > span.Length)
            return [];

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
        offset += 4;
        if (count <= 0 || count > 4096 || offset + count * 80 > span.Length)
            return [];

        var links = new XbxLink[count];
        for (var i = 0; i < count; i++)
        {
            var lo = offset + i * 80;
            var matrix = Matrix4x4.Identity;
            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    matrix[row, col] = BinaryPrimitives.ReadSingleBigEndian(
                        span[(lo + 16 + (row * 4 + col) * 4)..]);
                }
            }

            links[i] = new XbxLink
            {
                SectorChecksum = BinaryPrimitives.ReadUInt32BigEndian(span[lo..]),
                ParentChecksum = BinaryPrimitives.ReadUInt32BigEndian(span[(lo + 4)..]),
                Index = (ushort)i,
                Transform = matrix
            };
        }

        return links;
    }

    private static Vector4 ReadVector4(ReadOnlySpan<byte> span, int offset)
    {
        return new Vector4(
            BinaryPrimitives.ReadSingleBigEndian(span[offset..]),
            BinaryPrimitives.ReadSingleBigEndian(span[(offset + 4)..]),
            BinaryPrimitives.ReadSingleBigEndian(span[(offset + 8)..]),
            BinaryPrimitives.ReadSingleBigEndian(span[(offset + 12)..]));
    }

    private static void RequireRange(int end, int offset, long size, string context)
    {
        if (offset < 0 || size < 0 || offset > end || size > end - offset)
        {
            throw new InvalidDataException(
                $"{context} overruns its containing region at 0x{offset:X} " +
                $"(need {size} bytes, end 0x{end:X})");
        }
    }

    private readonly record struct PoolLayout(
        int PosOffset,
        int NumPos,
        int ColOffset,
        int NumCol,
        int TexOffset,
        int NumTex,
        int NrmOffset,
        int NumNrm);

    private readonly record struct SkinVertex(
        Vector3 Position,
        Vector3 Normal,
        int Bone0,
        int Bone1,
        float Weight0,
        float Weight1,
        int Bone2,
        float Weight2);

    private enum AttrMode
    {
        None,
        Index8,
        Index16
    }

    private readonly record struct VertexDescriptor(
        int MatrixIndexBytes,
        AttrMode Position,
        AttrMode Normal,
        AttrMode Color0,
        AttrMode Color1,
        AttrMode Tex0,
        int ExtraTexBytes)
    {
        public int Stride => MatrixIndexBytes
                             + Size(Position) + Size(Normal) + Size(Color0) + Size(Color1)
                             + Size(Tex0) + ExtraTexBytes;

        private static int Size(AttrMode mode)
        {
            return mode switch
            {
                AttrMode.Index8 => 1,
                AttrMode.Index16 => 2,
                _ => 0
            };
        }
    }
}
