using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parser for Xbox/PC scene files (.skin.xbx, .mdl.xbx, .skin.wpc, .mdl.wpc).
///     THUG2 format: per-mesh interleaved vertex buffers (not per-sector flat arrays).
///     Format from nxtools fmt_thscene_import.py + THUG source material.cpp.
///     Validated against THUG2 Xbox/PC sample data: 925 files (751 SKIN + 174 MDL), 0 failures.
/// </summary>
public static class XbxSceneFile
{
    // Sector flags (from nxtools constants.py)
    private const int SECFLAG_HAS_COLORS = 1 << 1;
    private const int SECFLAG_HAS_NORMALS = 1 << 2;
    private const int SECFLAG_HAS_WEIGHTS = 1 << 4;
    private const int SECFLAG_BILLBOARD = 0x00800000;

    public static readonly string[] SupportedExtensions =
        [".skin.xbx", ".mdl.xbx", ".skin.wpc", ".mdl.wpc"];

    /// <summary>
    ///     Quick probe: version triple must be (1,1,1).
    /// </summary>
    public static bool IsXbxScene(byte[] data)
    {
        if (data.Length < 12) return false;
        var v0 = BitConverter.ToUInt32(data, 0);
        var v1 = BitConverter.ToUInt32(data, 4);
        var v2 = BitConverter.ToUInt32(data, 8);
        return v0 == 1 && v1 == 1 && v2 == 1;
    }

    public static XbxScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static XbxScene Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        // Version triple (always 1,1,1 for Xbox/PC THUG2)
        var matVersion = r.ReadUInt32();
        var meshVersion = r.ReadUInt32();
        var vertVersion = r.ReadUInt32();

        if (matVersion != 1 || meshVersion != 1 || vertVersion != 1)
            throw new InvalidDataException(
                $"Unexpected Xbox scene version ({matVersion},{meshVersion},{vertVersion}), expected (1,1,1)");

        // Materials (per material.cpp LoadMaterialsFromMemory)
        var numMaterials = r.ReadInt32();
        var materials = new XbxMaterial[numMaterials];
        for (var i = 0; i < numMaterials; i++)
            materials[i] = XbxSceneMaterialReader.ReadMaterial(r);

        // CScene: sector_count then sectors
        var numSectors = r.ReadInt32();
        var sectors = new XbxSector[numSectors];
        for (var i = 0; i < numSectors; i++)
            sectors[i] = ReadSector(r, materials);

        // Hierarchy links (present in MDL files with multi-part objects)
        var links = ReadLinks(r);

