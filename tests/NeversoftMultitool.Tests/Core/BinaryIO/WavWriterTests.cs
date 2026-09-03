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

    [Fact]
    public void WritePcm16_InfiniteForwardLoop_WritesExactSamplerChunk()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"nmt-wav-loop-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WritePcm16(
                outputPath,
                sampleRate: 1_000,
                channels: 1,
                samples: [1, 2, 3, 4],
                loop: new Pcm16WavLoop(1, 3, PlayCount: 0));

            var bytes = File.ReadAllBytes(outputPath);
            Assert.Equal(120, bytes.Length);
            Assert.Equal(112u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
            Assert.Equal("data"u8.ToArray(), bytes[36..40]);
            Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
            Assert.Equal("smpl"u8.ToArray(), bytes[52..56]);
            Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56)));
            Assert.Equal(1_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(68)));
            Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(72)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96))); // cue ID
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(100))); // forward
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104)));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(108)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(112)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(116))); // infinite
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void WritePcm16_InvalidLoop_RejectsBeforeCreatingOutput()
    {
        var (outputDirectory, outputPath) = CreateAbsentOutputPath();
        try
        {
            Assert.Throws<ArgumentException>(() => WavWriter.WritePcm16(
                outputPath,
                44_100,
                1,
                [1, 2, 3, 4],
                new Pcm16WavLoop(3, 2, PlayCount: 0)));
            Assert.False(Directory.Exists(outputDirectory));

            Assert.Throws<ArgumentException>(() => WavWriter.WritePcm16(
                outputPath,
                44_100,
                1,
                [1, 2, 3, 4],
                new Pcm16WavLoop(0, 4, PlayCount: 0)));
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, true);
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
