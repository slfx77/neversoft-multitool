using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class BinaryProbeReaderTests
{
    [Fact]
    public void TryReadHeader_ReturnsHeaderBytesAndByteCount()
    {
        var filePath = FormatProbeTestHelper.CreateTempFile(".bin", [0x34, 0x12, 0x78, 0x56, 0xEF, 0xCD]);

        try
        {
            var result = BinaryProbeReader.TryReadHeader(filePath, 8, out var header, out var bytesRead);

            Assert.True(result);
            Assert.Equal(6, bytesRead);
            Assert.Equal((byte)0x34, header[0]);
            Assert.Equal((byte)0xCD, header[5]);
            Assert.Equal((byte)0x00, header[6]);
            Assert.Equal((byte)0x00, header[7]);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void TryReadAllBytes_ReturnsFalseForMissingFile()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.bin");

        var result = BinaryProbeReader.TryReadAllBytes(filePath, out var data);

        Assert.False(result);
        Assert.Empty(data);
    }

    [Fact]
    public void TryReadHeader_NegativeLength_ReturnsFalse()
    {
        var result = BinaryProbeReader.TryReadHeader(
            "not-read.bin", -1, out var header, out var bytesRead);

        Assert.False(result);
        Assert.Empty(header);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void TryReadHeader_CappedStream_AccumulatesTheCompleteHeader()
    {
        using var stream = new CappedReadStream([0x10, 0x20, 0x30, 0x40, 0x50, 0x60], 2);

        var result = BinaryProbeReader.TryReadHeader(stream, 6, out var header, out var bytesRead);

        Assert.True(result);
        Assert.Equal(6, bytesRead);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 }, header);
        Assert.Equal(3, stream.ReadCount);
    }

    [Fact]
    public void TryReadHeader_CappedStreamEndingEarly_ReturnsExactCountAndZeroFilledTail()
    {
        using var stream = new CappedReadStream([0xA1, 0xB2, 0xC3], 2);

        var result = BinaryProbeReader.TryReadHeader(stream, 6, out var header, out var bytesRead);

        Assert.True(result);
        Assert.Equal(3, bytesRead);
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0x00, 0x00, 0x00 }, header);
    }

    [Fact]
    public void TryReadHeader_ZeroLengthStreamRequest_SucceedsWithoutReading()
    {
        using var stream = new CappedReadStream([0x01], 1);

        var result = BinaryProbeReader.TryReadHeader(stream, 0, out var header, out var bytesRead);

        Assert.True(result);
        Assert.Empty(header);
        Assert.Equal(0, bytesRead);
        Assert.Equal(0, stream.ReadCount);
    }

    [Fact]
    public void TryReadHeader_StreamReadFailure_ResetsOutputs()
    {
        using var stream = new ThrowingReadStream();

        var result = BinaryProbeReader.TryReadHeader(stream, 4, out var header, out var bytesRead);

        Assert.False(result);
        Assert.Empty(header);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ReadUnsignedIntegerHelpers_ReadLittleEndianValues()
    {
        byte[] data = [0x34, 0x12, 0x78, 0x56, 0xEF, 0xCD, 0xAB, 0x90];

        Assert.Equal((ushort)0x1234, BinaryProbeReader.ReadUInt16(data));
        Assert.Equal(0x56781234u, BinaryProbeReader.ReadUInt32(data));
        Assert.Equal(0x90ABCDEF56781234ul, BinaryProbeReader.ReadUInt64(data));
    }

    private sealed class CappedReadStream(byte[] data, int maximumReadSize) : MemoryStream(data)
    {
        public int ReadCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return base.Read(buffer, offset, Math.Min(count, maximumReadSize));
        }
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("Synthetic read failure");
        }
    }
}
