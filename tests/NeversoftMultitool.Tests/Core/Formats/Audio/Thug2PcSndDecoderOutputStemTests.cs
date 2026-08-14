using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Thug2PcSndDecoderOutputStemTests
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
            "NsMtThug2PcSndStem_" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(tempRoot, "output");
        var escapedPath = Path.Combine(tempRoot, "escaped.wav");

        try
        {
            var result = Thug2PcSndDecoder.ConvertToWav(
                BuildOneByteSnd(),
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
            "NsMtThug2PcSndStem_" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(tempRoot, "output");
        var rootedStem = Path.GetFullPath(Path.Combine(tempRoot, "rooted"));

        try
        {
            var result = Thug2PcSndDecoder.ConvertToWav(
                BuildOneByteSnd(),
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
            "NsMtThug2PcSndStem_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = Thug2PcSndDecoder.ConvertToWav(
                BuildOneByteSnd(),
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

    private static byte[] BuildOneByteSnd()
    {
        const int sampleRate = 44100;
        const int decodedBytes = 4;

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(decodedBytes);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(1);
            writer.Write((byte)0);
            writer.Write((byte)0);
        }

        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(decodedBytes + 36);
            writer.Write("WAVE"u8);
            writer.Write(body.ToArray());
        }

        return file.ToArray();
    }
}
