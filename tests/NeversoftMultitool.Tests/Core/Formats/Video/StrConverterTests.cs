using System.Globalization;
using System.Reflection;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class StrConverterTests
{
    private const int XaSectorSize = 2336;

    [Fact]
    public void BuildFfmpegArgs_UsesInvariantFrameRate()
    {
        var method = typeof(StrConverter).GetMethod(
            "BuildFfmpegArgs",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var arguments = Assert.IsType<string>(method.Invoke(null, [
                320,
                240,
                12.5,
                null,
                "output.mp4"
            ]));

            Assert.Contains("-r 12.50", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("12,50", arguments, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void PrepareAudio_MultiChannelUsesLowestChannelAndCleanupRemovesOwnedScratch()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "nmt-str-audio-scratch-" + Guid.NewGuid().ToString("N"));
        var firstScratch = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        var secondScratch = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        var data = BuildXaAudioSectors(7, 2);

        try
        {
            var firstWav = StrConverter.PrepareAudio(data, "first/shared.str", firstScratch);
            var secondWav = StrConverter.PrepareAudio(data, "second/shared.str", secondScratch);

            Assert.NotNull(firstWav);
            Assert.NotNull(secondWav);
            Assert.NotEqual(firstWav, secondWav);
            Assert.Equal(Path.Combine(firstScratch, "audio", "ch02.wav"), firstWav);
            Assert.Equal(Path.Combine(secondScratch, "audio", "ch02.wav"), secondWav);
            Assert.Equal(
                ["ch02.wav", "ch07.wav"],
                Directory.GetFiles(Path.Combine(firstScratch, "audio"), "*.wav")
                    .Select(Path.GetFileName)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToArray());

            StrConverter.TryDeleteDirectory(firstScratch);
            Assert.False(Directory.Exists(firstScratch));
            Assert.True(Directory.Exists(secondScratch));
            Assert.True(File.Exists(secondWav));
            Assert.True(File.Exists(Path.Combine(secondScratch, "audio", "ch07.wav")));

            StrConverter.TryDeleteDirectory(firstScratch); // idempotent
            Assert.True(Directory.Exists(secondScratch));

            StrConverter.TryDeleteDirectory(secondScratch);
            Assert.False(Directory.Exists(secondScratch));
        }
        finally
        {
            StrConverter.TryDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public void PrepareAudio_DotDotInputStemCannotEscapeOwnedScratch()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "nmt-str-audio-containment-" + Guid.NewGuid().ToString("N"));
        var scratch = Path.Combine(testRoot, "owned");

        try
        {
            var wavPath = StrConverter.PrepareAudio(
                BuildXaAudioSectors(7, 2),
                "...str",
                scratch);

            Assert.Equal(Path.Combine(scratch, "audio", "ch02.wav"), wavPath);
            Assert.Empty(Directory.GetFiles(testRoot, "*.wav", SearchOption.TopDirectoryOnly));

            StrConverter.TryDeleteDirectory(scratch);

            Assert.False(Directory.Exists(scratch));
            Assert.Empty(Directory.GetFiles(testRoot, "*.wav", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            StrConverter.TryDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public void PrepareAudio_InvalidXaRemainsVideoOnlyAndScratchIsCleanable()
    {
        var scratch = Path.Combine(
            Path.GetTempPath(),
            "nmt-str-audio-failure-" + Guid.NewGuid().ToString("N"));
        var data = BuildXaAudioSectors(1);
        data[4] = 0xFF; // invalidate the duplicated XA subheader

        try
        {
            Assert.Null(StrConverter.PrepareAudio(data, "broken.str", scratch));
            Assert.True(Directory.Exists(scratch));
        }
        finally
        {
            StrConverter.TryDeleteDirectory(scratch);
        }

        Assert.False(Directory.Exists(scratch));
    }

    private static byte[] BuildXaAudioSectors(params byte[] channels)
    {
        var data = new byte[XaSectorSize * channels.Length];
        for (var sectorIndex = 0; sectorIndex < channels.Length; sectorIndex++)
        {
            var offset = sectorIndex * XaSectorSize;
            data[offset + 1] = channels[sectorIndex];
            data[offset + 2] = 0x04;
            data.AsSpan(offset, 4).CopyTo(data.AsSpan(offset + 4, 4));
        }

        return data;
    }
}
