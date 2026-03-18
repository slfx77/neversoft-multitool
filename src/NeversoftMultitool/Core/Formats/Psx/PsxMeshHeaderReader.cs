using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

internal static class PsxMeshHeaderReader
{
    /// <summary>
    ///     Reads the PSX file header: objects, mesh pointers, tagged chunks, name hashes,
    ///     and texture hashes. Does NOT read mesh geometry data.
    /// </summary>
    internal static PsxMeshHeader? Parse(BinaryReader reader)
    {
        var version = reader.ReadUInt16();
        if (version is not (0x03 or 0x04 or 0x06))
            return null;

        var magic = reader.ReadUInt16();
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
            out var gouraudPalette);

        var meshNameHashes = new uint[meshCount];
        for (uint i = 0; i < meshCount; i++)
            meshNameHashes[i] = reader.ReadUInt32();

        var textureHashCount = reader.ReadUInt32();
        var textureHashes = new uint[textureHashCount];
        for (uint i = 0; i < textureHashCount; i++)
            textureHashes[i] = reader.ReadUInt32();

        const float baseScale = 2.25f;
        var scaleDivisor = hasHierarchy ? baseScale * 16f : baseScale;

        if (hierarchyParents != null)
        {
            for (var i = 0; i < Math.Min(hierarchyParents.Length, objects.Count); i++)
                objects[i].ParentIndex = hierarchyParents[i] != i ? hierarchyParents[i] : -1;
        }

        return new PsxMeshHeader
        {
            Version = version,
            Objects = objects,
            MeshTopPointers = meshTopPointers,
            MeshNameHashes = meshNameHashes,
            TextureHashes = textureHashes,
            GouraudPalette = gouraudPalette,
            HasHierarchy = hasHierarchy,
            ScaleDivisor = scaleDivisor,
            TranslationDivisor = baseScale
        };
    }

    private static PsxMeshObject ReadObject(BinaryReader reader)
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

    private static ushort[]? ReadTaggedChunks(BinaryReader reader, uint objectCount,
        out bool hasHierarchy, out Vector4[]? gouraudPalette)
    {
        const uint TagStop = 0xFFFFFFFF;
        const uint TagHIER = 'H' | ((uint)'I' << 8) | ((uint)'E' << 16) | ((uint)'R' << 24);
        const uint TagRgbs = 'R' | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'s' << 24);

        hasHierarchy = false;
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
            else if (tag == TagRgbs)
            {
                var count = Math.Min(length / 4, 256u);
                gouraudPalette = new Vector4[count];
                var specialStarted = false;
                for (uint i = 0; i < count; i++)
                {
                    var r = reader.ReadByte();
                    var g = reader.ReadByte();
                    var b = reader.ReadByte();
                    reader.ReadByte();

                    if (r == 0 && g == 0 && b == 0)
                    {
                        gouraudPalette[i] = specialStarted
                            ? new Vector4(0.5f, 0.5f, 0.5f, 1f)
                            : new Vector4(0f, 0f, 0f, 1f);
                        specialStarted = true;
                    }
                    else
                    {
                        gouraudPalette[i] = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                    }
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
