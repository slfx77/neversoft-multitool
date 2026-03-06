using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parser for THAW PS2 .skin.ps2 files (pre-compiled VIF/DMA rendering chains).
///     Also handles THUG2 pre-compiled .skin.ps2 files that have no .iskin.ps2 companion.
///     Binary layout: 32B header + object table (8B×N) + entry table (64B×M) + gap chunks + DMA/VIF data.
///     Each mesh starts with FLUSH+DIRECT (GIF register setup) followed by STCYCL(CL=3,WL=1)
///     interleaved batches: V3_16 positions, V3_8 normals, V4_16 UVs+bone data.
///     Each VIF batch is a continuous triangle strip with no internal strip restarts.
///     The PS2 VU1 microprogram processes each batch as one unbroken strip.
///
///     Also handles PAK-extracted .skin files from THAW PS2 level _main PAKs.
///     These have the same VIF vertex format but a different preamble (no 32B header/entry table).
///     Material info is extracted from GS register writes (TEX0_1, ALPHA_1) in DIRECT VIF blocks.
/// </summary>
public static class ThawPs2SkinFile
{
    private const float PositionScale = 1f / 16f; // SUB_INCH_PRECISION
    private const float NormalScale = 1f / 127f;
    private const float UvScale = 1f / 4096f;

    /// <summary>
    ///     Detect pre-compiled VIF/DMA .skin.ps2 (THAW or THUG2).
    ///     Works with partial data (32-byte header from FormatProbe) or full file data.
    ///     When fileSize is provided, it's used for size validation instead of data.Length.
    /// </summary>
    public static bool IsThawPs2Skin(byte[] data, long fileSize = 0)
    {
        if (data.Length < 32) return false;
        if (fileSize == 0) fileSize = data.Length;

        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes1 = BitConverter.ToUInt32(data, 4);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);

        // Standard PS2 scene version triples: (3,4,1), (5,6,1), (6,6,1)
        if (numObjects is 3 or 5 or 6 && totalMeshes1 is 4 or 6 && totalMeshes2 == 1)
            return false;

        // Sanity: numObjects small, meshes reasonable
        if (numObjects == 0 || numObjects > 20) return false;
        if (totalMeshes2 == 0 || totalMeshes2 > 500) return false;
        if (totalMeshes1 > totalMeshes2) return false;

        // dataSize + 16 must not exceed file size (allow CAS trailing data)
        if (dataSize + 16 > fileSize) return false;

        // Bounding sphere floats at 0x10 must be valid
        var bsR = BitConverter.ToSingle(data, 0x1C);
        if (float.IsNaN(bsR) || float.IsInfinity(bsR) || bsR <= 0) return false;

        // Entry table must fit within file (only check with full data)
        if (data.Length > 32)
        {
            var entryTableEnd = 32 + numObjects * 8 + totalMeshes2 * 64;
            if (entryTableEnd > fileSize) return false;
        }

