using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal static class PsxMeshHeaderReader
{
    /// <summary>
    ///     Reads the PSX file header: objects, mesh pointers, tagged chunks, name hashes,
    ///     and texture hashes. Does NOT read mesh geometry data.
    /// </summary>
    /// <summary>
    ///     <paramref name="hasGeometry" /> declares whether the file still
    ///     carries its mesh blocks. It is false for carved N64 shells, whose
    ///     geometry chunks AND per-mesh pointer array are stripped: the
    ///     Apocalypse-v3 test probes a real mesh header, so with no geometry to
    ///     probe it would dereference whatever bytes follow (measured: pointer
    ///     values 42, 300, 1) and return an arbitrary answer. Declaring it is
    ///     the difference between "not applicable" and a coin flip.
    /// </summary>
    internal static PsxMeshHeader? Parse(EndianBinaryReader reader, bool hasGeometry = true)
    {
        // The leading field is ONE u32 carrying version in its low half and the
        // 0x0002 marker in its high half — not two independent u16s. Reading it
        // as a word is what makes the same code serve the big-endian N64 copy:
        // a PS1 file stores 0x00020004 little-endian (04 00 02 00) and its N64
        // counterpart stores that same value big-endian (00 02 00 04), so the
        // u32 agrees while a pair of u16s would appear exchanged. Measured
        // against hawk.psx and its N64 sibling; on the little-endian path this
        // is bit-for-bit the previous two reads.
        var header = reader.ReadUInt32();
        var version = (ushort)(header & 0xFFFF);
        if (version is not (0x03 or 0x04 or 0x06))
            return null;

        var magic = (ushort)(header >> 16);
        if (magic != 0x0002)
            return null;

        var metaTop = reader.ReadUInt32();
        var objectCount = reader.ReadUInt32();
        if (objectCount == 0)
            return null;

        var objects = new List<PsxMeshObject>((int)objectCount);
        for (uint i = 0; i < objectCount; i++)
            objects.Add(ReadObject(reader));

        var meshCount = reader.ReadUInt32();
        if (meshCount == 0)
            return null;

        var meshTopPointers = new uint[meshCount];
        for (uint i = 0; i < meshCount; i++)
            meshTopPointers[i] = reader.ReadUInt32();

        reader.BaseStream.Seek(metaTop, SeekOrigin.Begin);

        var hierarchyParents = ReadTaggedChunks(
            reader,
            objectCount,
            out var hasHierarchy,
            out var hasAnimChunk,
            out var gouraudPalette);

        var meshNameHashes = new uint[meshCount];
        for (uint i = 0; i < meshCount; i++)
            meshNameHashes[i] = reader.ReadUInt32();

        var textureHashCount = reader.ReadUInt32();
        var textureHashes = new uint[textureHashCount];
        for (uint i = 0; i < textureHashCount; i++)
            textureHashes[i] = reader.ReadUInt32();

        const float baseScale = 2.25f;
        var (isApocalypse, isSuperModel, scaleDivisor) = ClassifyModelScale(
            reader, version, hasGeometry ? meshTopPointers : [], hasAnimChunk, baseScale);
        var formatRevision = ClassifyFormatRevision(version, isApocalypse);
        var hasMeshLodField = version != 0x03 || !isApocalypse;

        if (hierarchyParents != null)
        {
            for (var i = 0; i < Math.Min(hierarchyParents.Length, objects.Count); i++)
                objects[i].ParentIndex = hierarchyParents[i] != i ? hierarchyParents[i] : -1;
        }

        return new PsxMeshHeader
        {
            Version = version,
            FormatRevision = formatRevision,
            Objects = objects,
            MeshTopPointers = meshTopPointers,
            MeshNameHashes = meshNameHashes,
            TextureHashes = textureHashes,
            GouraudPalette = gouraudPalette,
            HasHierarchy = hasHierarchy,
            HasAnimChunk = hasAnimChunk,
            IsSuperModel = isSuperModel,
            HasMeshLodField = hasMeshLodField,
            ScaleDivisor = scaleDivisor,
            TranslationDivisor = baseScale
        };
    }

    /// <summary>
    ///     Decides whether the file is a super (character/animated prop) and
    ///     which vertex scale applies. <c>ProcessNewPSX</c> clears the region's
    ///     <c>IsSuper</c> flag, then sets it only while processing a 0x2A/0x2C
    ///     animation chunk. A HIER chunk merely supplies parent indices and is
    ///     not itself a super marker. Mirroring that flag is more reliable than
    ///     guessing from a part-count ceiling: Spider-Man's Super-Ock and
    ///     Doc Ock have 45/46 animated parts because their articulated
    ///     appendages are built from many separately transformed meshes.
    ///     Super rendering shifts vertex XYZ right by 4 before the GTE
    ///     transform, so supers store vertices ×16 (divisor 36 = 2.25×16).
    ///     One runtime-specific carve-out remains:
    ///     <list type="bullet">
    ///         <item>
    ///             Apocalypse (1998) v3 models use raw vertex coordinates
    ///             (no /16). Detected by probing the first mesh's header — its v3
    ///             lacks the LOD field after the bbox.
    ///         </item>
    ///     </list>
    /// </summary>
    private static (bool IsApocalypse, bool IsSuperModel, float ScaleDivisor) ClassifyModelScale(
        EndianBinaryReader reader,
        ushort version,
        uint[] meshTopPointers,
        bool hasAnimChunk,
        float baseScale)
    {
        var isApocalypse = version == 0x03 && meshTopPointers.Length > 0
                                           && !MajorityProbeV3HasLod(reader, meshTopPointers);
        var isSuperModel = hasAnimChunk;
        var scaleDivisor = isSuperModel && !isApocalypse ? baseScale * 16f : baseScale;
        return (isApocalypse, isSuperModel, scaleDivisor);
    }

    /// <summary>
    ///     Majority vote of the per-mesh LOD-field probe over the first few
    ///     meshes. The field's presence is an exporter-revision trait and is
    ///     uniform across the file, but the probe heuristic can misfire on an
    ///     individual mesh (level meshes with unusual leading vertex types),
    ///     so a single-mesh decision is unreliable.
    /// </summary>
    private static bool MajorityProbeV3HasLod(EndianBinaryReader reader, uint[] meshTopPointers)
    {
        const int sampleCount = 5;
        var votes = 0;
        var samples = 0;
        for (var i = 0; i < meshTopPointers.Length && samples < sampleCount; i++)
        {
            samples++;
            if (ProbeMeshHasLod(reader, meshTopPointers[i]))
                votes++;
        }

        return samples > 0 && votes * 2 >= samples;
    }

    private static PsxMeshFormatRevision ClassifyFormatRevision(ushort version, bool isApocalypse)
    {
        return version switch
        {
            0x03 => isApocalypse ? PsxMeshFormatRevision.ApocalypseV3 : PsxMeshFormatRevision.NeversoftV3,
            0x04 => PsxMeshFormatRevision.NeversoftV4,
            0x06 => PsxMeshFormatRevision.NeversoftV6,
            _ => PsxMeshFormatRevision.Unknown
        };
    }

    /// <summary>
    ///     Probes whether a v3 mesh at the given file offset has a 4-byte LOD/m_gunkl2 field
    ///     between the bounding box and vertex data. Apocalypse (1998) v3 files lack this field.
    ///     Uses the same logic as PsxMeshGeometryReader.ProbeV3HasLod but seeks to the mesh first.
    /// </summary>
    private static bool ProbeMeshHasLod(EndianBinaryReader reader, uint meshOffset)
    {
        var savedPos = reader.BaseStream.Position;
        // v3 mesh header: 4×u32(16) + u32 gunkl1(4) + 6×i16 bbox(12) = 32 bytes to the probe point
        var probePos = meshOffset + 32;
        if (probePos + 12 > reader.BaseStream.Length)
        {
            reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
            return false;
        }

        reader.BaseStream.Seek(probePos, SeekOrigin.Begin);
        var result = PsxMeshGeometryReader.ProbeV3HasLod(reader);
        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
        return result;
    }

    private static PsxMeshObject ReadObject(EndianBinaryReader reader)
    {
        var flags = reader.ReadUInt32();
        var rawX = reader.ReadInt32();
        var rawY = reader.ReadInt32();
        var rawZ = reader.ReadInt32();
        reader.ReadUInt32();
        reader.ReadUInt16();
        var meshIndex = reader.ReadUInt16();
        reader.ReadInt16();
        reader.ReadInt16();
        reader.ReadUInt32();
        reader.ReadUInt32();

        return new PsxMeshObject
        {
            Flags = flags,
            RawX = rawX,
            RawY = rawY,
            RawZ = rawZ,
            MeshIndex = meshIndex
        };
    }

    private static ushort[]? ReadTaggedChunks(EndianBinaryReader reader, uint objectCount,
        out bool hasHierarchy, out bool hasAnimChunk, out Vector4[]? gouraudPalette)
    {
        const uint TagStop = 0xFFFFFFFF;
        const uint TagHIER = 'H' | ((uint)'I' << 8) | ((uint)'E' << 16) | ((uint)'R' << 24);
        const uint TagRgbs = 'R' | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'s' << 24);

        hasHierarchy = false;
        hasAnimChunk = false;
        gouraudPalette = null;
        ushort[]? hierarchyParents = null;

        var tag = reader.ReadUInt32();
        var chunkCount = 0;
        while (tag != TagStop)
        {
            var length = reader.ReadUInt32();
            var chunkStart = reader.BaseStream.Position;

            if (tag == TagHIER)
            {
                hasHierarchy = true;
                var count = Math.Min(length / 2, objectCount);
                hierarchyParents = new ushort[count];
                for (uint i = 0; i < count; i++)
                    hierarchyParents[i] = reader.ReadUInt16();
            }
            else if (tag is PsxMeshFile.HierChunkV1Tag or PsxMeshFile.HierChunkV2Tag)
            {
                hasAnimChunk = true;
            }
            else if (tag == TagRgbs)
            {
                var count = Math.Min(length / 4, 256u);
                gouraudPalette = new Vector4[count];
                for (uint i = 0; i < count; i++)
                {
                    var r = reader.ReadByte();
                    var g = reader.ReadByte();
                    var b = reader.ReadByte();
                    reader.ReadByte();
                    gouraudPalette[i] = new Vector4(
                        r / 255f,
                        g / 255f,
                        b / 255f,
                        1f);
                }
            }

            reader.BaseStream.Seek(chunkStart + length, SeekOrigin.Begin);
            tag = reader.ReadUInt32();

            if (++chunkCount > 16)
                break;
        }

        return hierarchyParents;
    }
}
