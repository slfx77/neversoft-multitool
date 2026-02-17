using System.Text;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
/// Extracts textures from Neversoft BON (Bone) model archives.
/// BON files bundle textures, mesh geometry, and skeleton data into a single file.
/// Supports Xbox versions 3/4 (DDS textures) and Dreamcast version 1 (PVR textures decoded to PNG).
/// </summary>
public static class BonArchive
{
    private static readonly byte[] Magic = "Bon\0"u8.ToArray();

    /// <summary>
    /// Reads the texture list from a BON archive.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string bonPath)
    {
        using var stream = File.OpenRead(bonPath);
        using var reader = new BinaryReader(stream);

        var (version, textureCount) = ReadHeader(reader);

        return version == 1
            ? ReadV1Entries(reader, stream, textureCount)
            : ReadV3V4Entries(reader, stream, textureCount);
    }

    /// <summary>
    /// Extracts all textures from a BON archive.
    /// Xbox (v3/v4): extracts raw DDS files.
    /// Dreamcast (v1): decodes PVR textures to PNG.
    /// </summary>
    public static void ExtractFiles(string bonPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        using var stream = File.OpenRead(bonPath);
        using var reader = new BinaryReader(stream);

        var (version, textureCount) = ReadHeader(reader);
        var entries = version == 1
            ? ReadV1Entries(reader, stream, textureCount)
            : ReadV3V4Entries(reader, stream, textureCount);

        var archiveName = Path.GetFileNameWithoutExtension(bonPath);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];

            if (version == 1)
            {
                // Decode PVR texture to PNG
                var pngName = Path.ChangeExtension(entry.Name, ".png");
                var exportPath = Path.Combine(outputDir, archiveName, pngName);
                var exportDir = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(exportDir))
                    Directory.CreateDirectory(exportDir);

                PvrFileDecoder.DecodeToPng(reader, entry.Offset, exportPath);
            }
            else
            {
                // Extract raw DDS file
                stream.Seek(entry.Offset, SeekOrigin.Begin);
                var exportPath = Path.Combine(outputDir, archiveName, entry.Name);
                var exportDir = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(exportDir))
                    Directory.CreateDirectory(exportDir);

                var data = new byte[entry.Size];
                stream.ReadExactly(data);
                File.WriteAllBytes(exportPath, data);
            }

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }

    private static (uint Version, int TextureCount) ReadHeader(BinaryReader reader)
    {
        var magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid BON file: missing 'Bon\\0' magic");

        var version = reader.ReadUInt32();
        if (version is not (1 or 3 or 4))
            throw new NotSupportedException($"Unsupported BON version: {version}");

        var textureCount = version == 3
            ? reader.ReadUInt16()
            : (int)reader.ReadUInt32();

        return (version, textureCount);
    }

    /// <summary>
    /// Parses Dreamcast BON v1 texture entries.
    /// V1 uses uint8 name lengths, 7 material floats, a hasTexture flag,
    /// and embeds PVR textures (GBIX+PVRT) instead of DDS.
    /// </summary>
    private static List<ArchiveEntry> ReadV1Entries(BinaryReader reader, Stream stream, int textureCount)
    {
        var entries = new List<ArchiveEntry>(textureCount);

        for (var i = 0; i < textureCount; i++)
        {
            // Part name (uint8 length + chars, NOT null-terminated)
            var nameLen = reader.ReadByte();
            reader.ReadBytes(nameLen); // skip display name (e.g. "Shirt", "Face")

            // 7 material floats (RGBA tint + unknown + specular + glossiness)
            reader.ReadBytes(7 * 4);

            // Flags
            reader.ReadByte(); // flag1 (always 0)
            var hasTexture = reader.ReadByte();

            if (hasTexture == 0)
                continue; // No texture data for this entry

            // Dev build path (uint8 length + chars)
            var pathLen = reader.ReadByte();
            var pathBytes = reader.ReadBytes(pathLen);
            var devPath = Encoding.ASCII.GetString(pathBytes);

            // 3 flag bytes
            reader.ReadBytes(3);

            // PVR data size and offset
            var pvrSize = reader.ReadUInt32();
            var pvrOffset = stream.Position;

            // Use the filename stem from the dev path as the entry name
            var textureName = Path.GetFileNameWithoutExtension(devPath);
            if (string.IsNullOrEmpty(textureName))
                textureName = $"texture_{entries.Count}";

            entries.Add(new ArchiveEntry
            {
                Name = textureName + ".pvr",
                Size = pvrSize,
                Offset = pvrOffset
            });

            // Skip past the PVR data
            stream.Seek(pvrSize, SeekOrigin.Current);
        }

        return entries;
    }

    /// <summary>
    /// Parses Xbox BON v3/v4 texture entries.
    /// V3/V4 use uint16 name lengths, RGBA bytes + 2 floats + 1 flag,
    /// and embed DDS textures.
    /// </summary>
    private static List<ArchiveEntry> ReadV3V4Entries(BinaryReader reader, Stream stream, int textureCount)
    {
        var entries = new List<ArchiveEntry>(textureCount);

        for (var i = 0; i < textureCount; i++)
        {
            // Display name (uint16 length + chars)
            var displayNameLen = reader.ReadUInt16();
            reader.ReadBytes(displayNameLen);

            // Material properties: RGBA (4 bytes) + 2 floats + 1 flag byte
            reader.ReadBytes(4);  // RGBA color
            reader.ReadBytes(4);  // float specular
            reader.ReadBytes(4);  // float glossiness
            reader.ReadByte();    // unknown flag

            // Internal name (uint16 length + chars)
            var internalNameLen = reader.ReadUInt16();
            var internalNameBytes = reader.ReadBytes(internalNameLen);
            var internalName = Encoding.ASCII.GetString(internalNameBytes);

            // 3 flag bytes + DDS size
            reader.ReadBytes(3);
            var ddsSize = reader.ReadUInt32();
            var ddsOffset = stream.Position;

            entries.Add(new ArchiveEntry
            {
                Name = internalName + ".DDS",
                Size = ddsSize,
                Offset = ddsOffset
            });

            // Skip past the DDS data
            stream.Seek(ddsSize, SeekOrigin.Current);
        }

        return entries;
    }
}