        return true;
    }

    public static Ps2Scene Parse(string filePath) => Parse(File.ReadAllBytes(filePath));

    public static Ps2Scene Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        // Header (32 bytes)
        r.ReadUInt32(); // numObjects
        var numObjects = BitConverter.ToUInt32(data, 0);
        r.ReadUInt32(); // totalMeshes1
        var totalMeshes2 = r.ReadUInt32();
        var dataSize = r.ReadUInt32();
        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // bsphere

        // Re-read numObjects from data directly (we already advanced past it)
        ms.Position = 0x20;

        // Object table (8B × numObjects)
        var objects = new (uint checksum, uint meshCount)[numObjects];
        for (var i = 0; i < numObjects; i++)
            objects[i] = (r.ReadUInt32(), r.ReadUInt32());

        // Entry table (64B × totalMeshes2)
        var entries = new EntryRecord[totalMeshes2];
        for (var i = 0; i < totalMeshes2; i++)
            entries[i] = ReadEntry(r);

        // Build materials from entry table
        var materialMap = new Dictionary<uint, Ps2Material>();
        foreach (var entry in entries)
        {
            if (!materialMap.ContainsKey(entry.MaterialChecksum))
            {
                materialMap[entry.MaterialChecksum] = new Ps2Material
                {
                    Checksum = entry.MaterialChecksum,
                    TextureChecksum = entry.TextureChecksum,
                    RegAlpha = entry.GsAlphaLow | ((ulong)entry.GsAlphaHigh << 32),
                    Flags = entry.MaterialFlags
                };
            }
        }

        // Find mesh boundaries in VIF data.
        // After the entry table there are variable-length 80-byte material/bounding descriptor
        // chunks, then DMA/VIF rendering chains. Each mesh starts with a FLUSH+DIRECT pair.
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);

        // Find all mesh start positions (FLUSH+DIRECT pairs)
        var meshStarts = FindMeshBoundaries(data, entryTableEnd, vifEnd);

        // Extract vertex batches from each mesh's VIF range
        var allBatches = new List<VifBatchRecord>();
        for (var meshIdx = 0; meshIdx < meshStarts.Count; meshIdx++)
        {
            var meshStart = meshStarts[meshIdx];
            var meshEnd = meshIdx + 1 < meshStarts.Count ? meshStarts[meshIdx + 1] : vifEnd;

            var meshBatches = WalkMeshVif(data, meshStart, meshEnd);
            foreach (var batch in meshBatches)
            {
                var b = batch;
                b.SetupIndex = meshIdx;
                allBatches.Add(b);
            }
        }

        // Map batches to entry table rows → build meshes
        var meshes = BuildMeshes(data, entries, allBatches);

        // Group meshes by owner object checksum
        var groupMap = new Dictionary<uint, List<Ps2Mesh>>();
        foreach (var (mesh, entryIdx) in meshes)
        {
            var ownerCk = entryIdx < entries.Length ? entries[entryIdx].OwnerObjectChecksum : 0u;
            if (!groupMap.TryGetValue(ownerCk, out var list))
            {
                list = [];
                groupMap[ownerCk] = list;
            }

            list.Add(mesh);
        }

        var meshGroups = groupMap.Select(kv => new Ps2MeshGroup
        {
            Checksum = kv.Key,
            Meshes = kv.Value
        }).ToList();

        return new Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [.. materialMap.Values],
            MeshGroups = meshGroups
        };
    }

    private static EntryRecord ReadEntry(BinaryReader r)
    {
        r.ReadUInt32(); // materialEntrySize
        var materialChecksum = r.ReadUInt32();
        var materialFlags = r.ReadUInt32();
        r.ReadUInt32(); // flags2
        var gsAlphaLow = r.ReadUInt32();
        var gsAlphaHigh = r.ReadUInt32();
        r.ReadUInt32(); // grassFlag
        r.ReadUInt32(); // reserved1
        var textureChecksum = r.ReadUInt32();
        r.ReadUInt32(); // texturePassFlag
        r.ReadUInt32(); // reserved2
        var ownerObjectChecksum = r.ReadUInt32();
        r.ReadUInt32(); // packedStcycl
        var vertexColorMask = r.ReadUInt32();
        r.ReadUInt32(); // reserved3
        r.ReadUInt32(); // reserved4

        return new EntryRecord
        {
            MaterialChecksum = materialChecksum,
            MaterialFlags = materialFlags,
            GsAlphaLow = gsAlphaLow,
            GsAlphaHigh = gsAlphaHigh,
            TextureChecksum = textureChecksum,
            OwnerObjectChecksum = ownerObjectChecksum,
            HasVertexColors = vertexColorMask != 0
        };
    }

    /// <summary>
    ///     Detect PAK-extracted .skin files from THAW PS2 level _main PAK archives.
    ///     These files contain VIF/DMA rendering chains but have a variable-length preamble
    ///     instead of the standard 32B ThawPs2SkinFile header.
    ///     Detection: NOT a known format + contains FLUSH+DIRECT VIF opcode pair in first 8KB.
    /// </summary>
    public static bool IsPakSkin(byte[] data)
    {
        if (data.Length < 256) return false;

        // Must NOT be a known format
        if (IsThawPs2Skin(data, data.Length)) return false;
        if (Ps2SceneFile.IsPs2Scene(data)) return false;

        // Must contain a FLUSH (0x11000000) followed by DIRECT in the first 8KB
        var searchEnd = Math.Min(data.Length, 8192);
        for (var i = 0; i < searchEnd - 3; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != 0x11000000)
                continue;

            // Verify DIRECT (0x50/0x51) follows the FLUSH
            var next = VifNextCode(data, i, data.Length);
            if (next + 4 <= data.Length && (data[next + 3] & 0x7F) is 0x50 or 0x51)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Parse a PAK-extracted .skin file from THAW PS2 level _main PAK archives.
    ///     These files have a variable-length preamble before VIF data. Material info
    ///     is extracted from GS registers (TEX0_1, ALPHA_1) in each mesh's DIRECT block.
    /// </summary>
    public static Ps2Scene ParsePakSkin(byte[] data)
    {
        // Find VIF data start by scanning for first FLUSH opcode
        var vifStart = -1;
        for (var i = 0; i < data.Length - 3; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == 0x11000000)
            {
                vifStart = i;
                break;
            }
        }

        if (vifStart < 0)
            return new Ps2Scene
            {
                MaterialVersion = 0,
                MeshVersion = 0,
                VertexVersion = 0,
                Materials = [],
                MeshGroups = []
            };

        // Find all mesh boundaries (FLUSH+DIRECT pairs)
        var meshStarts = FindMeshBoundaries(data, vifStart, data.Length);
        if (meshStarts.Count == 0)
            return new Ps2Scene
            {
                MaterialVersion = 0,
                MeshVersion = 0,
                VertexVersion = 0,
                Materials = [],
                MeshGroups = []
            };

        // For each mesh: extract GS registers from DIRECT block, then walk VIF for vertices
        var materialMap = new Dictionary<uint, Ps2Material>();
        var allMeshes = new List<Ps2Mesh>();

        for (var meshIdx = 0; meshIdx < meshStarts.Count; meshIdx++)
        {
            var meshStart = meshStarts[meshIdx];
            var meshEnd = meshIdx + 1 < meshStarts.Count ? meshStarts[meshIdx + 1] : data.Length;

            // Parse GS registers from the DIRECT block immediately after FLUSH
            var directOffset = VifNextCode(data, meshStart, data.Length);
            var gsRegs = ParseDirectBlockRegisters(data, directOffset);

            // Create material from GS register data
            var matChecksum = gsRegs.Tex0Cbp; // CBP is unique per texture binding
            if (!materialMap.ContainsKey(matChecksum))
            {
                materialMap[matChecksum] = new Ps2Material
                {
                    Checksum = matChecksum,
                    TextureChecksum = gsRegs.Tex0Cbp,
                    RegAlpha = gsRegs.Alpha1,
                    Flags = 0
                };
            }

            // Walk VIF batches for vertices
            var batches = WalkMeshVif(data, meshStart, meshEnd);
            foreach (var batch in batches)
            {
                if (batch.VertexCount == 0 || batch.PositionOffset < 0) continue;

                var vertices = ExtractBatchVertices(data, batch, false);
                if (vertices.Length == 0) continue;

                allMeshes.Add(new Ps2Mesh
                {
                    Checksum = matChecksum,
                    MaterialChecksum = matChecksum,
                    Vertices = vertices
                });
            }
        }

        // Group all meshes into a single mesh group (one object per .skin file)
        var meshGroups = new List<Ps2MeshGroup>();
        if (allMeshes.Count > 0)
        {
            meshGroups.Add(new Ps2MeshGroup
            {
                Checksum = 0,
                Meshes = allMeshes
            });
        }

        return new Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [.. materialMap.Values],
            MeshGroups = meshGroups
        };
    }

    /// <summary>
    ///     Parse GS register writes from a DIRECT VIF block.
    ///     DIRECT blocks contain GIF A+D register writes that set up GS state (textures, blending).
    ///     Layout: 4-byte DIRECT header → 16-byte GIF tag → N×16-byte register writes.
    /// </summary>
    private static GsRegisters ParseDirectBlockRegisters(byte[] data, int directOffset)
    {
        var result = new GsRegisters();

        if (directOffset + 4 > data.Length) return result;

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directOffset));
        var gifStart = directOffset + 4;
        var gifEnd = gifStart + (qwc << 4);
        if (gifEnd > data.Length || qwc == 0) return result;

        // Read GIF tag (128 bits)
        if (gifStart + 16 > data.Length) return result;
        var gifLo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart));
        var nloop = (int)(gifLo & 0x7FFF);
        var flg = (int)((gifLo >> 58) & 3);
        var nreg = (int)((gifLo >> 60) & 0xF);
        if (nreg == 0) nreg = 16;
        var gifHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart + 8));

        // Expect PACKED mode (FLG=0), single register descriptor A+D (0x0E)
        if (flg != 0 || nreg != 1 || (gifHi & 0xFF) != 0x0E) return result;

        // Parse A+D register writes
        for (var i = 0; i < nloop; i++)
        {
            var off = gifStart + 16 + i * 16;
            if (off + 16 > data.Length) break;

            var dataVal = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off));
            var regHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 8));
            var regAddr = (int)(regHi & 0xFF);

            switch (regAddr)
            {
                case 0x06: // TEX0_1 — extract CBP (bits 37-50) as texture identifier
                    result.Tex0Cbp = (uint)((dataVal >> 37) & 0x3FFF);
                    break;
                case 0x42: // ALPHA_1
                    result.Alpha1 = dataVal;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    ///     Find all mesh boundary positions by scanning for FLUSH+DIRECT VIF opcode pairs.
    ///     Each mesh's DMA rendering chain starts with FLUSH (0x10/0x11) immediately followed
    ///     by DIRECT (0x50/0x51) for GS register setup. This pattern is distinctive and does
    ///     not appear in gap chunk data (which contains floats and checksums).
    /// </summary>
    private static List<int> FindMeshBoundaries(byte[] data, int searchStart, int searchEnd)
    {
        var meshStarts = new List<int>();
        for (var i = searchStart; i + 8 <= searchEnd; i += 4)
        {
            var c1 = data[i + 3] & 0x7F;
            var c2 = data[i + 7] & 0x7F;
            if (c1 is 0x10 or 0x11 && c2 is 0x50 or 0x51)
                meshStarts.Add(i);
        }

        return meshStarts;
    }

    /// <summary>
    ///     Walk VIF opcodes within a single mesh's range and extract vertex batches.
    ///     Each batch = one STCYCL(CL=3,WL=1) block with V3_16 positions + V3_8 normals + V4_16 UVs.
    /// </summary>
    private static List<VifBatchRecord> WalkMeshVif(byte[] data, int start, int end)
    {
        var batches = new List<VifBatchRecord>();
        var pCode = start;
        var inInterleaved = false;
        var currentBatch = new VifBatchRecord();

        while (pCode < end && pCode + 4 <= data.Length)
        {
            var cmd = data[pCode + 3];

            // STCYCL
            if ((cmd & 0x7F) == 0x01)
            {
                var cl = data[pCode];
                var wl = data[pCode + 1];
                if (cl == 3 && wl == 1) // CL=3, WL=1 → interleaved 3-attribute mode
                    inInterleaved = true;
                else if (cl == 1 && wl == 1)
                    inInterleaved = false;

                pCode = VifNextCode(data, pCode, end);
                continue;
            }

            // MSCNT (0x17) or MSCAL (0x14/0x15) — VU kick, ends batch
            if ((cmd & 0x7F) is 0x17 or 0x14 or 0x15)
            {
                if (currentBatch.PositionOffset >= 0 && currentBatch.VertexCount > 0)
                    batches.Add(currentBatch);

                currentBatch = new VifBatchRecord();
                inInterleaved = false;
                pCode = VifNextCode(data, pCode, end);
                continue;
            }

            // UNPACK
            if ((cmd & 0x60) == 0x60)
            {
                var vn = (cmd >> 2) & 3;
                var vl = cmd & 3;
                var num = data[pCode + 2];
                var unpackDataOff = pCode + 4;

                if (inInterleaved && num > 1)
                {
                    if (vn == 2 && vl == 1) // V3_16 = positions
                    {
                        currentBatch.PositionOffset = unpackDataOff;
                        currentBatch.VertexCount = num;
                    }
                    else if (vn == 2 && vl == 2) // V3_8 = normals
                    {
                        currentBatch.NormalOffset = unpackDataOff;
                    }
                    else if (vn == 3 && vl == 1) // V4_16 = UVs + ADC
                    {
                        currentBatch.UvAdcOffset = unpackDataOff;
                    }
                }
            }

            pCode = VifNextCode(data, pCode, end);
        }

        // Flush any remaining batch
        if (currentBatch.PositionOffset >= 0 && currentBatch.VertexCount > 0)
            batches.Add(currentBatch);

        return batches;
    }

    /// <summary>
    ///     Build Ps2Mesh objects by mapping VIF batches to entry table rows.
    ///     Each VIF batch becomes a separate mesh with a continuous triangle strip.
    /// </summary>
    private static List<(Ps2Mesh mesh, int entryIndex)> BuildMeshes(
        byte[] data, EntryRecord[] entries, List<VifBatchRecord> batches)
    {
        var result = new List<(Ps2Mesh, int)>();

        foreach (var batch in batches)
        {
            if (batch.VertexCount == 0 || batch.PositionOffset < 0) continue;

            var entryIdx = Math.Min(batch.SetupIndex, entries.Length - 1);
            var entry = entries[entryIdx];
            var vertices = ExtractBatchVertices(data, batch, entry.HasVertexColors);

            if (vertices.Length == 0) continue;

            result.Add((new Ps2Mesh
            {
                Checksum = entry.MaterialChecksum,
                MaterialChecksum = entry.MaterialChecksum,
                Vertices = vertices
            }, entryIdx));
        }

        return result;
    }

    /// <summary>
    ///     Extract vertices from a single VIF batch.
    ///     Each batch is a continuous triangle strip — no per-vertex strip restart flags.
    ///     The VU1 microprogram processes the entire batch as one unbroken strip via XGKICK.
    /// </summary>
    private static Ps2Vertex[] ExtractBatchVertices(byte[] data, VifBatchRecord batch, bool hasColors)
    {
        var count = batch.VertexCount;
        if (count == 0) return [];

        var vertices = new Ps2Vertex[count];
        var written = 0;

        for (var i = 0; i < count; i++)
        {
            var posOff = batch.PositionOffset + i * 6;
            if (posOff + 6 > data.Length) break;
            var x = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOff)) * PositionScale;
            var y = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOff + 2)) * PositionScale;
            var z = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOff + 4)) * PositionScale;

            var normal = Vector3.UnitY;
            var hasNormal = false;
            if (batch.NormalOffset >= 0)
            {
                var nrmOff = batch.NormalOffset + i * 3;
                if (nrmOff + 3 <= data.Length)
                {
                    var n = new Vector3(
                        (sbyte)data[nrmOff] * NormalScale,
                        (sbyte)data[nrmOff + 1] * NormalScale,
                        (sbyte)data[nrmOff + 2] * NormalScale);
                    var len = n.Length();
                    normal = len > 0.001f ? n / len : Vector3.UnitY;
                    hasNormal = true;
                }
            }

            var u = 0f;
            var v = 0f;
            var hasUv = false;
            if (batch.UvAdcOffset >= 0)
            {
                var uvOff = batch.UvAdcOffset + i * 8;
                if (uvOff + 4 <= data.Length)
                {
                    u = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(uvOff)) * UvScale;
                    v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(uvOff + 2)) * UvScale;
                    hasUv = true;
                }
            }

            vertices[written++] = new Ps2Vertex(
                new Vector3(x, y, z), normal,
                128, 128, 128, 128, u, v,
                hasNormal, hasColors, hasUv,
                isStripRestart: false);
        }

        return written == count ? vertices : vertices[..written];
    }

    /// <summary>Advance past one VIF opcode. Port of vif::NextCode from vif.cpp.</summary>
    private static int VifNextCode(byte[] data, int offset, int end)
    {
        if (offset >= end || offset + 4 > data.Length)
            return end;

        var cmd = data[offset + 3];

        if ((cmd & 0x60) != 0x60)
        {
            return (cmd & 0x7F) switch
            {
                0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                    or 0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x17 => offset + 4,
                0x20 => offset + 8, // STMASK
                0x30 or 0x31 => offset + 20, // STROW/STCOL
                0x4A => offset + (data[offset + 2] << 3) + 4, // MPG
                0x50 or 0x51 => // DIRECT/DIRECTHL
                    offset + (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) << 4) + 4,
                _ => end
            };
        }

        // UNPACK
        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[offset + 2];
        var dimension = vn + 1;
        var bitLength = 32 >> vl;
        var dataSize = ((bitLength * dimension * num + 31) >> 5) << 2;
        return offset + 4 + dataSize;
    }

    private struct EntryRecord
    {
        public uint MaterialChecksum;
        public uint MaterialFlags;
        public uint GsAlphaLow;
        public uint GsAlphaHigh;
        public uint TextureChecksum;
        public uint OwnerObjectChecksum;
        public bool HasVertexColors;
    }

    private struct VifBatchRecord
    {
        public int PositionOffset;
        public int NormalOffset;
        public int UvAdcOffset;
        public int VertexCount;
        public int SetupIndex;

        public VifBatchRecord()
        {
            PositionOffset = -1;
            NormalOffset = -1;
            UvAdcOffset = -1;
            VertexCount = 0;
            SetupIndex = -1;
        }
    }

    private struct GsRegisters
    {
        public uint Tex0Cbp;  // CLUT base pointer — unique per texture binding
        public ulong Alpha1;  // GS blend mode register
    }
}
