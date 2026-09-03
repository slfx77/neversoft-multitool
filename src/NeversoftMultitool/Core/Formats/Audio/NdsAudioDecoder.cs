using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Nds;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Sample rate, channels and duration of a DS wave — Nitro or <c>.hwas</c>.</summary>
public sealed record NdsAudioProbeResult(int SampleRate, int Channels, double DurationSeconds, string Format);

/// <summary>
///     Converts the DS carts' wave formats to WAV: the two Nintendo Nitro SDK ones,
///     <see cref="SwavFile" /> (1,405 sound effects inside the GOB container) and
///     <see cref="StrmFile" /> (American Sk8land's 30-track, 62-minute soundtrack
///     inside <c>sound_stream.sdat</c>) — plus the studio's own
///     <see cref="HwasStream" />, which carries the Downhill Jam and Proving Ground
///     soundtracks, 86 minutes across 35 files. The Nitro pair are wave type 2,
///     Nintendo IMA-ADPCM (<see cref="NitroAdpcm" />), across the whole corpus; PCM8
///     and PCM16 are implemented because the formats allow them, not because a
///     shipped file uses them.
/// </summary>
public static class NdsAudioDecoder
{
    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        try
        {
            return ConvertToWav(
                File.ReadAllBytes(inputPath),
                Path.GetFileNameWithoutExtension(inputPath),
                outputDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return new AudioConvertResult { ErrorMessage = "Output stem must be a plain file-name stem." };

            short[] samples;
            int sampleRate;
            int channels;

            if (StrmFile.IsStrm(data))
            {
                var strm = StrmFile.Parse(data);
                samples = strm.Decode();
                sampleRate = strm.SampleRate;
                channels = strm.Channels;
            }
            else if (SwavFile.IsSwav(data))
            {
                var swav = SwavFile.Parse(data);
                samples = swav.Decode();
                sampleRate = swav.SampleRate;
                channels = 1;
            }
            else if (HwasStream.IsHwas(data))
            {
                var hwas = HwasStream.Parse(data);
                samples = hwas.Decode();
                sampleRate = hwas.SampleRate;
                channels = hwas.Channels;
            }
            else
            {
                return new AudioConvertResult
                {
                    Skipped = true, ErrorMessage = "Not a Nitro SWAV or STRM wave, or a .hwas stream"
                };
            }

            if (samples.Length == 0)
                return new AudioConvertResult { Skipped = true, ErrorMessage = "Wave carries no samples" };

            Directory.CreateDirectory(outputDir);
            WavWriter.WritePcm16(Path.Combine(outputDir, stem + ".wav"), sampleRate, channels, samples);
            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException
                                   or OverflowException or UnauthorizedAccessException)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>Header-only probe for the tab's duration column, with no decode.</summary>
    public static NdsAudioProbeResult? Probe(byte[] data)
    {
        try
        {
            if (StrmFile.IsStrm(data))
            {
                var strm = StrmFile.Parse(data);
                return new NdsAudioProbeResult(strm.SampleRate, strm.Channels, strm.DurationSeconds, "STRM");
            }

            if (HwasStream.IsHwas(data))
            {
                var hwas = HwasStream.Parse(data);
                return new NdsAudioProbeResult(
                    hwas.SampleRate, hwas.Channels, hwas.DurationSeconds, "HWAS");
            }

            if (SwavFile.IsSwav(data))
            {
                var swav = SwavFile.Parse(data);
                var samples = swav.WaveType switch
                {
                    NitroWaveType.Pcm8 => swav.Payload.Length,
                    NitroWaveType.Pcm16 => swav.Payload.Length / 2,
                    _ => NitroAdpcm.SampleCount(swav.Payload.Length)
                };
                return new NdsAudioProbeResult(
                    swav.SampleRate, 1, swav.SampleRate > 0 ? (double)samples / swav.SampleRate : 0, "SWAV");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
        }

        return null;
    }
}