        return new XbxScene { Materials = materials, Sectors = sectors, Links = links };
    }
    // ── Sectors (CGeom + per-mesh vertex data) ────────────────────────────

    /// <summary>
    ///     Read a sector: header + CGeom (bounding volumes + per-mesh data).
    ///     Layout from nxtools ReadCSectors + ReadCGeom.
    /// </summary>
    private static XbxSector ReadSector(BinaryReader r, XbxMaterial[] materials)
    {
        var checksum = r.ReadUInt32();
        var boneIndex = r.ReadInt32();
        var flags = r.ReadInt32();

        // CGeom: num_meshes + bounding volumes
        var numMeshes = r.ReadInt32();
        var bboxMin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var bboxMax = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var bsphereCenter = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var bsphereRadius = r.ReadSingle();

        // Billboard data (if flags & 0x00800000)
        if ((flags & SECFLAG_BILLBOARD) != 0)
        {
            r.ReadUInt32(); // billboard_type
            r.BaseStream.Position += 36; // origin(3f) + pos(3f) + axis(3f)
        }

        // Per-mesh: ReadSMesh + ReadSMeshLOD with per-mesh vertex buffers
        var meshes = new XbxMesh[numMeshes];
        for (var m = 0; m < numMeshes; m++)
            meshes[m] = ReadMesh(r, flags, materials);

        return new XbxSector
        {
            Checksum = checksum,
            BoneIndex = boneIndex,
            Flags = flags,
            BboxMin = bboxMin,
            BboxMax = bboxMax,
            BsphereCenter = bsphereCenter,
            BsphereRadius = bsphereRadius,
            Meshes = meshes
        };
    }

    /// <summary>
    ///     Read a single mesh: bounding data + material + LOD levels.
    ///     Layout from nxtools ReadSMesh.
    /// </summary>
    private static XbxMesh ReadMesh(BinaryReader r, int sectorFlags, XbxMaterial[] materials)
    {
        // Bounding sphere: center(3f) + radius(f)
        var sphereCenter = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var sphereRadius = r.ReadSingle();

        // Bounding box: min(3f) + max(3f)
        var bboxMin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var bboxMax = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        var meshFlags = r.ReadUInt32();
        var materialChecksum = r.ReadUInt32();
        var lodCount = r.ReadInt32();

        // Find pass count for this mesh's material (affects UV count in vertex data)
        var passCount = GetPassCount(materials, materialChecksum);

        XbxVertex[] vertices = [];
        ushort[] faceIndices = [];

        for (var lod = 0; lod < lodCount; lod++)
        {
            var (lodVerts, lodFaces) = ReadMeshLod(r, sectorFlags, passCount, lod);
            if (lod == 0)
            {
                vertices = lodVerts;
                faceIndices = lodFaces;
            }
        }

        return new XbxMesh
        {
            BsphereCenter = sphereCenter,
            BsphereRadius = sphereRadius,
            BboxMin = bboxMin,
            BboxMax = bboxMax,
            MeshFlags = meshFlags,
            MaterialChecksum = materialChecksum,
            Vertices = vertices,
            FaceIndices = faceIndices
        };
    }

    /// <summary>
    ///     Read a single LOD level: indices + vertex buffer + trailing metadata.
    ///     Layout from nxtools ReadSMeshLOD.
    /// </summary>
    private static (XbxVertex[] vertices, ushort[] faces) ReadMeshLod(
        BinaryReader r, int sectorFlags, int passCount, int lodIndex)
    {
        // Global vertex indices
        var numGlobalIndices = r.ReadInt32();
        r.BaseStream.Position += numGlobalIndices * 2; // u16 each

        // LOD 0: local face indices (used for triangle strip)
        ushort[] faceIndices;
        if (lodIndex == 0)
        {
            var numFaces = r.ReadUInt16();
            faceIndices = ReadUInt16Array(r, numFaces);
        }
        else
        {
            var numLocalB = r.ReadUInt16();
            r.BaseStream.Position += numLocalB * 2;
            faceIndices = [];
        }

        // 14 unknown bytes
        r.BaseStream.Position += 14;

        var vertexStride = r.ReadByte();
        var vertexCount = r.ReadUInt16();
        var vertexBufCount = r.ReadByte();
        var vertexCsCount = r.ReadByte();

        // Read vertex buffers — we only decode buffer 0 of LOD 0
        XbxVertex[] vertices = [];
        for (var vb = 0; vb < vertexBufCount; vb++)
        {
            if (vb > 0)
                r.ReadByte(); // pad byte between buffers

            var blockBytes = r.ReadInt32();

            if (vb == 0 && lodIndex == 0)
            {
                var bufStart = r.BaseStream.Position;
                vertices = ReadVertexBuffer(r, sectorFlags, vertexCount, vertexStride, passCount);
                r.BaseStream.Position = bufStart + blockBytes;
            }
            else
            {
                r.BaseStream.Position += blockBytes;
            }
        }

        // CS objects (shadow volumes / collision?) — skip
        SkipCsObjects(r, vertexCsCount);

        // Trailing LOD metadata
        ReadLodTrailer(r, vertexCount);

        return (vertices, faceIndices);
    }

    /// <summary>
    ///     Decode interleaved vertex buffer based on sector flags.
    ///     Skinned vertices preserve packed weight/index data when present.
    ///     Non-skinned: pos(3f) + [normal(3f)] + [color(4B)] + UVs.
    /// </summary>
    private static XbxVertex[] ReadVertexBuffer(
        BinaryReader r, int sectorFlags, int vertexCount, int vertexStride, int passCount)
    {
        var vertices = new XbxVertex[vertexCount];
        var isSkinned = (sectorFlags & SECFLAG_HAS_WEIGHTS) != 0;
        var hasNormals = (sectorFlags & SECFLAG_HAS_NORMALS) != 0;
        var hasColors = (sectorFlags & SECFLAG_HAS_COLORS) != 0;

        for (var i = 0; i < vertexCount; i++)
        {
            var vertStart = r.BaseStream.Position;
            var v = new XbxVertex();

            // Position (always present)
            v.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            if (isSkinned)
            {
                XbxSkinVertexCodec.ReadSkinningData(r, ref v);

                if (hasNormals)
                {
                    var packed = r.ReadUInt32();
                    v.Normal = UnpackNormal(packed);
                    v.HasNormal = true;
                }
            }
            else
            {
                if (hasNormals)
                {
                    v.Normal = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    v.HasNormal = true;
                }
            }

            if (hasColors)
            {
                var b = r.ReadByte();
                var g = r.ReadByte();
                var red = r.ReadByte();
                var a = r.ReadByte();
                v.Color = new Vector4(red / 128f, g / 128f, b / 128f, a / 128f);
                v.HasColor = true;
            }

            // UVs — one set per material pass, we keep only the first
            if (passCount > 0)
            {
                var u = r.ReadSingle();
                var vCoord = r.ReadSingle();
                v.TexCoord = new Vector2(u, vCoord);
            }

            vertices[i] = v;

            // Advance to next vertex using stride (skips extra UV sets etc.)
            r.BaseStream.Position = vertStart + vertexStride;
        }

        return vertices;
    }

    /// <summary>Unpack THUG2-style packed normal from u32 (same as PS2 format).</summary>
    private static Vector3 UnpackNormal(uint packed)
    {
        // 11-bit X, 11-bit Y, 10-bit Z (signed)
        var ix = (int)(packed & 0x7FF);
        if ((packed & 0x400) != 0) ix -= 0x800;
        var iy = (int)((packed >> 11) & 0x7FF);
        if ((packed & (0x400 << 11)) != 0) iy -= 0x800;
        var iz = (int)((packed >> 22) & 0x3FF);
        if ((packed & (0x200 << 22)) != 0) iz -= 0x400;

        var nx = ix / 1023f;
        var ny = iy / 1023f;
        var nz = iz / 511f;
        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 0)
        {
            nx /= len;
            ny /= len;
            nz /= len;
        }

        return new Vector3(nx, ny, nz);
    }

    /// <summary>Skip CS (shadow/collision) objects. Each entry = 132 bytes.</summary>
    private static void SkipCsObjects(BinaryReader r, int csCount)
    {
        for (var cs = 0; cs < csCount; cs++)
        {
            var csVertCount = r.ReadInt32();
            // vec3f(12) + f(4) + f(4) + 6×vec4f(96) + 3×u32(12) + i32(4) = 132 bytes each
            r.BaseStream.Position += csVertCount * 132;
        }
    }

    /// <summary>Read trailing LOD metadata: flags + vc_wibble + pixel_shader.</summary>
    private static void ReadLodTrailer(BinaryReader r, int vertexCount)
    {
        r.ReadUInt32(); // lod_flags_a
        r.ReadUInt32(); // lod_flags_b
        r.BaseStream.Position += 3; // unknown 3 bytes
        var hasVcWibble = r.ReadByte();
        if (hasVcWibble != 0)
            r.BaseStream.Position += vertexCount;
        r.ReadInt32(); // num_index_sets
        var pixelShader = r.ReadUInt32();
        if (pixelShader == 1)
        {
            r.ReadInt32();
            var cnt = r.ReadInt32();
            r.BaseStream.Position += cnt;
        }
    }

    // ── Links ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Read hierarchy links: link_count(u32) + N × 80 bytes.
    ///     Per link: sector_checksum(u32) + parent_checksum(u32) + pad_u16 + index(u16) + pad(u32) + 4×4 matrix.
    /// </summary>
    private static XbxLink[] ReadLinks(BinaryReader r)
    {
        if (r.BaseStream.Position >= r.BaseStream.Length)
            return [];

        var numLinks = r.ReadInt32();
        if (numLinks <= 0)
            return [];

        var links = new XbxLink[numLinks];
        for (var i = 0; i < numLinks; i++)
        {
            var sectorChecksum = r.ReadUInt32();
            var parentChecksum = r.ReadUInt32();
            r.ReadUInt16(); // always 0
            var index = r.ReadUInt16();
            r.ReadUInt32(); // always 0

            // 4×4 matrix read column-major
            var m = new Matrix4x4(
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            links[i] = new XbxLink
            {
                SectorChecksum = sectorChecksum,
                ParentChecksum = parentChecksum,
                Index = index,
                Transform = m
            };
        }

        return links;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int GetPassCount(XbxMaterial[] materials, uint materialChecksum)
    {
        foreach (var mat in materials)
        {
            if (mat.Checksum == materialChecksum)
                return mat.NumPasses;
        }

        return 1; // fallback
    }

    private static ushort[] ReadUInt16Array(BinaryReader r, int count)
    {
        var arr = new ushort[count];
        for (var i = 0; i < count; i++)
            arr[i] = r.ReadUInt16();
        return arr;
    }
}
