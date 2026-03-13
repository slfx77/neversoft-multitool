using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parser for THAW PC/Xbox scene files (.skin.wpc, .mdl.wpc, .skin.xbx, .mdl.xbx).
///     Completely different format from THUG2 (XbxSceneFile): 32B file header, 0xBABEFACE sentinel,
///     parallel material pass arrays, CScene with relative offsets, 0x7FFF strip separators.
///     Format spec from nxtools fmt_thawscene_import.py.
/// </summary>
public static class ThawSceneFile
{
    private const uint BabefaceMagic = 0xBABEFACE;
    private const int MaxPasses = 4;
    private const int SMeshHeaderSize = 224;

    // Sector flags (shared with XbxSceneFile / nxtools constants.py)
    private const int SecflagHasColors = 1 << 1;
    private const int SecflagHasNormals = 1 << 2;
    private const int SecflagHasWeights = 1 << 4;
    private const int SecflagBillboard = 0x00800000;

    public static readonly string[] SupportedExtensions =
        [".skin.wpc", ".mdl.wpc", ".skin.xbx", ".mdl.xbx"];

    /// <summary>
    ///     Detect THAW scene format: not THUG2 (version 1,1,1), pre_material_header_size=16 at 0x21.
    /// </summary>
    public static bool IsThawScene(byte[] data)
    {
        if (data.Length < 0x30) return false;
        if (XbxSceneFile.IsXbxScene(data)) return false;
        if (data[0x21] != 16) return false;
        var matCount = BitConverter.ToUInt16(data, 0x22);
        return matCount > 0 && matCount < 10000;
    }

    public static XbxScene Parse(string filePath) => Parse(File.ReadAllBytes(filePath));

    public static XbxScene Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        // ── File header (32 bytes) ──────────────────────────────────────────
        r.ReadUInt32(); // dq_offset (disqualifier block, unused here)
        r.BaseStream.Position = 32; // skip 28 reserved bytes

        // ── Material list ───────────────────────────────────────────────────
        var offMaterials = (int)r.BaseStream.Position; // = 32
        r.ReadByte();  // material_version (2 for WPC)
        r.ReadByte();  // pre_material_header_size (16)
        var materialCount = r.ReadUInt16();
        var matListSize = r.ReadInt32();
        r.ReadInt32(); // babeface_block_size
        r.ReadInt32(); // unknown

        var materials = new XbxMaterial[materialCount];
        for (var i = 0; i < materialCount; i++)
            materials[i] = ReadMaterial(r);

        // Seek past all material data (incl. UV wibble/anim data at separate offsets)
        r.BaseStream.Position = offMaterials + matListSize;

        // Verify and skip 0xBABEFACE sentinel + padding
        if (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var magic = r.ReadUInt32();
            if (magic == BabefaceMagic)
            {
                var padCount = r.ReadInt32();
                if (padCount > 0)
                    r.BaseStream.Position += padCount;
            }
        }

        // ── CScene object ───────────────────────────────────────────────────
        var offScene = (int)r.BaseStream.Position;
        r.ReadUInt16(); // scene_version
        r.ReadUInt16(); // CScene object size
        r.BaseStream.Position += 32; // reserved
        r.BaseStream.Position += 16; // bounds_min (vec4f)
        r.BaseStream.Position += 16; // bounds_max (vec4f)
        r.BaseStream.Position += 12; // sphere_pos (vec3f)
        r.ReadSingle(); // sphere_radius

        var numLinks = r.ReadInt32();
        r.BaseStream.Position += 32; // reserved
        var offHierarchy = offScene + r.ReadInt32();
        r.BaseStream.Position += 4; // reserved

        var sectorCount = r.ReadInt32();
        var offCSector = offScene + r.ReadInt32();
        var offCGeom = offScene + r.ReadInt32();
        r.ReadInt32(); // billboards offset
        r.ReadInt32(); // big_padding offset
        var offSMesh = offScene + r.ReadInt32();
        r.BaseStream.Position += 16; // reserved
        r.ReadInt32(); // unk_d

        // ── CSectors (48 bytes each) ────────────────────────────────────────
        r.BaseStream.Position = offCSector;
        var sectorChecksums = new uint[sectorCount];
        var sectorFlags = new int[sectorCount];
        for (var i = 0; i < sectorCount; i++)
        {
            r.ReadInt32(); // always 0
            sectorChecksums[i] = r.ReadUInt32();
            sectorFlags[i] = r.ReadInt32();
            r.BaseStream.Position += 36; // null padding
        }

