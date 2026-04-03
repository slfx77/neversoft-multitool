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
    public void ReadUnsignedIntegerHelpers_ReadLittleEndianValues()
    {
        byte[] data = [0x34, 0x12, 0x78, 0x56, 0xEF, 0xCD, 0xAB, 0x90];

        Assert.Equal((ushort)0x1234, BinaryProbeReader.ReadUInt16(data));
        Assert.Equal(0x56781234u, BinaryProbeReader.ReadUInt32(data));
        Assert.Equal(0x90ABCDEF56781234ul, BinaryProbeReader.ReadUInt64(data));
    }
}
