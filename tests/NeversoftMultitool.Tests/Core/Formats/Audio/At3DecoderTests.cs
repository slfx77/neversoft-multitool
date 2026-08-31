using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     PSP ATRAC3/ATRAC3plus <c>.at3</c> routing (2026-08-26) — 12,122 corpus
///     files / 1.4 GiB / ~25 hours, the largest unreachable audio population.
///     Conversion shells to ffmpeg's native atrac3/atrac3p decoders, so these
///     tests pin the container gate and the probe rather than codec output.
/// </summary>
public sealed class At3DecoderTests
{
    [Fact]
    public void IsAt3_RequiresRiffWaveContainer()
    {
        Assert.True(At3Decoder.IsAt3(RiffWave()));

        var notWave = RiffWave();
        "AVI "u8.CopyTo(notWave.AsSpan(8));
        Assert.False(At3Decoder.IsAt3(notWave));

        Assert.False(At3Decoder.IsAt3(new byte[12]));
        Assert.False(At3Decoder.IsAt3(new byte[4]));
    }

    /// <summary>
    ///     A non-RIFF payload is SKIPPED, not failed: extensions are shared
    ///     across unrelated formats and a batch run should say "not this
    ///     format" rather than report an error the user cannot act on.
    /// </summary>
    [Fact]
    public void ConvertToWav_NonRiffPayload_SkipsWithoutFailing()
    {
        var result = At3Decoder.ConvertToWav(new byte[64], "stem", Path.GetTempPath());

        Assert.True(result.Skipped);
        Assert.False(result.Success);
    }

    [Fact]
    public void ProbeAudio_At3File_ClassifiesByContainer()
    {
        var valid = FormatProbeTestHelper.CreateTempFile(".at3", RiffWave());
        var invalid = FormatProbeTestHelper.CreateTempFile(".at3", new byte[32]);
        try
        {
            Assert.Equal(FormatProbe.FormatSupport.Supported, FormatProbe.ProbeAudio(valid).Support);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, FormatProbe.ProbeAudio(invalid).Support);
        }
        finally
        {
            File.Delete(valid);
            File.Delete(invalid);
        }
    }

    /// <summary>
    ///     The Wii builds name their audio-only VID1 movies <c>.ogg</c>, so that
    ///     extension routes to the VID1 reader — but a GENUINE Ogg Vorbis file
    ///     must report unsupported rather than being fed to a reader that fails
    ///     hard. No real Ogg Vorbis exists anywhere in the 57-build corpus, so
    ///     this gate only ever protects content from outside it.
    /// </summary>
    [Fact]
    public void ProbeAudio_RealOggVorbis_IsUnsupported()
    {
        var vorbis = new byte[64];
        "OggS"u8.CopyTo(vorbis);
        var path = FormatProbeTestHelper.CreateTempFile(".ogg", vorbis);
        try
        {
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, FormatProbe.ProbeAudio(path).Support);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] RiffWave()
    {
        var data = new byte[32];
        "RIFF"u8.CopyTo(data);
        "WAVE"u8.CopyTo(data.AsSpan(8));
        return data;
    }
}