        // ── CGeoms (one per sector) ─────────────────────────────────────────
        r.BaseStream.Position = offCGeom;
        var meshStartIndices = new int[sectorCount];
        var meshCounts = new int[sectorCount];
        var runningMeshIndex = 0;

        for (var i = 0; i < sectorCount; i++)
        {
            r.ReadInt32(); // block_size
            r.BaseStream.Position += 20; // reserved
            r.BaseStream.Position += 16; // bounds_min (vec4f)
            r.BaseStream.Position += 16; // bounds_max (vec4f)
            r.BaseStream.Position += 12; // reserved
            meshCounts[i] = r.ReadInt32();
            r.BaseStream.Position += 32; // reserved

            meshStartIndices[i] = runningMeshIndex;
            runningMeshIndex += meshCounts[i];
        }

        // ── sMeshes + vertex/face data ──────────────────────────────────────
        var sectors = new XbxSector[sectorCount];
        for (var s = 0; s < sectorCount; s++)
        {
            var meshes = new XbxMesh[meshCounts[s]];
            for (var m = 0; m < meshCounts[s]; m++)
            {
                var headerOffset = offSMesh + SMeshHeaderSize * (meshStartIndices[s] + m);
                meshes[m] = ReadSMesh(r, headerOffset, offScene, sectorFlags[s], materials);
            }

            sectors[s] = new XbxSector
            {
                Checksum = sectorChecksums[s],
                BoneIndex = -1,
                Flags = sectorFlags[s],
                Meshes = meshes,
            };
        }

        // ── Hierarchy links ─────────────────────────────────────────────────
        var links = numLinks > 0 ? ReadLinks(r, offHierarchy, numLinks) : [];

