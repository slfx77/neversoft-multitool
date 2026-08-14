using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public sealed class BinaryReaderExtensionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadFixedString_TruncatedField_ThrowsExactEndOfStream(
        bool containsEarlyNull)
    {
        byte[] bytes = containsEarlyNull ? [(byte)'A', 0] : [(byte)'A', (byte)'B'];
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);

        var exception = Assert.Throws<EndOfStreamException>(
            () => reader.ReadFixedString(3));

        Assert.Equal(
            "Expected 3 bytes for fixed-length string, but read 2.",
            exception.Message);
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void ReadFixedString_ExactFieldWithNull_ConsumesFullFieldOnly()
    {
        byte[] bytes = [(byte)'A', 0, (byte)'X', (byte)'Y', 0x7F];
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);

        var value = reader.ReadFixedString(4);

        Assert.Equal("A", value);
        Assert.Equal(4, stream.Position);
        Assert.Equal((byte)0x7F, reader.ReadByte());
    }

    [Fact]
    public void ReadFixedString_ExactFieldWithoutNull_ReturnsWholeField()
    {
        using var stream = new MemoryStream("ABCD"u8.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);

        var value = reader.ReadFixedString(4);

        Assert.Equal("ABCD", value);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void ReadFixedString_ZeroLength_ReturnsEmptyWithoutConsumingInput()
    {
        using var stream = new MemoryStream([(byte)'A'], writable: false);
        using var reader = new BinaryReader(stream);

        var value = reader.ReadFixedString(0);

        Assert.Equal(string.Empty, value);
        Assert.Equal(0, stream.Position);
        Assert.Equal((byte)'A', reader.ReadByte());
    }
}
