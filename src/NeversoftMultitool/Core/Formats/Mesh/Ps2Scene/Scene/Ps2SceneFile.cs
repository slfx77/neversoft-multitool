using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

/// <summary>
///     Parser for native PS2 scene files (.mdl.ps2, .skin.ps2, .iskin.ps2).
///     Follows the THUG source code (scene.cpp/mesh.cpp/material.cpp) binary format,
///     validated across 2,299 files: THPS4 (574), THUG (812), THUG2 (913).
///     Version triples: THPS4 (3,4,1), THUG (5,6,1), THUG2 (6,6,1).
///     Key differences from the io_thps_scene cross-platform format:
///     - Single-pass materials (no multi-pass concept)
///     - Per-mesh vertex data with ADC-based triangle strips (no separate index arrays)
///     - Version-dependent vertex precision (THPS4 all-float vs THUG packed sint16)
/// </summary>
public static class Ps2SceneFile
{
    /// <summary>
    ///     Skinned sint16 position scale factor. From THUG render.h: SUB_INCH_PRECISION = 16.0f.
    ///     VU1 converts: float_pos = sint16_pos / 16.0f.
    /// </summary>
    private const float SkinPositionScale = 1f / 16f;

    /// <summary>
    ///     Skinned sint16 UV scale factor. From THUG mesh.cpp VertexSTFloat:
    ///     float_st = sint16 * 0.000244140625f = sint16 / 4096.0f.
    /// </summary>
    private const float SkinUvScale = 1f / 4096f;

    public static readonly string[] SupportedExtensions =
        [".mdl.ps2", ".skin.ps2", ".iskin.ps2", ".skin", ".mdl"];

    public static bool IsPs2Scene(byte[] data)
    {
        if (data.Length < 12) return false;
        var matVer = BitConverter.ToUInt32(data, 0);
        var meshVer = BitConverter.ToUInt32(data, 4);
        var vertVer = BitConverter.ToUInt32(data, 8);
        return matVer is 3 or 5 or 6
               && meshVer is 4 or 6
               && vertVer == 1;
    }

    public static Ps2Scene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static Ps2Scene Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        // Version triple
        var matVersion = r.ReadInt32();
        var meshVersion = r.ReadInt32();
        var vertVersion = r.ReadInt32();

        // THUG2 .skin.ps2 files use format marker 1 for pre-compiled VIF/DMA rendering
        // chains (bones pre-baked, lighting pre-computed). The matching .iskin.ps2 file
        // contains the same mesh in the standard parseable scene format.
        if (matVersion == 1)
            throw new InvalidDataException(
                $"THUG2 pre-compiled VIF/DMA format (version {matVersion},{meshVersion},{vertVersion})" +
                " — use matching .iskin.ps2 file instead");

        // Materials (per material.cpp LoadMaterials)
        var numMaterials = r.ReadInt32();
        var materials = new List<Ps2Material>(numMaterials);
        for (var i = 0; i < numMaterials; i++)
            materials.Add(ReadMaterial(r, matVersion));

        // Mesh groups (per scene.cpp LoadMeshGroup / mesh.cpp LoadVertices)
        var numGroups = r.ReadInt32();
        if (meshVersion >= 4)
            r.ReadInt32(); // TotalNumMeshes (unused by parser)

        var meshGroups = new List<Ps2MeshGroup>(numGroups);
        for (var i = 0; i < numGroups; i++)
            meshGroups.Add(ReadMeshGroup(r, meshVersion));

