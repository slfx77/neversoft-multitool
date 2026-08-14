using System.IO.Compression;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

public sealed class ArchiveEntryDecoderTests
{
    [Theory]
    [InlineData(ArchiveAssetType.Pkr, "PKR")]
    [InlineData(ArchiveAssetType.Zip, "ZIP")]
    public void Decode_CompressedPayloadSmallerThanDeclaredSize_ThrowsInvalidDataException(
        ArchiveAssetType type,
        string formatName)
    {
        var stored = Compress(type, "AB"u8);
        var entry = CreateCompressedEntry(stored.Length, size: 3);

        var exception = Assert.Throws<InvalidDataException>(
            () => ArchiveEntryDecoder.Decode(type, entry, stored));

        Assert.Equal(
            $"Decompressed {formatName} entry is shorter than its declared size of 3 bytes.",
            exception.Message);
        Assert.IsType<EndOfStreamException>(exception.InnerException);
    }

    [Theory]
    [InlineData(ArchiveAssetType.Pkr)]
    [InlineData(ArchiveAssetType.Zip)]
    public void Decode_CompressedPayloadLargerThanDeclaredSize_ThrowsInsteadOfTruncating(
        ArchiveAssetType type)
    {
        var stored = Compress(type, "ABC"u8);
        var entry = CreateCompressedEntry(stored.Length, size: 2);

        var exception = Assert.Throws<InvalidDataException>(
            () => ArchiveEntryDecoder.Decode(type, entry, stored));

        Assert.Contains("exceeds its declared size of 2 bytes", exception.Message);
    }

    [Theory]
    [InlineData(ArchiveAssetType.Pkr)]
    [InlineData(ArchiveAssetType.Zip)]
    public void Decode_CompressedPayloadMatchingDeclaredSize_ReturnsEveryByte(
        ArchiveAssetType type)
    {
        var stored = Compress(type, "ABC"u8);
        var entry = CreateCompressedEntry(stored.Length, size: 3);

        var decoded = ArchiveEntryDecoder.Decode(type, entry, stored);

        Assert.Equal("ABC"u8.ToArray(), decoded);
    }

    [Theory]
    [InlineData(ArchiveAssetType.Pkr)]
    [InlineData(ArchiveAssetType.Zip)]
    public void Decode_TrailingCompressedInputPadding_RemainsAllowed(
        ArchiveAssetType type)
    {
        var compressed = Compress(type, "ABC"u8);
        var stored = new byte[compressed.Length + 2];
        compressed.CopyTo(stored, 0);
        stored[^2] = 0xDE;
        stored[^1] = 0xAD;
        var entry = CreateCompressedEntry(stored.Length, size: 3);

        var decoded = ArchiveEntryDecoder.Decode(type, entry, stored);

        Assert.Equal("ABC"u8.ToArray(), decoded);
    }

    private static ArchiveEntry CreateCompressedEntry(int compressedSize, int size)
    {
        return new ArchiveEntry
        {
            Name = "fixture.bin",
            Size = size,
            CompressedSize = compressedSize,
            IsCompressed = true
        };
    }

    private static byte[] Compress(ArchiveAssetType type, ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (Stream compressor = type switch
               {
                   ArchiveAssetType.Pkr => new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
                   ArchiveAssetType.Zip => new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
                   _ => throw new ArgumentOutOfRangeException(nameof(type))
               })
        {
            compressor.Write(payload);
        }

        return output.ToArray();
    }
}
