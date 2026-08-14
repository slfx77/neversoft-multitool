using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

public sealed class N64AssetCarverPayloadTableValidationTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void CarveStream_ZeroEntryOffsetPastEnd_PreservesRawGroup(int streamLength)
    {
        var stream = new byte[streamLength];
        WriteInt32(stream, 0, 0);
        WriteInt32(stream, 4, 12);
        stream.AsSpan(8).Fill(0xA5);

        var assets = Carve(stream);

        var asset = Assert.Single(assets);
        Assert.Equal("group7.bin", asset.Path);
        Assert.Same(stream, asset.Data);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    public void CarveStream_ZeroEntryOffsetAtEnd_AcceptsEmptyTable(int streamLength)
    {
        var stream = new byte[streamLength];
        WriteInt32(stream, 0, 0);
        WriteInt32(stream, 4, streamLength);

        var assets = Carve(stream);

        Assert.Empty(assets);
    }

    [Fact]
    public void CarveStream_OneEntryTable_EmitsOwnedPayload()
    {
        var stream = new byte[13];
        WriteInt32(stream, 0, 1);
        WriteInt32(stream, 4, 12);
        WriteInt32(stream, 8, 13);
        stream[12] = 0xA5;

        var assets = Carve(stream);

        var asset = Assert.Single(assets);
        Assert.Equal("group7/000.bin", asset.Path);
        Assert.Equal(new byte[] { 0xA5 }, asset.Data);
    }

    private static List<N64AssetCarver.CarvedAsset> Carve(byte[] stream)
    {
        var assets = new List<N64AssetCarver.CarvedAsset>();
        var usedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        N64AssetCarver.CarveStream(stream, 7, assets, usedDirectories);
        return assets;
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }
}
