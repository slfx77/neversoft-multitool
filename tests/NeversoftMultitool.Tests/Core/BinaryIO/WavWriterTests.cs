using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public sealed class WavWriterTests
{
    [Fact]
    public void WritePcm16_BlockAlignmentBeyondWaveField_RejectsBeforeCreatingOutput()
    {
        var (outputDirectory, outputPath) = CreateAbsentOutputPath();
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                WavWriter.WritePcm16(outputPath, 44_100, 32_768, []));

            Assert.Equal("channels", error.ParamName);
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
    public void WritePcm16_ByteRateBeyondWaveField_RejectsBeforeCreatingOutput()
    {
        var (outputDirectory, outputPath) = CreateAbsentOutputPath();
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                WavWriter.WritePcm16(outputPath, int.MaxValue, 2, []));

            Assert.Equal("sampleRate", error.ParamName);
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
    public void WritePcm16_MaximumRepresentableBlockAlignment_WritesExactHeader()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"nmt-wav-writer-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WritePcm16(outputPath, 1, 32_767, []);

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(44, bytes.Length);
            Assert.Equal((ushort)32_767, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal(65_534u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4)));
            Assert.Equal((ushort)65_534, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32, 2)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

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

    private static (string OutputDirectory, string OutputPath) CreateAbsentOutputPath()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(WavWriterTests),
            Guid.NewGuid().ToString("N"));
        return (outputDirectory, Path.Combine(outputDirectory, "nested", "output.wav"));
    }
}
