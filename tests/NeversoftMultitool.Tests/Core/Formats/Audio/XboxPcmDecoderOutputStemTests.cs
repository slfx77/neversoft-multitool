using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class XboxPcmDecoderOutputStemTests
{
    private const string InvalidStemMessage =
        "Output stem must be a non-empty file-name stem without path components.";

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escaped")]
    [InlineData("..\\escaped")]
    public void ConvertToWav_NonLeafStem_FailsWithoutWriting(string stem)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "NsMtXboxPcmStem_" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(tempRoot, "output");
        var escapedPath = Path.Combine(tempRoot, "escaped.wav");

        try
        {
            var result = XboxPcmDecoder.ConvertToWav(
                BuildOneBlockWave(),
                stem,
                outputDirectory);

            Assert.False(result.Success);
            Assert.Equal(InvalidStemMessage, result.ErrorMessage);
            Assert.False(Directory.Exists(outputDirectory));
            Assert.False(File.Exists(escapedPath));
            Assert.False(Directory.Exists(tempRoot));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ConvertToWav_RootedStem_FailsWithoutCreatingOutputDirectory()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "NsMtXboxPcmStem_" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(tempRoot, "output");
        var rootedStem = Path.GetFullPath(Path.Combine(tempRoot, "rooted"));

        try
        {
            var result = XboxPcmDecoder.ConvertToWav(
                BuildOneBlockWave(),
                rootedStem,
                outputDirectory);

            Assert.False(result.Success);
            Assert.Equal(InvalidStemMessage, result.ErrorMessage);
            Assert.False(Directory.Exists(outputDirectory));
            Assert.False(File.Exists(rootedStem + ".wav"));
            Assert.False(Directory.Exists(tempRoot));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ConvertToWav_LeafStem_PreservesSuccessfulConversion()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "NsMtXboxPcmStem_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = XboxPcmDecoder.ConvertToWav(
                BuildOneBlockWave(),
                "plain_out",
                outputDirectory);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "plain_out.wav")));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static byte[] BuildOneBlockWave()
    {
        const int channels = 1;
        const int sampleRate = 44100;
        const int dataLength = XboxImaAdpcm.BlockAlignPerChannel;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + 20 + 8 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(20);
        writer.Write((ushort)XboxImaAdpcm.FormatTag);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * dataLength / XboxImaAdpcm.SamplesPerBlock);
        writer.Write((ushort)dataLength);
        writer.Write((ushort)4);
        writer.Write((ushort)2);
        writer.Write((ushort)XboxImaAdpcm.SamplesPerBlock);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }
}
