using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public sealed class WavWriterTests
{
    [Fact]
    public void WritePcm16_IncompleteStereoFrame_RejectsBeforeCreatingOutput()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"nmt-wav-writer-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(outputDirectory, "output.wav");

        try
        {
            var exception = Assert.Throws<ArgumentException>(
                () => WavWriter.WritePcm16(outputPath, 44_100, 2, [123]));

            Assert.Equal("samples", exception.ParamName);
            Assert.False(Directory.Exists(outputDirectory));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, true);
        }
    }

    [Fact]
    public void WritePcm16_CompleteStereoFrame_PreservesWaveLayout()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"nmt-wav-writer-{Guid.NewGuid():N}.wav");

        try
        {
            WavWriter.WritePcm16(outputPath, 44_100, 2, [123, -123]);

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(48, bytes.Length);
            Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
            Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal(44_100, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32, 2)));
            Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4)));
            Assert.Equal((short)123, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44, 2)));
            Assert.Equal((short)-123, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46, 2)));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
