using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.RenderWare;

public sealed class RwTxdArchiveQualityTests(TestPaths paths)
{
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    [CorpusFact]
    public void PedProDictionaries_FromSkate3Wad_DecodeTheirAuthoredTopLevelRasters()
    {
        var wadPath = paths.FindSampleFile(Thps3Build, "SKATE3.WAD");
        Assert.SkipWhen(wadPath == null, "THPS3 SKATE3.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;

        var pedProDictionaries = backend.Entries
            .Where(static entry =>
                entry.Directory.StartsWith("Models/PedPro_", StringComparison.OrdinalIgnoreCase)
                && entry.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(13, pedProDictionaries.Length);
        foreach (var entry in pedProDictionaries)
        {
            var sourceEntry = backend.FindEntry(Path.ChangeExtension(entry.Name, ".SKN"));
            Assert.NotNull(sourceEntry);
            var source = new ArchiveAssetSource(backend, sourceEntry);
            var bytes = source.TryReadCompanion(entry.Name);
            Assert.NotNull(bytes);
            Assert.Equal(backend.ReadEntryBytes(entry), bytes);

            var nativeRasters = ReadNativeRasterFacts(bytes);
            Assert.InRange(nativeRasters.Count, 6, 8);
            Assert.All(nativeRasters, static raster =>
            {
                Assert.Equal(32, raster.Width);
                Assert.Equal(32, raster.Height);
                Assert.Equal(4, raster.Depth);
                Assert.Equal(0x4104, raster.RasterFormat);
                Assert.Equal(0, raster.RasterVersion);

                // The native pixel payload is exactly one complete 32x32 PSMT4
                // image. There are no lower levels appended for the reader to
                // accidentally select in place of the authored top level.
                Assert.Equal(raster.Width * raster.Height * raster.Depth / 8, raster.PixelSize);
                Assert.Equal(32, raster.PaletteSize);
                Assert.Equal(raster.PixelSize + raster.PaletteSize, raster.DataChunkSize);
                Assert.Equal(0, raster.RasterBytesAfterData);
            });

            var decoded = RwTxdFile.Parse(bytes);
            Assert.True(decoded.Success, decoded.ErrorMessage);
            Assert.Equal(nativeRasters.Count, decoded.Textures.Count);
            Assert.All(decoded.Textures, static texture =>
            {
                Assert.Equal(32, texture.Width);
                Assert.Equal(32, texture.Height);
                Assert.Equal(0x14u, texture.Psm);
                Assert.NotNull(texture.Pixels);
                Assert.Equal(32 * 32 * 4, texture.Pixels.Length);
            });
        }
    }

    [CorpusFact]
    public void PedProMuska_FromSkate3Wad_ReferencesTheLowRastersInItsCompanionDictionary()
    {
        var wadPath = paths.FindSampleFile(Thps3Build, "SKATE3.WAD");
        Assert.SkipWhen(wadPath == null, "THPS3 SKATE3.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;

        var meshEntry = backend.FindEntry("PedPro_Muska.SKN");
        Assert.NotNull(meshEntry);
        var source = new ArchiveAssetSource(backend, meshEntry);

        var clump = RwDffFile.Parse(source.ReadBytes());
        Assert.Equal(2_889, clump.Geometries.Sum(static geometry => geometry.Triangles.Length));
        var referencedNames = clump.Geometries
            .SelectMany(static geometry => geometry.Materials)
            .Select(static material => material.TextureName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(8, referencedNames.Length);
        Assert.All(referencedNames,
            static name => Assert.EndsWith("_low.png", name!, StringComparison.OrdinalIgnoreCase));

        var textureBytes = source.TryReadCompanion("PedPro_Muska.tex");
        Assert.NotNull(textureBytes);
        var dictionaryNames = ReadNativeRasterFacts(textureBytes)
            .Select(static raster => raster.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(referencedNames, name => Assert.Contains(name!, dictionaryNames));
    }

    private static List<NativeRasterFacts> ReadNativeRasterFacts(byte[] data)
    {
        var dictionary = ReadChunk(data, 0);
        Assert.Equal(0x16u, dictionary.Type);

        var dictionaryStruct = ReadChunk(data, dictionary.PayloadOffset);
        Assert.Equal(0x01u, dictionaryStruct.Type);
        var textureCount = BinaryPrimitives.ReadUInt16LittleEndian(
            data.AsSpan(dictionaryStruct.PayloadOffset, sizeof(ushort)));
        var offset = dictionaryStruct.EndOffset;
        var result = new List<NativeRasterFacts>(textureCount);

        for (var i = 0; i < textureCount; i++)
        {
            var native = ReadChunk(data, offset);
            Assert.Equal(0x15u, native.Type);
            var childOffset = native.PayloadOffset;

            var platform = ReadChunk(data, childOffset);
            childOffset = platform.EndOffset;
            var nameChunk = ReadChunk(data, childOffset);
            var name = Encoding.ASCII.GetString(data, nameChunk.PayloadOffset, nameChunk.Size).TrimEnd('\0');
            childOffset = nameChunk.EndOffset;
            var mask = ReadChunk(data, childOffset);
            childOffset = mask.EndOffset;

            var rasterContainer = ReadChunk(data, childOffset);
            var rasterHeader = ReadChunk(data, rasterContainer.PayloadOffset);
            Assert.Equal(0x01u, rasterHeader.Type);
            Assert.True(rasterHeader.Size >= 64);
            var header = rasterHeader.PayloadOffset;

            var pixelData = ReadChunk(data, rasterHeader.EndOffset);
            Assert.Equal(0x01u, pixelData.Type);
            result.Add(new NativeRasterFacts(
                name,
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + 4, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + 8, 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 12, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 14, 2)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + 48, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + 52, 4)),
                pixelData.Size,
                rasterContainer.EndOffset - pixelData.EndOffset));

            offset = native.EndOffset;
        }

        return result;
    }

    private static Chunk ReadChunk(byte[] data, int offset)
    {
        Assert.InRange(offset, 0, data.Length - 12);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4)));
        var payloadOffset = offset + 12;
        var endOffset = checked(payloadOffset + size);
        Assert.InRange(endOffset, payloadOffset, data.Length);
        return new Chunk(type, size, payloadOffset, endOffset);
    }

    private sealed record NativeRasterFacts(
        string Name,
        int Width,
        int Height,
        int Depth,
        ushort RasterFormat,
        ushort RasterVersion,
        int PixelSize,
        int PaletteSize,
        int DataChunkSize,
        int RasterBytesAfterData);

    private readonly record struct Chunk(uint Type, int Size, int PayloadOffset, int EndOffset);
}
