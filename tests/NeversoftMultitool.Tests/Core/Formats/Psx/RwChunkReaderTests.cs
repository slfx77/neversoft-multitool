using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public sealed class RwChunkReaderTests
{
    [Fact]
    public void ReadChunkHeader_ReturnsCorrectValues()
    {
        // Clump header: type=0x0010, size=0x1234, version=0x0310
        var data = new byte[12];
        BitConverter.GetBytes((uint)0x0010).CopyTo(data, 0);
        BitConverter.GetBytes((uint)0x1234).CopyTo(data, 4);
        BitConverter.GetBytes((uint)0x0310).CopyTo(data, 8);

        var offset = 0;
        var (type, size, version) = RwChunkReader.ReadChunkHeader(data, ref offset);

        Assert.Equal(0x0010u, type);
        Assert.Equal(0x1234u, size);
        Assert.Equal(0x0310u, version);
        Assert.Equal(12, offset);
    }

    [Fact]
    public void TryReadStruct_MatchesStructType()
    {
        var data = new byte[12];
        BitConverter.GetBytes(RwChunkReader.RW_STRUCT).CopyTo(data, 0);
        BitConverter.GetBytes((uint)100).CopyTo(data, 4);
        BitConverter.GetBytes((uint)0x0310).CopyTo(data, 8);

        var offset = 0;
        var result = RwChunkReader.TryReadStruct(data, ref offset, data.Length, out var type, out var size);

        Assert.True(result);
        Assert.Equal(RwChunkReader.RW_STRUCT, type);
        Assert.Equal(100u, size);
    }

    [Fact]
    public void TryReadStruct_FailsOnNonStruct()
    {
        var data = new byte[12];
        BitConverter.GetBytes(RwChunkReader.RW_STRING).CopyTo(data, 0);
        BitConverter.GetBytes((uint)10).CopyTo(data, 4);
        BitConverter.GetBytes((uint)0x0310).CopyTo(data, 8);

        var offset = 0;
        var result = RwChunkReader.TryReadStruct(data, ref offset, data.Length, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryReadStruct_FailsOnInsufficientData()
    {
        var data = new byte[8]; // too short for 12-byte header

        var offset = 0;
        var result = RwChunkReader.TryReadStruct(data, ref offset, data.Length, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryReadChunk_MatchesExpectedType()
    {
        var data = new byte[12];
        BitConverter.GetBytes(RwChunkReader.RW_CLUMP).CopyTo(data, 0);
        BitConverter.GetBytes((uint)500).CopyTo(data, 4);
        BitConverter.GetBytes((uint)0x0310).CopyTo(data, 8);

        var offset = 0;
        var result = RwChunkReader.TryReadChunk(data, ref offset, data.Length,
            RwChunkReader.RW_CLUMP, out var size);

        Assert.True(result);
        Assert.Equal(500u, size);
    }

    [Fact]
    public void TryReadChunk_FailsOnWrongType()
    {
        var data = new byte[12];
        BitConverter.GetBytes(RwChunkReader.RW_GEOMETRY).CopyTo(data, 0);
        BitConverter.GetBytes((uint)500).CopyTo(data, 4);
        BitConverter.GetBytes((uint)0x0310).CopyTo(data, 8);

        var offset = 0;
        var result = RwChunkReader.TryReadChunk(data, ref offset, data.Length,
            RwChunkReader.RW_CLUMP, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryReadAnyChunk_AlwaysSucceeds()
    {
        var data = new byte[12];
        BitConverter.GetBytes((uint)0xABCD).CopyTo(data, 0);
        BitConverter.GetBytes((uint)42).CopyTo(data, 4);
        BitConverter.GetBytes((uint)0).CopyTo(data, 8);

        var offset = 0;
        var result = RwChunkReader.TryReadAnyChunk(data, ref offset, data.Length, out var type, out var size);

        Assert.True(result);
        Assert.Equal(0xABCDu, type);
        Assert.Equal(42u, size);
    }

    [Fact]
    public void ReadNullTerminatedString_ReadsCorrectly()
    {
        var data = "hello\0world"u8.ToArray();
        var result = RwChunkReader.ReadNullTerminatedString(data, 0, data.Length);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ReadNullTerminatedString_RespectsMaxLength()
    {
        var data = "hello world"u8.ToArray();
        var result = RwChunkReader.ReadNullTerminatedString(data, 0, 5);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ReadNullTerminatedString_HandlesEmptyString()
    {
        var data = new byte[] { 0, 0x41, 0x42 };
        var result = RwChunkReader.ReadNullTerminatedString(data, 0, 3);
        Assert.Equal("", result);
    }
}
