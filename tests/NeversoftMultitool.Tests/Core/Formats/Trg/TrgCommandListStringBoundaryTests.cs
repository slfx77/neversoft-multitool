using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Trg;

public sealed class TrgCommandListStringBoundaryTests
{
    [Theory]
    [InlineData(0x007E)]
    [InlineData(0x00BF)]
    [InlineData(0x0002)]
    public void ParseCommandList_StringTerminatorOutsideSlice_ThrowsWithoutBorrowing(int opcode)
    {
        var bytes = BuildStringOperation(opcode, bigEndian: false, [(byte)'A', 0]);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian: false);

        var exception = Assert.Throws<InvalidDataException>(
            () => TrgCommandList.ParseCommandList(reader, maxBytes: 3));

        Assert.Equal("TRG string at position 2 is not NUL-terminated within boundary 3.", exception.Message);
        Assert.Equal(3, stream.Position);
    }

    [Theory]
    [InlineData(0x4200)]
    [InlineData(0x42B0)]
    public void ParseScript_StringTerminatorOutsideSlice_ThrowsWithoutBorrowing(int opcode)
    {
        var bytes = BuildStringOperation(opcode, bigEndian: false, [(byte)'A', 0]);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian: false);

        var exception = Assert.Throws<InvalidDataException>(
            () => TrgCommandList.ParseScript(reader, maxBytes: 3));

        Assert.Equal("TRG string at position 2 is not NUL-terminated within boundary 3.", exception.Message);
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void ParseCommandList_UnterminatedStringAtPhysicalEnd_ThrowsInvalidDataException()
    {
        var bytes = BuildStringOperation(0x007E, bigEndian: false, [(byte)'A']);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian: false);

        var exception = Assert.Throws<InvalidDataException>(
            () => TrgCommandList.ParseCommandList(reader, maxBytes: 4));

        Assert.Equal("TRG string at position 2 is not NUL-terminated within boundary 4.", exception.Message);
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void ParseCommandList_AlignmentOutsideSlice_ThrowsWithoutSeekingPastTerminator()
    {
        var bytes = BuildStringOperation(0x007E, bigEndian: false, [0, 0]);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian: false);

        var exception = Assert.Throws<InvalidDataException>(
            () => TrgCommandList.ParseCommandList(reader, maxBytes: 3));

        Assert.Equal(
            "TRG string at position 2 cannot align to position 4 within boundary 3 and stream length 4.",
            exception.Message);
        Assert.Equal(3, stream.Position);
    }

    [Theory]
    [InlineData(0x007E, false)]
    [InlineData(0x00BF, false)]
    [InlineData(0x0002, false)]
    [InlineData(0x007E, true)]
    public void ParseCommandList_OwnedTerminator_ParsesStringInEitherByteOrder(int opcode, bool bigEndian)
    {
        var bytes = BuildStringOperation(opcode, bigEndian, [(byte)'A', 0]);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian);

        var command = Assert.Single(TrgCommandList.ParseCommandList(reader, maxBytes: bytes.Length));

        Assert.Equal(opcode, command.Opcode);
        Assert.Equal("A", Assert.Single(command.Args!));
        Assert.Equal(4, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseScript_OwnedTerminatorAndAlignment_ParseEmptyString(bool bigEndian)
    {
        var bytes = BuildStringOperation(0x4200, bigEndian, [0, 0]);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian);

        var op = Assert.Single(TrgCommandList.ParseScript(reader, maxBytes: bytes.Length));

        Assert.Equal(string.Empty, op.Value);
        Assert.Equal(4, stream.Position);
    }

    private static byte[] BuildStringOperation(int opcode, bool bigEndian, ReadOnlySpan<byte> stringBytes)
    {
        var bytes = new byte[2 + stringBytes.Length];
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)opcode));
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)opcode));
        stringBytes.CopyTo(bytes.AsSpan(2));
        return bytes;
    }
}
