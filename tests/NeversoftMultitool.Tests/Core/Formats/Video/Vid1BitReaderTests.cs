using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1BitReaderTests
{
    [Fact]
    public void ReadBits_SingleByte_MsbFirst()
    {
        var reader = new Vid1BitReader([0b1010_0011]);

        Assert.Equal(1, reader.ReadBits(1));
        Assert.Equal(0, reader.ReadBits(1));
        Assert.Equal(0b10, reader.ReadBits(2));
        Assert.Equal(0b0011, reader.ReadBits(4));
    }

    [Fact]
    public void ReadBits_CrossesByteBoundary()
    {
        var reader = new Vid1BitReader([0xFF, 0x00]);

        Assert.Equal(0b1111_1111_0, reader.ReadBits(9));
    }

    [Fact]
    public void ReadBitsUInt32_Reads32Bits()
    {
        var reader = new Vid1BitReader([0xDE, 0xAD, 0xBE, 0xEF, 0x12]);

        Assert.Equal(0xDEADBEEFu, reader.ReadBitsUInt32());
        Assert.Equal(4, reader.BytesConsumed);
    }

    [Fact]
    public void ReadFlag_ReturnsCorrectBool()
    {
        var reader = new Vid1BitReader([0b1000_0000]);

        Assert.True(reader.ReadFlag());
        Assert.False(reader.ReadFlag());
    }

    [Fact]
    public void AlignToNextByte_SkipsRemainder()
    {
        var reader = new Vid1BitReader([0xFF, 0xAB]);

        reader.ReadBits(3);
        reader.AlignToNextByte();

        Assert.Equal(1, reader.BytesConsumed);
        Assert.Equal(0xAB, reader.ReadBits(8));
    }

    [Fact]
    public void ReadBits_PastEnd_ThrowsEndOfStream()
    {
        var reader = new Vid1BitReader([0xFF]);

        Assert.Throws<EndOfStreamException>(() => reader.ReadBits(9));
    }

    [Fact]
    public void BitPosition_TracksCorrectly()
    {
        var reader = new Vid1BitReader([0xFF, 0x00]);

        Assert.Equal(0, reader.BitPosition);
        reader.ReadBits(5);
        Assert.Equal(5, reader.BitPosition);
        reader.ReadBits(3);
        Assert.Equal(8, reader.BitPosition);
    }
}
