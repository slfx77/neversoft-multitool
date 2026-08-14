using System.Text;

namespace NeversoftMultitool.Core.Formats.Texture.Psx;

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

        try
        {
            var meshHashes = ReadModelDataWithHashes(reader);
            var textureHashes = ReadHashArray(reader, "texture hash list");
            SkipCountedRecords(reader, 36, "16-color palette list");
            SkipCountedRecords(reader, 516, "256-color palette list");

            string[]? detailNames = null;
            string[]? cubemapNames = null;

            var numActualTex = ReadUInt32Exact(reader, "texture count");
            if (numActualTex == 0xFFFFFFFF)
            {
                detailNames = ReadExtendedNames(reader, "detail texture", sizeof(uint));
                cubemapNames = ReadExtendedNames(reader, "cubemap", sizeof(uint));
                numActualTex = ReadUInt32Exact(reader, "actual texture count");
            }

            GetCountedBlockLength(reader, numActualTex, sizeof(uint), 0, "texture top-pointer table");

            return new PsxHashEnumeration
            {
                MeshNameHashes = meshHashes,
                TextureNameHashes = textureHashes,
                DetailTextureNames = detailNames,
                CubemapNames = cubemapNames
            };
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads model data and returns mesh name hashes instead of skipping them.
    ///     Same parsing logic as <see cref="PsxLibrary.SkipModelData" /> but captures the hash values.
    /// </summary>
    internal static uint[] ReadModelDataWithHashes(BinaryReader reader)
    {
        var ptrMeta = ReadUInt32Exact(reader, "model metadata pointer");
        var objCount = ReadUInt32Exact(reader, "object count");

        var objectBytes = GetCountedBlockLength(reader, objCount, 36, sizeof(uint), "object list");
        SkipExact(reader, objectBytes, "object list");

        var meshCount = ReadUInt32Exact(reader, "mesh count");
        var meshPointerBytes = GetCountedBlockLength(
            reader, meshCount, sizeof(uint), 0, "mesh top-pointer table");
        var minimumMetadataOffset = reader.BaseStream.Position + meshPointerBytes;
        if (ptrMeta < minimumMetadataOffset)
            throw new InvalidDataException("PSX model metadata pointer overlaps the mesh top-pointer table");

        SeekAbsolute(reader, ptrMeta, sizeof(uint), "model metadata pointer");
        var chunkCount = -1;
        while (true)
        {
            var magic = ReadBytesExact(reader, sizeof(uint), "tagged chunk marker");
            chunkCount++;
            if (magic[0] != 0xFF || magic[1] != 0xFF || magic[2] != 0xFF || magic[3] != 0xFF)
            {
                var unkLength = ReadUInt32Exact(reader, "tagged chunk length");
                RequireRemaining(reader, unkLength, "tagged chunk payload");
                SkipExact(reader, unkLength, "tagged chunk payload");
                if (chunkCount > 16)
                    throw new InvalidDataException(
                        "Unable to parse PSX texture library, cannot find texture data");
            }
            else
            {
                break;
            }
        }

        return ReadHashArray(reader, meshCount, "mesh hash list");
    }

    private static uint[] ReadHashArray(BinaryReader reader, string description)
    {
        var count = ReadUInt32Exact(reader, $"{description} count");
        return ReadHashArray(reader, count, description);
    }

    private static uint[] ReadHashArray(BinaryReader reader, uint count, string description)
    {
        if ((ulong)count > (ulong)Array.MaxLength)
            throw new InvalidDataException($"PSX {description} count is too large");

        GetCountedBlockLength(reader, count, sizeof(uint), 0, description);
        var hashes = new uint[(int)count];
        for (var i = 0; i < hashes.Length; i++)
            hashes[i] = reader.ReadUInt32();

        return hashes;
    }

    private static void SkipCountedRecords(BinaryReader reader, int recordSize, string description)
    {
        var count = ReadUInt32Exact(reader, $"{description} count");
        var byteCount = GetCountedBlockLength(reader, count, recordSize, 0, description);
        SkipExact(reader, byteCount, description);
    }

    private static string[] ReadExtendedNames(BinaryReader reader, string description, int trailingBytes)
    {
        var count = ReadUInt32Exact(reader, $"{description} count");
        if ((ulong)count > (ulong)Array.MaxLength)
            throw new InvalidDataException($"PSX {description} count is too large");

        GetCountedBlockLength(reader, count, 36, trailingBytes, $"{description} list");
        var names = new string[(int)count];
        for (var i = 0; i < names.Length; i++)
        {
            var nameBytes = ReadBytesExact(reader, 32, $"{description} name");
            names[i] = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            ReadBytesExact(reader, sizeof(uint), $"{description} flags");
        }

        return names;
    }

    private static long GetCountedBlockLength(BinaryReader reader, uint count, int recordSize,
        int trailingBytes, string description)
    {
        var remaining = GetRemaining(reader);
        if (remaining < trailingBytes || count > (remaining - trailingBytes) / recordSize)
            throw new InvalidDataException($"PSX {description} is truncated");

        return (long)count * recordSize;
    }

    private static uint ReadUInt32Exact(BinaryReader reader, string description)
    {
        RequireRemaining(reader, sizeof(uint), description);
        return reader.ReadUInt32();
    }

    private static byte[] ReadBytesExact(BinaryReader reader, int count, string description)
    {
        RequireRemaining(reader, count, description);
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new InvalidDataException($"PSX {description} is truncated");

        return bytes;
    }

    private static void RequireRemaining(BinaryReader reader, long byteCount, string description)
    {
        if (byteCount < 0 || byteCount > GetRemaining(reader))
            throw new InvalidDataException($"PSX {description} is truncated");
    }

    private static long GetRemaining(BinaryReader reader)
    {
        var position = reader.BaseStream.Position;
        var length = reader.BaseStream.Length;
        if (position < 0 || position > length)
            throw new InvalidDataException("PSX reader position is outside the file");

        return length - position;
    }

    private static void SkipExact(BinaryReader reader, long byteCount, string description)
    {
        RequireRemaining(reader, byteCount, description);
        Span<byte> buffer = stackalloc byte[4096];
        while (byteCount > 0)
        {
            var requested = (int)Math.Min(byteCount, buffer.Length);
            var read = reader.Read(buffer[..requested]);
            if (read == 0)
                throw new InvalidDataException($"PSX {description} is truncated");

            byteCount -= read;
        }
    }

    private static void SeekAbsolute(BinaryReader reader, uint offset, int requiredBytes, string description)
    {
        var length = reader.BaseStream.Length;
        if (offset > length || requiredBytes > length - offset)
            throw new InvalidDataException($"PSX {description} is outside the file");

        reader.BaseStream.Seek(offset, SeekOrigin.Begin);
    }
}
