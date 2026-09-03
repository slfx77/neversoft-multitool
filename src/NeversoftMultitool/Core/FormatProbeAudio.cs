using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Core;

internal static class FormatProbeAudio
{
    private const int AdxHeaderSize = 18;

    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        if (LatePlatformAudio.HasSupportedFileName(filePath))
            return ProbeLatePlatformFile(filePath);

        if (ThawXmaBank.HasSupportedFileName(filePath))
            return ProbeThawXmaFile(filePath);

        if (Fsb3AudioBank.HasSupportedFileName(filePath))
            return ProbeFsb3File(filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".adx" => ProbeAdxFile(filePath),
            ".pcm" => ProbePcmFile(filePath),
            ".swav" or ".strm" or ".hwas" => ProbeNintendoDsAudio(filePath),
            ".dee" => ProbeThps4PcDeeFile(filePath),
            ".smo" => ProbeThps4PcSmoFile(filePath),
            ".snd" => ProbeSndFile(filePath),
            ".xa" => ProbeExtensionOnlyFile(filePath, "XA Audio"),
            ".vab" => ProbeExtensionOnlyFile(filePath, "VAB Sound Bank"),
            ".vag" => ProbeExtensionOnlyFile(filePath, "VAG Audio"),
            ".kat" => ProbeExtensionOnlyFile(filePath, "KAT Sound Bank"),
            ".sfx" => ProbeSfxFile(filePath),
            ".pss" => ProbePssFile(filePath),
            ".pmf" => ProbePsmfFile(filePath),
            // The Wii builds' audio-only VID1 movies ship named .ogg, so the
            // extension routes to the VID1 probe — which content-gates, so a
            // genuine Ogg Vorbis file reports unsupported rather than decoding.
            ".vid" or ".ogg" => ProbeVidFile(filePath),
            ".at3" => ProbeAt3File(filePath),
            ".wav" => ProbeStandardWaveFile(filePath),
            ".wma" => ProbeWindowsMediaAudioFile(filePath),
            _ => ProbeHeaderlessAudio(filePath)
        };
    }

    private static FormatProbe.FormatProbeResult ProbeThps4PcDeeFile(string filePath)
    {
        var probe = Thps4PcDeeAudio.Probe(filePath);
        return probe != null
            ? new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "THPS4 PC Bink-DCT Sound",
                $"{probe.Channels} ch, {probe.SampleRate} Hz, {probe.DurationSeconds:F2} s")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THPS4 PC DEE Sound",
                "Not an exact THPS4 PC BIKi DEE audio carrier");
    }

    private static FormatProbe.FormatProbeResult ProbeThps4PcSmoFile(string filePath)
    {
        var probe = Thps4PcSmoAudio.Probe(filePath);
        return probe != null
            ? new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "THPS4 PC Bink-DCT Soundtrack",
                $"{probe.Channels} ch, {probe.SampleRate} Hz, {probe.DurationSeconds:F2} s")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THPS4 PC SMO Soundtrack",
                "Not an exact THPS4 PC BIKi SMO audio carrier");
    }

    private static FormatProbe.FormatProbeResult ProbeLatePlatformFile(string filePath)
    {
        var probe = LatePlatformAudio.Probe(filePath);
        if (probe == null)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Late-generation compound audio",
                "Not an exact supported .wav.ps3/.wav.xen audio payload");
        }

        return probe.Kind switch
        {
            LatePlatformAudioKind.Ps3MpegLayer3 => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "PS3 MPEG Layer III Audio",
                $"{probe.Channels} ch, {probe.SampleRate} Hz, {probe.DurationSeconds:F2} s"),
            LatePlatformAudioKind.Ps3Fsb3 => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                $"PS3 FSB3 {probe.CodecName} Audio",
                $"{probe.Channels} ch, {probe.SampleRate} Hz, {probe.DurationSeconds:F2} s"),
            LatePlatformAudioKind.Xbox360Xma1 => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "Xbox 360 XMA1 Audio",
                $"{probe.Channels} ch, {probe.SampleRate} Hz, {probe.FrameOrPacketCount} packet(s)"),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Late-generation compound audio")
        };
    }

    private static FormatProbe.FormatProbeResult ProbeThawXmaFile(string filePath)
    {
        var bank = ThawXmaBank.Probe(filePath);
        return bank != null
            ? new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "THAW Xbox 360 XMA Sound Bank")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THAW Xbox 360 XMA Sound Bank",
                "Not an exact, supported THAW XMA DAT/WAD pair");
    }

    private static FormatProbe.FormatProbeResult ProbeFsb3File(string filePath)
    {
        var bank = Fsb3AudioBank.Probe(filePath);
        if (bank == null)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "FSB3 Sound Bank",
                "Not an exact, supported FSB3.1 MP3/XMA bank");
        }

        var mp3Count = bank.Samples.Count(static sample =>
            sample.Codec == Fsb3AudioCodec.MpegLayer3);
        var xmaCount = bank.Samples.Count - mp3Count;
        var codecName = xmaCount == 0
            ? "MP3"
            : mp3Count == 0
                ? "XMA1"
                : "MP3/XMA1";
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Supported,
            $"FSB3 {codecName} Sound Bank");
    }

    private static FormatProbe.FormatProbeResult ProbeExtensionOnlyFile(
        string filePath,
        string formatName)
    {
        return File.Exists(filePath)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, formatName)
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                formatName,
                "File not found");
    }

    private static FormatProbe.FormatProbeResult ProbeAdxFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, AdxHeaderSize, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < AdxHeaderSize)
            return FileTooSmall();

        if (header[0] == 0x80 && header[1] == 0x00)
        {
            var encoding = header[4];
            return encoding == 3
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "ADX Audio")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "ADX Audio",
                    $"Unsupported ADX encoding type {encoding} (only type 3 supported)");
        }

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "Unknown",
            "Not a valid ADX file (missing 0x8000 magic)");
    }

    private static FormatProbe.FormatProbeResult ProbeNintendoDsAudio(string filePath)
    {
        try
        {
            var probe = NdsAudioDecoder.Probe(File.ReadAllBytes(filePath));
            if (probe == null)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Nintendo DS audio",
                    "Not a Nitro SWAV/STRM wave or Neversoft HWAS stream");
            }

            var formatName = probe.Format == "HWAS"
                ? "Neversoft DS HWAS"
                : $"Nitro {probe.Format}";
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, formatName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "Nintendo DS audio", ex.Message);
        }
    }

    private static FormatProbe.FormatProbeResult ProbePcmFile(string filePath)
    {
        return XboxPcmDecoder.Probe(filePath) != null
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox ADPCM")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Xbox ADPCM",
                "Not a RIFF/WAVE container holding Xbox ADPCM (wFormatTag 0x0069)");
    }

    /// <summary>
    ///     THUG2 PC <c>.snd</c>. Its fmt chunk claims 16-bit mono PCM, while the
    ///     payload is the game's custom 4-bit codec and nAvgBytesPerSec carries the
    ///     decoded byte count rather than a byte rate.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbeSndFile(string filePath)
    {
        return Thug2PcSndDecoder.Probe(filePath) != null
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THUG2 PC Sound")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THUG2 PC Sound",
                "Not a valid THUG2 PC SND container");
    }

    private static FormatProbe.FormatProbeResult ProbeSfxFile(string filePath)
    {
        return SfxExtractor.CanExtract(filePath, out var error)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "SFX Cue Bank")
            : new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "SFX Cue Bank", error);
    }

    private static FormatProbe.FormatProbeResult ProbePssFile(string filePath)
    {
        return PssAudioExtractor.Probe(filePath) != null
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSS Audio")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PSS Audio",
                "PSS private-stream audio was not found");
    }

    private static FormatProbe.FormatProbeResult ProbePsmfFile(string filePath)
    {
        var probe = PsmfAudioExtractor.Probe(filePath);
        if (probe == null)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PSMF ATRAC3+ Audio",
                "Not a complete supported PSMF private audio stream");
        }

        return probe.HasAudio
            ? new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "PSMF ATRAC3+ Audio",
                $"{probe.Channels} ch, {probe.SampleRate} Hz, {probe.DurationSeconds:F2} s")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PSMF ATRAC3+ Audio",
                "PSMF contains no ATRAC3+ audio stream");
    }

    private static FormatProbe.FormatProbeResult ProbeAt3File(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 12, out var header, out var bytesRead) || bytesRead < 12)
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "ATRAC3 Audio", "File too small");

        return At3Decoder.IsAt3(header)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "ATRAC3 Audio")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "ATRAC3 Audio",
                "Not a RIFF/WAVE ATRAC3 container");
    }

    private static FormatProbe.FormatProbeResult ProbeVidFile(string filePath)
    {
        return Vid1AudioExtractor.TryProbe(filePath, out _, out var error)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VID1 Audio")
            : new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "VID1 Audio", error);
    }

    private static FormatProbe.FormatProbeResult ProbeStandardWaveFile(string filePath)
    {
        try
        {
            return StandardAudioFormatSupport.ProbeWave(File.ReadAllBytes(filePath)) != null
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "WAV Audio")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "WAV Audio",
                    "Not a complete, playable RIFF/WAVE file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "WAV Audio", ex.Message);
        }
    }

    private static FormatProbe.FormatProbeResult ProbeWindowsMediaAudioFile(string filePath)
    {
        try
        {
            return StandardAudioFormatSupport.ProbeWindowsMediaAudio(File.ReadAllBytes(filePath)) != null
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Windows Media Audio")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Windows Media Audio",
                    "Not an ASF container with a valid audio stream");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "Windows Media Audio", ex.Message);
        }
    }

    /// <summary>
    ///     The fallback for files with no recognized audio extension. Both real
    ///     callers only ever feed it EXTENSIONLESS voice/music streams, so a
    ///     size-only rule used to hand back "Supported / Headerless SPU-ADPCM" for
    ///     anything whose length happened to be a multiple of 16 — including 516
    ///     .pcm and 56 .snd files, which are RIFF containers and not SPU-ADPCM at
    ///     all. Now a container or a named extension is rejected outright, and the
    ///     size rule must additionally survive the real VAG probe.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbeHeaderlessAudio(string filePath)
    {
        try
        {
            var extension = Path.GetExtension(filePath);

            if (RiffWaveReader.TryReadHeader(filePath, out _))
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    "RIFF/WAVE container with an unrecognized extension");
            }

            if (!string.IsNullOrEmpty(extension))
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    $"Unrecognized audio format: {extension}");
            }

            var data = File.ReadAllBytes(filePath);
            if (WiiDspAudio.Probe(data) is { } dsp)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Supported,
                    "Nintendo DSP-ADPCM",
                    $"Mono, {dsp.SampleRate} Hz, {dsp.DurationSeconds:F2} s");
            }

            var info = new FileInfo(filePath);
            return info.Length > 0 && info.Length % 16 == 0 && VagDecoder.Probe(data) != null
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Headerless SPU-ADPCM")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    $"Unrecognized audio format: {extension}");
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Failed to read file");
        }
    }

    private static FormatProbe.FormatProbeResult FileTooSmall()
    {
        return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "Unknown", "File too small");
    }

    private static FormatProbe.FormatProbeResult HeaderReadFailure()
    {
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "Unknown",
            "Failed to read file header");
    }
}
