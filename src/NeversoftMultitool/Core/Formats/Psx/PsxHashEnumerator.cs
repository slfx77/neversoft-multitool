using System.Text;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Enumerates mesh and texture name hashes from PSX files without extracting pixel data.
///     Used by QbKeyCrossRef for hash cross-referencing.
/// </summary>
internal static class PsxHashEnumerator
{
    /// <summary>
    ///     Enumerates all name hashes (mesh + texture) from a PSX file,
    ///     plus any plaintext names from v6 extended headers.
    ///     Returns null if the file is not a valid PSX file.
    /// </summary>
    public static PsxHashEnumeration? EnumerateAllHashes(string inputFile)
    {
        using var stream = File.OpenRead(inputFile);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(4);
        if (!PsxLibrary.IsValidMagic(magic))
            return null;

        var meshHashes = ReadModelDataWithHashes(reader);
        var textureHashes = PsxLibrary.ReadTextureInfo(reader);
        PsxLibrary.ReadPalettes(reader, 16);
        PsxLibrary.ReadPalettes(reader, 256);

        string[]? detailNames = null;
        string[]? cubemapNames = null;

        var numActualTex = reader.ReadUInt32();
        if (numActualTex == 0xFFFFFFFF)
        {
            var detailCount = reader.ReadUInt32();
            detailNames = new string[detailCount];
            for (var i = 0; i < detailCount; i++)
            {
                var nameBytes = reader.ReadBytes(32);
                detailNames[i] = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                reader.ReadBytes(4); // flags
            }

            var cubemapCount = reader.ReadUInt32();
            cubemapNames = new string[cubemapCount];
            for (var i = 0; i < cubemapCount; i++)
            {
                var nameBytes = reader.ReadBytes(32);
                cubemapNames[i] = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                reader.ReadBytes(4); // flags
            }
        }

        return new PsxHashEnumeration
        {
            MeshNameHashes = meshHashes,
            TextureNameHashes = textureHashes,
            DetailTextureNames = detailNames,
            CubemapNames = cubemapNames
        };
    }

    /// <summary>
    ///     Reads model data and returns mesh name hashes instead of skipping them.
    ///     Same parsing logic as <see cref="PsxLibrary.SkipModelData" /> but captures the hash values.
    /// </summary>
    internal static uint[] ReadModelDataWithHashes(BinaryReader reader)
    {
        var ptrMeta = reader.ReadUInt32();
        var objCount = reader.ReadUInt32();

        for (var i = 0; i < objCount; i++)
            reader.ReadBytes(36);

        var meshCount = reader.ReadUInt32();

        reader.BaseStream.Seek(ptrMeta, SeekOrigin.Begin);
        var chunkCount = -1;
        while (true)
        {
            var magic = reader.ReadBytes(4);
            chunkCount++;
            if (magic[0] != 0xFF || magic[1] != 0xFF || magic[2] != 0xFF || magic[3] != 0xFF)
            {
                var unkLength = reader.ReadUInt32();
                reader.ReadBytes((int)unkLength);
                if (chunkCount > 16)
                    throw new InvalidOperationException(
                        "Unable to parse PSX texture library, cannot find texture data");
            }
            else
            {
                break;
            }
        }

        var meshHashes = new uint[meshCount];
        for (var i = 0; i < meshCount; i++)
            meshHashes[i] = reader.ReadUInt32();

        return meshHashes;
    }
}