        return new Ps2Scene
        {
            MaterialVersion = matVersion,
            MeshVersion = meshVersion,
            VertexVersion = vertVersion,
            Materials = materials,
            MeshGroups = meshGroups
        };
    }

    // ================================================================
    //  Materials — per material.cpp LoadMaterials
    // ================================================================

    private static Ps2Material ReadMaterial(BinaryReader r, int matVer)
    {
        var checksum = r.ReadUInt32();

        // mat_ver >= 5: flags read here (before texture checksum)
        uint flags = 0;
        if (matVer >= 5)
            flags = r.ReadUInt32();

        // Alpha reference (Aref)
        var alphaRef = 0;
        if (matVer >= 2)
            alphaRef = (matVer >= 3 ? r.ReadInt32() : r.ReadByte()) & 0xFF;

        // Texture checksum — with animated texture handling for mat_ver >= 5
        uint textureChecksum = 0;
        if (matVer >= 5 && (flags & (uint)Ps2MaterialFlags.AnimatedTexture) != 0)
        {
            var numAnimTextures = r.ReadInt32();
            for (var i = 0; i < numAnimTextures; i++)
                r.ReadUInt32(); // each texture checksum
        }
        else
        {
            textureChecksum = r.ReadUInt32();
        }

        // Group checksum (mat_ver >= 3)
        uint groupChecksum = 0;
        if (matVer >= 3)
            groupChecksum = r.ReadUInt32();

        // mat_ver < 5: flags read here (after group checksum)
        if (matVer < 5)
            flags = r.ReadUInt32();

        // RegALPHA (u64) — GS blend equation register
        var regAlpha = r.ReadUInt64();

        // Clamp (mat_ver >= 2): 0=repeat, non-zero=clamp
        uint clampU = 0, clampV = 0;
        if (matVer >= 2)
        {
            clampU = r.ReadUInt32();
            clampV = r.ReadUInt32();
        }

        // Material colours: 36 bytes (ambient/diffuse/specular RGB + alpha)
        r.BaseStream.Seek(36, SeekOrigin.Current);

        // UV wibble (MATFLAG_UV_WIBBLE = 1 << 0)
        if ((flags & (uint)Ps2MaterialFlags.UvWibble) != 0)
        {
            var uvWibbleSize = matVer <= 4 ? 32 : 40;
            r.BaseStream.Seek(uvWibbleSize, SeekOrigin.Current);
        }

        // VC wibble (MATFLAG_VC_WIBBLE = 1 << 1)
        // Per sequence: num_keys(u32) + phase(i32) + num_keys × 20 bytes
        // Key: time(u32) + RGBA(4×f32) = 20 bytes
        // Note: THUG source material.cpp omits the phase read (source bug), but files include it
        if ((flags & (uint)Ps2MaterialFlags.VcWibble) != 0)
        {
            var numSeqs = r.ReadInt32();
            for (var s = 0; s < numSeqs; s++)
            {
                var numKeys = r.ReadInt32();
                r.ReadInt32(); // phase (always present in all versions)
                r.BaseStream.Seek(numKeys * 20, SeekOrigin.Current); // time(u32) + RGBA(4×f32)
            }
        }

        // Mipmap data
        if (textureChecksum != 0)
        {
            r.ReadUInt32(); // mmag
            r.ReadUInt32(); // mmin
            r.ReadSingle(); // K (LOD bias)
            if (matVer <= 1)
                r.ReadUInt32(); // L
        }
        else
        {
            r.BaseStream.Seek(12 + (matVer <= 1 ? 4 : 0), SeekOrigin.Current);
        }

        // Reflection map scale (mat_ver >= 4)
        if (matVer >= 4)
        {
            r.ReadSingle(); // ref_u
            r.ReadSingle(); // ref_v
        }

        // THUG2 shader_id (mat_ver >= 6) — always 4 bytes, no conditional color data
        if (matVer >= 6)
            r.ReadUInt32();

        return new Ps2Material
        {
            Checksum = checksum,
            Flags = flags,
            TextureChecksum = textureChecksum,
            GroupChecksum = groupChecksum,
            AlphaRef = alphaRef,
            RegAlpha = regAlpha,
            ClampUMode = clampU,
            ClampVMode = clampV
        };
    }

    // ================================================================
    //  Mesh Groups — per scene.cpp LoadMeshGroup
    // ================================================================

    private static Ps2MeshGroup ReadMeshGroup(BinaryReader r, int meshVer)
    {
        var groupChecksum = r.ReadUInt32();
        var numMeshes = r.ReadInt32();

        var meshes = new List<Ps2Mesh>(numMeshes);
        for (var m = 0; m < numMeshes; m++)
            meshes.Add(ReadMesh(r, meshVer));

        return new Ps2MeshGroup
        {
            Checksum = groupChecksum,
            Meshes = meshes
        };
    }

    // ================================================================
    //  Meshes — per mesh.cpp LoadVertices
    // ================================================================

    private static Ps2Mesh ReadMesh(BinaryReader r, int meshVer)
    {
        var meshChecksum = r.ReadUInt32();

        // LOD and hierarchy data (mesh_ver >= 2)
        if (meshVer >= 2)
        {
            r.ReadUInt32(); // lod1
            r.ReadUInt32(); // lod2
            r.ReadUInt32(); // hierarchy data
            var numChildren = r.ReadInt32();
            for (var c = 0; c < numChildren; c++)
                r.ReadUInt32(); // child checksum
            r.BaseStream.Seek(16, SeekOrigin.Current); // bounding sphere (4 floats)
        }

        // Material checksum
        var materialChecksum = r.ReadUInt32();

        // Mesh flags
        var meshFlags = (Ps2MeshFlags)r.ReadUInt32();

        // Bounding data: objbox(3f) + box(3f) + mesh_sphere(4f) = 40 bytes
        r.BaseStream.Seek(12, SeekOrigin.Current); // objbox
        r.BaseStream.Seek(12, SeekOrigin.Current); // box
        var sx = r.ReadSingle();
        var sy = r.ReadSingle();
        var sz = r.ReadSingle();
        var sr = r.ReadSingle();
        var boundingSphere = new Vector4(sx, sy, sz, sr);

        // Pass (mesh_ver >= 5)
        if (meshVer >= 5)
            r.ReadUInt32();

        // MaterialName (mesh_ver >= 6)
        if (meshVer >= 6)
            r.ReadUInt32();

        // Vertices
        var numVertices = r.ReadInt32();
        var vertices = numVertices > 0
            ? ReadVertices(r, numVertices, meshFlags, meshVer)
            : [];

        return new Ps2Mesh
        {
            Checksum = meshChecksum,
            MaterialChecksum = materialChecksum,
            MeshFlags = meshFlags,
            BoundingSphere = boundingSphere,
            Vertices = vertices
        };
    }

    // ================================================================
    //  Vertices — per mesh.cpp LoadVertices
    //
    //  Data layout order: [ST] [Colour] [Normal] [Skin] [Position+ADC]
    //  Each component is optional based on mesh flags.
    //
    //  THPS4 (mesh_ver <= 4): All attributes use full float precision.
    //  THUG/THUG2 (mesh_ver >= 6): Non-skinned uses floats, skinned uses packed sint16.
    // ================================================================

    private static Ps2Vertex[] ReadVertices(
        BinaryReader r, int count, Ps2MeshFlags flags, int meshVer)
    {
        var hasTexture = (flags & Ps2MeshFlags.Texture) != 0;
        var hasColours = (flags & Ps2MeshFlags.Colours) != 0;
        var hasNormals = (flags & Ps2MeshFlags.Normals) != 0;
        var isSkinned = (flags & Ps2MeshFlags.Skinned) != 0;

        // Determine if this mesh uses packed (sint16) vertex data
        var usePacked = meshVer >= 6 && isSkinned;

        var vertices = new Ps2Vertex[count];
        for (var i = 0; i < count; i++)
            vertices[i] = usePacked
                ? ReadVertexPacked(r, hasTexture, hasColours, hasNormals)
                : ReadVertexFloat(r, hasTexture, hasColours, hasNormals, isSkinned, meshVer);

        return vertices;
    }

    /// <summary>
    ///     Read a vertex with float-precision attributes.
    ///     Used for: all THPS4 vertices, THUG/THUG2 non-skinned vertices.
    /// </summary>
    private static Ps2Vertex ReadVertexFloat(
        BinaryReader r, bool hasTexture, bool hasColours, bool hasNormals,
        bool isSkinned, int meshVer)
    {
        // ST (texture coordinates) — 2 floats
        float u = 0, v = 0;
        if (hasTexture)
        {
            u = r.ReadSingle();
            v = r.ReadSingle();
        }

        // Colour — RGBA 4 bytes
        byte cr = 128, cg = 128, cb = 128, ca = 128;
        if (hasColours)
        {
            cr = r.ReadByte();
            cg = r.ReadByte();
            cb = r.ReadByte();
            ca = r.ReadByte();
        }

        // Normal
        float nx = 0, ny = 0, nz = 1;
        if (hasNormals)
        {
            if (meshVer <= 4)
            {
                // THPS4: 3 floats (12 bytes)
                nx = r.ReadSingle();
                ny = r.ReadSingle();
                nz = r.ReadSingle();
            }
            else
            {
                // THUG/THUG2 non-skinned: packed 2×sint16 (4 bytes)
                DecodePackedNormal(r, out nx, out ny, out nz);
            }
        }

        // Skin data — THPS4 only (meshVer <= 4): 2×f32 weights + pad(4B) + 2×u32 bones + pad(4B)
        int bi0 = 0, bi1 = 0, bi2 = 0;
        float bw0 = 0, bw1 = 0, bw2 = 0;
        var hasSkin = false;
        if (isSkinned && meshVer <= 4)
        {
            bw0 = r.ReadSingle();
            bw1 = r.ReadSingle();
            r.BaseStream.Seek(4, SeekOrigin.Current); // pad
            bi0 = (int)r.ReadUInt32();
            bi1 = (int)r.ReadUInt32();
            r.BaseStream.Seek(4, SeekOrigin.Current); // pad
            bw2 = 0;
            bi2 = 0; // THPS4 uses 2 bones max
            hasSkin = true;
        }
        // THUG/THUG2 skinned uses packed path (ReadVertexPacked), not this function

        // Position (3 floats) + ADC (u32)
        var px = r.ReadSingle();
        var py = r.ReadSingle();
        var pz = r.ReadSingle();
        var adc = r.ReadUInt32();

        return new Ps2Vertex(
            new Vector3(px, py, pz),
            new Vector3(nx, ny, nz),
            cr, cg, cb, ca,
            u, v,
            hasNormals, hasColours, hasTexture,
            (adc & 0x8000) != 0,
            bi0, bi1, bi2, bw0, bw1, bw2, hasSkin);
    }

    /// <summary>
    ///     Read a vertex with packed sint16 attributes.
    ///     Used for: THUG/THUG2 skinned vertices only.
    /// </summary>
    private static Ps2Vertex ReadVertexPacked(
        BinaryReader r, bool hasTexture, bool hasColours, bool hasNormals)
    {
        // ST (texture coordinates) — 2×sint16, scaled by 1/4096
        float u = 0, v = 0;
        if (hasTexture)
        {
            u = r.ReadInt16() * SkinUvScale;
            v = r.ReadInt16() * SkinUvScale;
        }

        // Colour — RGBA 4 bytes (same format as float path)
        byte cr = 128, cg = 128, cb = 128, ca = 128;
        if (hasColours)
        {
            cr = r.ReadByte();
            cg = r.ReadByte();
            cb = r.ReadByte();
            ca = r.ReadByte();
        }

        // Normal — packed 2×sint16 (same as THUG non-skinned)
        float nx = 0, ny = 0, nz = 1;
        if (hasNormals)
            DecodePackedNormal(r, out nx, out ny, out nz);

        // Skin data — 2×sint16 weights + 4×uint8 bone indices
        // From THUG mesh.cpp VertexWeights(): w2 = 0x7FFF - w0 - w1 (3 bones max).
        // Bone indices: only [0],[1],[2] used (packed into normals via <<2 in VU1).
        var sw0 = r.ReadInt16();
        var sw1 = r.ReadInt16();
        var bi0 = r.ReadByte();
        var bi1 = r.ReadByte();
        var bi2 = r.ReadByte();
        _ = r.ReadByte(); // unused 4th index
        const float weightScale = 1f / 32767f;
        var bw0 = sw0 * weightScale;
        var bw1 = sw1 * weightScale;
        var bw2 = (32767 - sw0 - sw1) * weightScale;

        // Position — 3×sint16, scaled by SUB_INCH_PRECISION inverse (1/16)
        var px = r.ReadInt16() * SkinPositionScale;
        var py = r.ReadInt16() * SkinPositionScale;
        var pz = r.ReadInt16() * SkinPositionScale;
        var adc = r.ReadUInt16();

        return new Ps2Vertex(
            new Vector3(px, py, pz),
            new Vector3(nx, ny, nz),
            cr, cg, cb, ca,
            u, v,
            hasNormals, hasColours, hasTexture,
            (adc & 0x8000) != 0,
            bi0, bi1, bi2, bw0, bw1, bw2, true);
    }

    /// <summary>
    ///     Decode packed normal from 2×sint16 (4 bytes).
    ///     From THUG mesh.cpp VertexNormal:
    ///     nz = sqrt(32767² - nx² - ny²)
    ///     Z sign encoded in LSB of nx (if bit 0 set, negate Z).
    /// </summary>
    private static void DecodePackedNormal(BinaryReader r, out float nx, out float ny, out float nz)
    {
        var nxRaw = r.ReadInt16();
        var nyRaw = r.ReadInt16();

        nx = nxRaw / 32767f;
        ny = nyRaw / 32767f;

        var nzSq = 1f - nx * nx - ny * ny;
        nz = nzSq > 0 ? MathF.Sqrt(nzSq) : 0f;

        // LSB of nx encodes Z sign
        if ((nxRaw & 1) != 0)
            nz = -nz;
    }
}
