using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaCompressedLookupTableTests
{
    [Fact]
    public void DecodeCompressedQKeys_LookupWithoutTable_IsRejected()
    {
        byte[] data = [0x00, 0x40, 0x07];
        var offset = 0;

        var exception = Assert.Throws<InvalidDataException>(() =>
            SkaCompressedKeyDecoders.DecodeCompressedQKeys(data, ref offset, data.Length, null));

        Assert.Equal(
            "SKA compressed Q lookup index 7 requires a Q48 compression table.",
            exception.Message);
    }

    [Fact]
    public void DecodeCompressedTKeys_LookupWithoutTable_IsRejected()
    {
        byte[] data = [0xC0, 0x07];
        var offset = 0;

        var exception = Assert.Throws<InvalidDataException>(() =>
            SkaCompressedKeyDecoders.DecodeCompressedTKeys(data, ref offset, data.Length, null));

        Assert.Equal(
            "SKA compressed T lookup index 7 requires a T48 compression table.",
            exception.Message);
    }

    [Fact]
    public void CompressedLookupKeys_WithTable_DecodeTableValues()
    {
        var qEntries = new SkaCompressEntry[256];
        var tEntries = new SkaCompressEntry[256];
        qEntries[7] = new SkaCompressEntry(4096, -8192, 2048);
        tEntries[7] = new SkaCompressEntry(32, -64, 96);
        var table = new SkaCompressTable { Q48 = qEntries, T48 = tEntries };

        byte[] qData = [0x00, 0x40, 0x07];
        var qOffset = 0;
        var qKey = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedQKeys(
            qData, ref qOffset, qData.Length, table));

        Assert.Equal(qData.Length, qOffset);
        Assert.Equal(new Vector3(-0.25f, 0.5f, -0.125f),
            new Vector3(qKey.Rotation.X, qKey.Rotation.Y, qKey.Rotation.Z));

        byte[] tData = [0xC0, 0x07];
        var tOffset = 0;
        var tKey = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedTKeys(
            tData, ref tOffset, tData.Length, table));

        Assert.Equal(tData.Length, tOffset);
        Assert.Equal(new Vector3(1f, -2f, 3f), tKey.Translation);
    }

    [Fact]
    public void CompressedDirectKeys_DoNotRequireLookupTable()
    {
        byte[] qData = [0x05, 0x00, 0x00, 0x10, 0x00, 0xE0, 0x00, 0x08];
        var qOffset = 0;
        var qKey = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedQKeys(
            qData, ref qOffset, qData.Length, null));

        Assert.Equal(qData.Length, qOffset);
        Assert.Equal(5f / 60f, qKey.Time);

        byte[] tData = [0x45, 0x20, 0x00, 0xC0, 0xFF, 0x60, 0x00];
        var tOffset = 0;
        var tKey = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedTKeys(
            tData, ref tOffset, tData.Length, null));

        Assert.Equal(tData.Length, tOffset);
        Assert.Equal(5f / 60f, tKey.Time);
        Assert.Equal(new Vector3(1f, -2f, 3f), tKey.Translation);
    }
}
