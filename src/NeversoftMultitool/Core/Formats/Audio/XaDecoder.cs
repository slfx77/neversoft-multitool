using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Decodes PS1 CD-ROM XA ADPCM audio files to PCM WAV.
///     Handles both sectored (2336-byte sectors with subheaders) and raw (continuous sound groups) formats.
///     Sectored files with multiple interleaved channels produce one WAV per channel in a subdirectory.
/// </summary>
public static class XaDecoder
{
    private const int SectorSize = 2336;
    private const int SubheaderSize = 8;
    private const int SoundGroupSize = 128;
    private const int SoundGroupParamSize = 16;
    private const int SoundGroupsPerSector = 18;
    private const int SamplesPerUnit = 28;
    private const int UnitsPerGroup = 8;

    // XA ADPCM filter coefficients (K0, K1) as doubles, matching jPSXdec
    private static readonly double[] K0 = [0.0, 60.0 / 64.0, 115.0 / 64.0, 98.0 / 64.0];
    private static readonly double[] K1 = [0.0, 0.0, -52.0 / 64.0, -55.0 / 64.0];

    /// <summary>
    ///     Decodes sectored XA audio data (as extracted by
    ///     <see cref="NeversoftMultitool.Core.Formats.Video.StrDemuxer.ExtractAudioSectors(byte[])" />)
    ///     to raw PCM16 samples in memory. Returns null if the data is not valid sectored XA audio.
    /// </summary>
    public static (short[] Samples, int SampleRate, int Channels)? DecodeToSamples(byte[] sectoredData)
    {
        if (!IsSectored(sectoredData))
            return null;

        var sectorCount = sectoredData.Length / SectorSize;

        // Use the first sector's coding byte to determine format
        var coding = sectoredData[3];
        var isStereo = (coding & 0x01) != 0;
        var sampleRate = (coding & 0x04) != 0 ? 18900 : 37800;

        var pcmSamples = new List<short>();
        var hist = new double[isStereo ? 2 : 1, 2];

        for (var s = 0; s < sectorCount; s++)
        {
            var sectorOffset = s * SectorSize;
            var audioStart = sectorOffset + SubheaderSize;

            for (var g = 0; g < SoundGroupsPerSector; g++)
            {
                var groupOffset = audioStart + g * SoundGroupSize;
                if (groupOffset + SoundGroupSize > sectoredData.Length) break;

                DecodeSoundGroup(sectoredData, groupOffset, hist, isStereo, pcmSamples);
            }
        }

        return (pcmSamples.ToArray(), sampleRate, isStereo ? 2 : 1);
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        try
        {
            return ConvertToWav(File.ReadAllBytes(inputPath), Path.GetFileNameWithoutExtension(inputPath), outputDir);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>In-memory variant of <see cref="ConvertToWav(string, string)" />.</summary>
    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir)
    {
        try
        {
            if (IsSectored(data))
                return DecodeSectored(data, stem, outputDir);

            if (data.Length % SoundGroupSize == 0)
                return DecodeRaw(data, stem, outputDir);

            return new AudioConvertResult { ErrorMessage = "Unrecognized XA format" };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    private static bool IsSectored(byte[] data)
    {
        if (data.Length < SubheaderSize || data.Length % SectorSize != 0)
            return false;

        // Subheader: 4 bytes repeated
        if (data[0] != data[4] || data[1] != data[5] || data[2] != data[6] || data[3] != data[7])
            return false;

        // Submode audio bit (bit 2) must be set
        return (data[2] & 0x04) != 0;
    }

    private static AudioConvertResult DecodeSectored(byte[] data, string stem, string outputDir)
    {
        var sectorCount = data.Length / SectorSize;

        // Group sectors by channel and read per-channel coding info
        var channelSectors = new Dictionary<int, List<int>>();
        var channelCoding = new Dictionary<int, byte>();

        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var channel = data[offset + 1];
            var coding = data[offset + 3];

            if (!channelSectors.ContainsKey(channel))
            {
                channelSectors[channel] = [];
                channelCoding[channel] = coding;
            }

            channelSectors[channel].Add(offset);
        }

        var channels = channelSectors.Keys.OrderBy(k => k).ToList();
        var multiChannel = channels.Count > 1;

        // For multi-channel, create subdirectory
        var outPath = multiChannel
            ? Path.Combine(outputDir, stem)
            : outputDir;

        var filesWritten = 0;

        foreach (var ch in channels)
        {
            var coding = channelCoding[ch];
            var isStereo = (coding & 0x01) != 0;
            var sampleRate = (coding & 0x04) != 0 ? 18900 : 37800;
            var outputChannels = isStereo ? 2 : 1;

            var pcmSamples = new List<short>();
            var hist = new double[isStereo ? 2 : 1, 2]; // [channel, 0=prev1/1=prev2]

            foreach (var sectorOffset in channelSectors[ch])
            {
                var audioStart = sectorOffset + SubheaderSize;

                for (var g = 0; g < SoundGroupsPerSector; g++)
                {
                    var groupOffset = audioStart + g * SoundGroupSize;
                    if (groupOffset + SoundGroupSize > data.Length) break;

                    DecodeSoundGroup(data, groupOffset, hist, isStereo, pcmSamples);
                }
            }

            var fileName = multiChannel ? $"ch{ch:D2}.wav" : $"{stem}.wav";
            var wavPath = Path.Combine(outPath, fileName);
            WavWriter.WritePcm16(wavPath, sampleRate, outputChannels, pcmSamples.ToArray());
            filesWritten++;
        }

        return new AudioConvertResult { Success = true, SamplesWritten = filesWritten };
    }

    private static AudioConvertResult DecodeRaw(byte[] data, string stem, string outputDir)
    {
        // Raw format: continuous sound groups, assume stereo 37800 Hz
        const int sampleRate = 37800;
        const bool isStereo = true;
        var outputChannels = isStereo ? 2 : 1;

        var groupCount = data.Length / SoundGroupSize;
        var pcmSamples = new List<short>();
        var hist = new double[2, 2]; // stereo: [channel, 0=prev1/1=prev2]

        for (var g = 0; g < groupCount; g++)
        {
            var groupOffset = g * SoundGroupSize;
            DecodeSoundGroup(data, groupOffset, hist, isStereo, pcmSamples);
        }

        var wavPath = Path.Combine(outputDir, stem + ".wav");
        WavWriter.WritePcm16(wavPath, sampleRate, outputChannels, pcmSamples.ToArray());

        return new AudioConvertResult { Success = true, SamplesWritten = 1 };
    }

    /// <summary>
    ///     Decodes a single 128-byte sound group into interleaved PCM samples.
    ///     Each sound group contains 8 sound units with 28 samples each.
    ///     For stereo: units 0,2,4,6 = left; units 1,3,5,7 = right.
    /// </summary>
    private static void DecodeSoundGroup(byte[] data, int offset, double[,] hist,
        bool isStereo, List<short> output)
    {
        // Read parameter bytes for each sound unit
        // Params layout: bytes 0-3 for units 0-3 (repeated at 4-7), bytes 8-11 for units 4-7 (repeated at 12-15)
        Span<int> ranges = stackalloc int[UnitsPerGroup];
        Span<int> filters = stackalloc int[UnitsPerGroup];

        for (var u = 0; u < 4; u++)
        {
            var paramByte = data[offset + u];
            ranges[u] = paramByte & 0x0F;
            filters[u] = (paramByte >> 4) & 0x03;
        }

        for (var u = 4; u < 8; u++)
        {
            var paramByte = data[offset + 8 + (u - 4)];
            ranges[u] = paramByte & 0x0F;
            filters[u] = (paramByte >> 4) & 0x03;
        }

        // Extract nibbles first, then decode each unit as a complete block.
        // Data layout: for sample s, unit n: byte at offset + 16 + s*4 + (n>>1)
        //   n even -> low nibble (bits 0-3), n odd -> high nibble (bits 4-7)
        var nibbles = new int[UnitsPerGroup, SamplesPerUnit];

        for (var s = 0; s < SamplesPerUnit; s++)
        {
            for (var n = 0; n < UnitsPerGroup; n++)
            {
                var byteOffset = offset + SoundGroupParamSize + s * 4 + (n >> 1);
                int nibble;

                if ((n & 1) == 0)
                    nibble = data[byteOffset] & 0x0F;
                else
                    nibble = (data[byteOffset] >> 4) & 0x0F;

                if (nibble >= 8) nibble -= 16;
                nibbles[n, s] = nibble;
            }
        }

        // Decode each unit's 28 samples as a complete block so ADPCM history
        // carries forward within a channel. Per-channel history is shared:
        // stereo: units 0,2,4,6 share left (ch=0), units 1,3,5,7 share right (ch=1)
        // mono: all 8 units share a single history (ch=0)
        var unitSamples = new short[UnitsPerGroup, SamplesPerUnit];

        // Process units in channel-sequential order
        var unitOrder = isStereo
            ? new[] { 0, 2, 4, 6, 1, 3, 5, 7 } // left units first, then right
            : new[] { 0, 1, 2, 3, 4, 5, 6, 7 }; // all sequential

        foreach (var n in unitOrder)
        {
            var ch = isStereo ? n & 1 : 0;
            var range = ranges[n];
            var filter = filters[n];

            for (var s = 0; s < SamplesPerUnit; s++)
            {
                // Arithmetic right shift handles range > 12 naturally
                var unranged = (nibbles[n, s] << 12) >> range;
                var decoded = unranged + K0[filter] * hist[ch, 0] + K1[filter] * hist[ch, 1];
                hist[ch, 1] = hist[ch, 0];
                hist[ch, 0] = decoded; // store raw double (unclamped) as history
                unitSamples[n, s] = (short)Math.Clamp((long)Math.Round(decoded), short.MinValue, short.MaxValue);
            }
        }

        // Interleave output: each unit pair contains 28 sequential stereo frames
        if (isStereo)
        {
            for (var pair = 0; pair < 4; pair++)
            {
                for (var s = 0; s < SamplesPerUnit; s++)
                {
                    output.Add(unitSamples[pair * 2, s]); // left
                    output.Add(unitSamples[pair * 2 + 1, s]); // right
                }
            }
        }
        else
        {
            for (var n = 0; n < UnitsPerGroup; n++)
            {
                for (var s = 0; s < SamplesPerUnit; s++)
                {
                    output.Add(unitSamples[n, s]);
                }
            }
        }
    }
}