        return new XbxScene { Materials = materials, Sectors = sectors, Links = links };
    }

    // ── Material reading ────────────────────────────────────────────────────

    /// <summary>
    ///     Read a single THAW material with parallel pass arrays.
    ///     Fixed 288 bytes per material (UV wibble/anim data at separate seek-based offsets).
    /// </summary>
    private static XbxMaterial ReadMaterial(BinaryReader r)
    {
        var checksum = r.ReadUInt32();
        var nameChecksum = r.ReadUInt32();
        var numPasses = Math.Clamp(r.ReadInt32(), 0, MaxPasses);

        r.ReadByte();  // unknown bool
        var doubleSided = r.ReadByte() > 0;
        r.ReadUInt16(); // unknown

        var opacityCutoff = r.ReadByte();
        r.ReadByte();  // unknown
        var useOpacityCutoff = r.ReadUInt16() > 0;

        r.BaseStream.Position += 24; // reserved
        var drawOrder = r.ReadSingle();

        // Parallel pass arrays (always MaxPasses=4 entries regardless of numPasses)
        var passFlags = ReadUInt32Array(r, MaxPasses);
        var passChecksums = ReadUInt32Array(r, MaxPasses);
        r.BaseStream.Position += MaxPasses * 16; // pass_colors (4×vec4f)

        var passBlendModes = new uint[MaxPasses];
        for (var i = 0; i < MaxPasses; i++)
        {
            passBlendModes[i] = r.ReadUInt16();
            r.ReadInt16(); // fixed amount
        }

        r.BaseStream.Position += MaxPasses * 4;  // pass_uv_modes
        r.BaseStream.Position += MaxPasses * 8;  // pass_pairs
        r.BaseStream.Position += MaxPasses * 4;  // pass_shorts
        r.BaseStream.Position += MaxPasses * 4;  // pass_uvwibble_offsets
        r.BaseStream.Position += 16;              // reserved

        r.ReadInt32(); // vc_wibble_count
        r.ReadInt32(); // vc_wibble_param_offset
        r.ReadInt32(); // vc_wibble_color_offset
        r.ReadInt32(); // anim_offset

        r.BaseStream.Position += 16; // specular_color (vec4f)

        var passes = new XbxPass[numPasses];
        for (var p = 0; p < numPasses; p++)
        {
            passes[p] = new XbxPass
            {
                TextureChecksum = passChecksums[p],
                Flags = passFlags[p],
                BlendMode = passBlendModes[p],
            };
        }

        return new XbxMaterial
        {
            Checksum = checksum,
            NameChecksum = nameChecksum,
            NumPasses = numPasses,
            AlphaCutoff = useOpacityCutoff ? opacityCutoff : 0,
            Sorted = drawOrder != 0,
            DrawOrder = drawOrder,
            SingleSided = !doubleSided,
            NoBfc = false,
            ZBias = 0,
            Grassify = false,
            Passes = passes,
        };
    }

    // ── sMesh reading ───────────────────────────────────────────────────────

    /// <summary>
    ///     Read a THAW sMesh: 224-byte header at fixed offset, then face + vertex data at
    ///     separate offsets (relative to off_scene). Pre-triangulates 0x7FFF strips.
    /// </summary>
    private static XbxMesh ReadSMesh(
        BinaryReader r, int headerOffset, int offScene, int sectorFlags, XbxMaterial[] materials)
    {
        r.BaseStream.Position = headerOffset;

        // sMesh header (reading sequentially through 224-byte block)
        var meshFlags = r.ReadUInt32();           // +4  =4
        var spherePos = ReadVec3(r);              // +12 =16
        var sphereRadius = r.ReadSingle();        // +4  =20
        var materialChecksum = r.ReadUInt32();     // +4  =24
        var vertexStride = r.ReadByte();          // +1  =25
        r.BaseStream.Position += 3;               // +3  =28 (pad, unk, 0xFF)
        r.BaseStream.Position += 4;               // +4  =32 (unk_b, oddbytes)
        r.BaseStream.Position += 2;               // +2  =34 (odd_const, lod_count)
        r.BaseStream.Position += 2;               // +2  =36 (face_type, odd_const)
        r.BaseStream.Position += 2;               // +2  =38 (internal_mesh_index)
        var vertexCount = r.ReadUInt16();         // +2  =40

        var faceCount0 = r.ReadInt32();           // +4  =44 (face_counts[0])
        r.BaseStream.Position += 12;              // +12 =56 (face_counts[1..3])
        r.BaseStream.Position += 8;               // +8  =64 (face_block_size_enc, odd_shorts)
        r.BaseStream.Position += 16;              // +16 =80 (alt_flags, unk, weird, shader)
        r.BaseStream.Position += 12;              // +12 =92 (FFFFFFFF, always_5)

        var faceOffset0Raw = r.ReadInt32();       // +4  =96 (face_offsets[0])
        r.BaseStream.Position += 28;              // +28 =124 (face_offsets[1..7])
        var vertexOffsetRaw = r.ReadInt32();      // +4  =128
        // Remaining 96 bytes of header not needed

        // ── Read face data (pass 0) ─────────────────────────────────────
        var passCount = GetPassCount(materials, materialChecksum);
        var isBillboard = (sectorFlags & SecflagBillboard) != 0;

        ushort[] faceIndices = [];
        if (faceCount0 > 0 && faceOffset0Raw > 0)
        {
            r.BaseStream.Position = offScene + faceOffset0Raw;
            r.ReadInt32(); // face_block_size
            var rawIndices = new ushort[faceCount0];
            for (var i = 0; i < faceCount0; i++)
                rawIndices[i] = r.ReadUInt16();
            faceIndices = TriangulateStrips(rawIndices, isBillboard);
        }

        // ── Read vertex data ────────────────────────────────────────────
        XbxVertex[] vertices = [];
        if (vertexCount > 0 && vertexOffsetRaw > 0)
        {
            r.BaseStream.Position = offScene + vertexOffsetRaw;
            vertices = ReadVertexBuffer(r, sectorFlags, vertexCount, vertexStride, passCount);
        }

        return new XbxMesh
        {
            BsphereCenter = spherePos,
            BsphereRadius = sphereRadius,
            MeshFlags = meshFlags,
            MaterialChecksum = materialChecksum,
            Vertices = vertices,
            FaceIndices = faceIndices,
            IsPreTriangulated = true,
        };
    }

    // ── Face triangulation ──────────────────────────────────────────────────

    /// <summary>
    ///     Triangulate 0x7FFF-separated triangle strips into flat indexed triangles.
    /// </summary>
    private static ushort[] TriangulateStrips(ushort[] rawIndices, bool billboard)
    {
        var triangles = new List<ushort>();
        var stripStart = 0;

        for (var i = 0; i <= rawIndices.Length; i++)
        {
            if (i == rawIndices.Length || rawIndices[i] == 0x7FFF)
            {
                TriangulateOneStrip(rawIndices, stripStart, i, triangles, billboard);
                stripStart = i + 1;
            }
        }

        return triangles.ToArray();
    }

    private static void TriangulateOneStrip(
        ushort[] indices, int start, int end, List<ushort> output, bool billboard)
    {
        var len = end - start;
        for (var f = 2; f < len; f++)
        {
            ushort i0, i1, i2;
            if (f % 2 == 0)
            {
                i0 = indices[start + f - 2];
                i1 = indices[start + f - 1];
                i2 = indices[start + f];
            }
            else
            {
                i0 = indices[start + f - 2];
                i1 = indices[start + f];
                i2 = indices[start + f - 1];
            }

            if (i0 == i1 || i1 == i2 || i0 == i2)
                continue;

            if (billboard)
            {
                output.Add(i2);
                output.Add(i1);
                output.Add(i0);
            }
            else
            {
                output.Add(i0);
                output.Add(i1);
                output.Add(i2);
            }
        }
    }

    // ── Vertex reading (same layout as THUG2 base class) ────────────────────

    /// <summary>
    ///     Decode interleaved vertex buffer. Same format family as THUG2 (XbxSceneFile).
    ///     Skinned vertices preserve packed weight/index data for later skeleton transfer.
    ///     Non-skinned: pos(3f) + [normal(3f)] + [color(4B)] + UVs.
    /// </summary>
    private static XbxVertex[] ReadVertexBuffer(
        BinaryReader r, int sectorFlags, int vertexCount, int vertexStride, int passCount)
    {
        var vertices = new XbxVertex[vertexCount];
        var isSkinned = (sectorFlags & SecflagHasWeights) != 0;
        var hasNormals = (sectorFlags & SecflagHasNormals) != 0;
        var hasColors = (sectorFlags & SecflagHasColors) != 0;
        var isBillboard = (sectorFlags & SecflagBillboard) != 0;

        for (var i = 0; i < vertexCount; i++)
        {
            var vertStart = r.BaseStream.Position;
            var v = new XbxVertex();

            if (isSkinned)
            {
                v.Position = ReadVec3(r);
                XbxSkinVertexCodec.ReadSkinningData(r, ref v);

                if (hasNormals)
                {
                    v.Normal = UnpackNormal(r.ReadUInt32());
                    v.HasNormal = true;
                }
            }
            else
            {
                var pos = ReadVec3(r);
                var nrm = Vector3.Zero;

                if (hasNormals)
                {
                    nrm = ReadVec3(r);
                    v.HasNormal = true;
                }

                // Billboard: offset position by normal, flatten normal
                if (isBillboard)
                {
                    pos += nrm;
                    nrm = Vector3.UnitZ;
                    v.HasNormal = true;
                }

                v.Position = pos;
                v.Normal = nrm;
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

            // UVs — one set per pass, keep only the first
            if (passCount > 0)
            {
                var u = r.ReadSingle();
                var vCoord = r.ReadSingle();
                // THAW textures are stored bottom-up (PS2 heritage); flip V to match flipped PNGs
                v.TexCoord = new Vector2(u, 1.0f - vCoord);
            }

            vertices[i] = v;
            r.BaseStream.Position = vertStart + vertexStride;
        }

        return vertices;
    }

    // ── Links ───────────────────────────────────────────────────────────────

    /// <summary>Read hierarchy links: 80 bytes per link (same format as THUG2).</summary>
    private static XbxLink[] ReadLinks(BinaryReader r, int offset, int count)
    {
        r.BaseStream.Position = offset;
        var links = new XbxLink[count];

        for (var i = 0; i < count; i++)
        {
            var sectorChecksum = r.ReadUInt32();
            var parentChecksum = r.ReadUInt32();
            r.ReadUInt16(); // always 0
            var index = r.ReadUInt16();
            r.ReadUInt32(); // always 0

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
                Transform = m,
            };
        }

        return links;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Unpack packed normal from u32 (11+11+10 bit signed, same as THUG2).</summary>
    private static Vector3 UnpackNormal(uint packed)
    {
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
        if (len > 0) { nx /= len; ny /= len; nz /= len; }
        return new Vector3(nx, ny, nz);
    }

    private static int GetPassCount(XbxMaterial[] materials, uint checksum)
    {
        foreach (var mat in materials)
            if (mat.Checksum == checksum)
                return mat.NumPasses;
        return 1;
    }

    private static Vector3 ReadVec3(BinaryReader r) =>
        new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static uint[] ReadUInt32Array(BinaryReader r, int count)
    {
        var arr = new uint[count];
        for (var i = 0; i < count; i++)
            arr[i] = r.ReadUInt32();
        return arr;
    }
}
