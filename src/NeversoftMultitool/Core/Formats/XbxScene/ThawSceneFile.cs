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
    internal const int MaxPasses = 4;
    private const int SMeshHeaderSize = 224;

    // Sector flags (shared with XbxSceneFile / nxtools constants.py)
    internal const int SecflagHasColors = 1 << 1;
    internal const int SecflagHasNormals = 1 << 2;
    internal const int SecflagHasWeights = 1 << 4;
    internal const int SecflagBillboard = 0x00800000;

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

    public static XbxScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static XbxScene Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        // ── File header (32 bytes) ──────────────────────────────────────────
        r.ReadUInt32(); // dq_offset (disqualifier block, unused here)
        r.BaseStream.Position = 32; // skip 28 reserved bytes

        // ── Material list ───────────────────────────────────────────────────
        var offMaterials = (int)r.BaseStream.Position; // = 32
        r.ReadByte(); // material_version (2 for WPC)
        r.ReadByte(); // pre_material_header_size (16)
        var materialCount = r.ReadUInt16();
        var matListSize = r.ReadInt32();
        r.ReadInt32(); // babeface_block_size
        r.ReadInt32(); // unknown

        var materials = new XbxMaterial[materialCount];
        for (var i = 0; i < materialCount; i++)
            materials[i] = ThawSceneMeshSupport.ReadMaterial(r);

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
                meshes[m] = ThawSceneMeshSupport.ReadSMesh(r, headerOffset, offScene, sectorFlags[s], materials);
            }

            sectors[s] = new XbxSector
            {
                Checksum = sectorChecksums[s],
                BoneIndex = -1,
                Flags = sectorFlags[s],
                Meshes = meshes
            };
        }

        // ── Hierarchy links ─────────────────────────────────────────────────
        var links = numLinks > 0 ? ReadLinks(r, offHierarchy, numLinks) : [];

        return new XbxScene { Materials = materials, Sectors = sectors, Links = links };
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
                Transform = m
            };
        }

        return links;
    }
}
