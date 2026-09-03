using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Audio;

public enum Fsb3AudioCodec
{
    MpegLayer3,
    Xma1
}

public sealed record Fsb3SampleInfo(
    int Index,
    string Name,
    Fsb3AudioCodec Codec,
    int DecodedSampleCount,
    int CompressedSize,
    int SampleRate,
    int Channels,
    int LoopStart,
    int LoopEnd,
    uint Mode,
    int HeaderSize,
    long DataOffset)
{
    public double DurationSeconds => DecodedSampleCount / (double)SampleRate;
}

public sealed record Fsb3BankInfo(
    uint Version,
    uint Flags,
    int SampleHeaderSize,
    int HeaderPaddingBytes,
    long DataOffset,
    long DataSize,
    IReadOnlyList<Fsb3SampleInfo> Samples);

/// <summary>
///     Strict reader and extractor for the FSB3.1 banks in Project 8 and
///     Proving Ground. The PS3 banks contain standard MP3 streams, while the
///     Xbox 360 banks contain raw XMA1 packets plus an FMOD seek table.
/// </summary>
/// <remarks>
///     This deliberately accepts only the two fully measured corpus dialects.
///     It rejects basic headers, encryption, mixed/unknown codec flags, loose
///     length declarations, and malformed XMA seek tables instead of guessing.
///     The measured population is 12 banks, 22,454 named streams, and every
///     accepted bank consumes its header and payload exactly.
/// </remarks>
public static class Fsb3AudioBank
{
    public const int MainHeaderSize = 24;
    public const int SampleHeaderSize = 80;
    public const int XmaPacketSize = XmaRiffAudio.PacketSize;
    public const int XmaBlockSize = XmaRiffAudio.BlockSize;
    public const int XmaWaveHeaderSize = XmaRiffAudio.WaveHeaderSize;

    private const uint Fsb31Version = 0x00030001;
    private const uint FsoundLoopMask = 0x00000007;
    private const uint FsoundMono = 0x00000020;
    private const uint FsoundStereo = 0x00000040;
    private const uint FsoundMpeg = 0x00000200;
    private const uint Fsound3d = 0x00100000;
    private const uint FsoundXma = 0x01000000;
    private const uint MpegMonoMode = FsoundMpeg | FsoundMono;
    private const uint MpegStereoMode = FsoundMpeg | FsoundStereo;
    private const uint XmaMonoMode = FsoundXma | Fsound3d | FsoundMono;
    private const uint XmaStereoMode = FsoundXma | Fsound3d | FsoundStereo;
    private const int MaximumHeaderSectionSize = 64 * 1024 * 1024;
    private const int MaximumHeaderPadding = 15;

    internal delegate bool AudioTranscoder(string inputPath, string outputPath, out string error);

    public static Fsb3BankInfo? Probe(string inputPath)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            return TryRead(stream, out var bank) ? bank : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Fsb3BankInfo? Probe(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        return TryRead(stream, out var bank) ? bank : null;
    }

    public static bool IsFsb3(string inputPath) => Probe(inputPath) != null;

    public static bool IsFsb3(byte[] data) => Probe(data) != null;

    /// <summary>
    ///     Matches the plain and platform-qualified names used by FMOD and the
    ///     measured PS3/Xbox 360 builds. This is only a candidate-name gate;
    ///     callers must still use <see cref="Probe(string)" /> before claiming
    ///     support.
    /// </summary>
    public static bool HasSupportedFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var name = Path.GetFileName(path);
        return name.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".fsb.ps3", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".fsb.xen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns one stream in a directly playable container. MP3 is copied
    ///     exactly; raw XMA1 packets receive the canonical 80-byte RIFF/XMA
    ///     header used by Microsoft's decoder and ffmpeg.
    /// </summary>
    public static byte[] CreatePlayableStream(byte[] bankData, int sampleIndex)
    {
        ArgumentNullException.ThrowIfNull(bankData);
        using var input = new MemoryStream(bankData, writable: false);
        if (!TryRead(input, out var bank) || sampleIndex < 0 || sampleIndex >= bank.Samples.Count)
            return [];

        var sample = bank.Samples[sampleIndex];
        var headerSize = sample.Codec == Fsb3AudioCodec.Xma1 ? XmaWaveHeaderSize : 0;
        var output = new byte[checked(sample.CompressedSize + headerSize)];
        using var destination = new MemoryStream(output, writable: true);
        WritePlayableStream(input, sample, destination);
        return output;
    }

    /// <summary>
    ///     Extracts every named stream without transcoding. MP3 samples retain
    ///     their authored bytes; XMA samples are emitted as RIFF/XMA files.
    /// </summary>
    public static AudioConvertResult ExtractEncoded(string inputPath, string outputDirectory)
    {
        return ExtractEncoded(inputPath, GetBankStem(inputPath), outputDirectory);
    }

    public static AudioConvertResult ExtractEncoded(
        string inputPath,
        string outputStem,
        string outputDirectory)
    {
        try
        {
            using var input = File.OpenRead(inputPath);
            return ExtractEncoded(input, outputStem, outputDirectory);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ExtractEncoded(
        byte[] bankData,
        string outputStem,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(bankData);
        try
        {
            using var input = new MemoryStream(bankData, writable: false);
            return ExtractEncoded(input, outputStem, outputDirectory);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string outputDirectory)
    {
        return ConvertToWav(inputPath, GetBankStem(inputPath), outputDirectory);
    }

    public static AudioConvertResult ConvertToWav(
        string inputPath,
        string outputStem,
        string outputDirectory)
    {
        try
        {
            using var input = File.OpenRead(inputPath);
            return ConvertToWav(input, outputStem, outputDirectory, RunFfmpeg);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(
        byte[] bankData,
        string outputStem,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(bankData);
        try
        {
            using var input = new MemoryStream(bankData, writable: false);
            return ConvertToWav(input, outputStem, outputDirectory, RunFfmpeg);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    internal static AudioConvertResult ConvertToWav(
        byte[] bankData,
        string outputStem,
        string outputDirectory,
        AudioTranscoder transcoder)
    {
        ArgumentNullException.ThrowIfNull(bankData);
        using var input = new MemoryStream(bankData, writable: false);
        return ConvertToWav(input, outputStem, outputDirectory, transcoder);
    }

    /// <summary>
    ///     Decodes one bank stream for GUI preview without loading a loose bank
    ///     into memory. The returned path is committed atomically and is null
    ///     when the bank, sample index, or decoder output is invalid.
    /// </summary>
    public static string? ConvertSingleToWav(
        string inputPath,
        int sampleIndex,
        string outputDirectory)
    {
        try
        {
            using var input = File.OpenRead(inputPath);
            return ConvertSingleToWav(
                input,
                GetBankStem(inputPath),
                sampleIndex,
                outputDirectory,
                RunFfmpeg);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>In-memory single-stream conversion for archive-backed banks.</summary>
    public static string? ConvertSingleToWav(
        byte[] bankData,
        string outputStem,
        int sampleIndex,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(bankData);
        try
        {
            using var input = new MemoryStream(bankData, writable: false);
            return ConvertSingleToWav(
                input,
                outputStem,
                sampleIndex,
                outputDirectory,
                RunFfmpeg);
        }
        catch
        {
            return null;
        }
    }

    private static AudioConvertResult ExtractEncoded(
        Stream input,
        string outputStem,
        string outputDirectory)
    {
        if (!TryRead(input, out var bank))
            return NotThisFormat("Not an exact, supported FSB3.1 MP3/XMA bank");

        var bankDirectory = PrepareBankDirectory(outputStem, outputDirectory);
        Directory.CreateDirectory(bankDirectory);
        var filesWritten = 0;
        foreach (var sample in bank.Samples)
        {
            var extension = sample.Codec == Fsb3AudioCodec.MpegLayer3 ? ".mp3" : ".xma";
            var outputPath = Path.Combine(bankDirectory, GetSampleStem(sample) + extension);
            var stagedPath = Path.Combine(bankDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    WritePlayableStream(input, sample, output);

                File.Move(stagedPath, outputPath, overwrite: true);
                filesWritten++;
            }
            finally
            {
                TryDelete(stagedPath);
            }
        }

        return new AudioConvertResult { Success = true, SamplesWritten = filesWritten };
    }

    private static AudioConvertResult ConvertToWav(
        Stream input,
        string outputStem,
        string outputDirectory,
        AudioTranscoder transcoder)
    {
        if (!TryRead(input, out var bank))
            return NotThisFormat("Not an exact, supported FSB3.1 MP3/XMA bank");

        var bankDirectory = PrepareBankDirectory(outputStem, outputDirectory);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Fsb3");
        Directory.CreateDirectory(bankDirectory);
        Directory.CreateDirectory(tempDirectory);

        var filesWritten = 0;
        foreach (var sample in bank.Samples)
        {
            var encodedExtension = sample.Codec == Fsb3AudioCodec.MpegLayer3 ? ".mp3" : ".xma";
            var encodedPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{encodedExtension}");
            var outputPath = Path.Combine(bankDirectory, GetSampleStem(sample) + ".wav");
            var stagedOutputPath = Path.Combine(bankDirectory, $".{Guid.NewGuid():N}.wav");
            try
            {
                using (var encoded = new FileStream(
                           encodedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    WritePlayableStream(input, sample, encoded);

                if (!transcoder(encodedPath, stagedOutputPath, out var error))
                    return new AudioConvertResult { ErrorMessage = error };

                if (!IsWaveFile(stagedOutputPath, sample))
                {
                    return new AudioConvertResult
                    {
                        ErrorMessage = $"Decoder produced no playable WAV for FSB sample {sample.Index}"
                    };
                }

                File.Move(stagedOutputPath, outputPath, overwrite: true);
                filesWritten++;
            }
            finally
            {
                TryDelete(encodedPath);
                TryDelete(stagedOutputPath);
            }
        }

        return new AudioConvertResult { Success = true, SamplesWritten = filesWritten };
    }

    private static string? ConvertSingleToWav(
        Stream input,
        string outputStem,
        int sampleIndex,
        string outputDirectory,
        AudioTranscoder transcoder)
    {
        if (!TryRead(input, out var bank)
            || sampleIndex < 0
            || sampleIndex >= bank.Samples.Count)
        {
            return null;
        }

        var sample = bank.Samples[sampleIndex];
        var bankDirectory = PrepareBankDirectory(outputStem, outputDirectory);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Fsb3");
        Directory.CreateDirectory(bankDirectory);
        Directory.CreateDirectory(tempDirectory);

        var encodedExtension = sample.Codec == Fsb3AudioCodec.MpegLayer3 ? ".mp3" : ".xma";
        var encodedPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{encodedExtension}");
        var outputPath = Path.Combine(bankDirectory, GetSampleStem(sample) + ".wav");
        var stagedOutputPath = Path.Combine(bankDirectory, $".{Guid.NewGuid():N}.wav");
        try
        {
            using (var encoded = new FileStream(
                       encodedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WritePlayableStream(input, sample, encoded);
            }

            if (!transcoder(encodedPath, stagedOutputPath, out _)
                || !IsWaveFile(stagedOutputPath, sample))
            {
                return null;
            }

            File.Move(stagedOutputPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            TryDelete(encodedPath);
            TryDelete(stagedOutputPath);
        }
    }

    private static bool TryRead(Stream stream, out Fsb3BankInfo bank)
    {
        bank = null!;
        if (!stream.CanRead || !stream.CanSeek || stream.Length < MainHeaderSize)
            return false;

        stream.Position = 0;
        Span<byte> mainHeader = stackalloc byte[MainHeaderSize];
        if (!TryReadExactly(stream, mainHeader)
            || !mainHeader[..4].SequenceEqual("FSB3"u8))
        {
            return false;
        }

        var sampleCount32 = BinaryPrimitives.ReadUInt32LittleEndian(mainHeader[4..8]);
        var sampleHeadersSize32 = BinaryPrimitives.ReadUInt32LittleEndian(mainHeader[8..12]);
        var dataSize32 = BinaryPrimitives.ReadUInt32LittleEndian(mainHeader[12..16]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(mainHeader[16..20]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(mainHeader[20..24]);
        if (version != Fsb31Version
            || flags != 0
            || sampleCount32 is 0 or > int.MaxValue
            || sampleHeadersSize32 is < SampleHeaderSize or > MaximumHeaderSectionSize
            || sampleHeadersSize32 > int.MaxValue
            || dataSize32 is 0 or > int.MaxValue
            || MainHeaderSize + (long)sampleHeadersSize32 + dataSize32 != stream.Length)
        {
            return false;
        }

        var sampleCount = (int)sampleCount32;
        var sampleHeadersSize = (int)sampleHeadersSize32;
        if (sampleCount > sampleHeadersSize / SampleHeaderSize)
            return false;

        var sampleHeaders = new byte[sampleHeadersSize];
        if (!TryReadExactly(stream, sampleHeaders))
            return false;

        var samples = new List<Fsb3SampleInfo>(sampleCount);
        var headerOffset = 0;
        long relativeDataOffset = 0;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            if (headerOffset > sampleHeaders.Length - SampleHeaderSize)
                return false;

            var remainingHeaders = sampleHeaders.AsSpan(headerOffset);
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(remainingHeaders[..2]);
            if (headerSize < SampleHeaderSize
                || (headerSize & 3) != 0
                || headerSize > remainingHeaders.Length)
            {
                return false;
            }

            var header = remainingHeaders[..headerSize];
            if (!TryReadName(header[2..32], out var name))
                return false;

            var decodedSamples32 = BinaryPrimitives.ReadUInt32LittleEndian(header[32..36]);
            var compressedSize32 = BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]);
            var loopStart32 = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
            var loopEnd32 = BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
            var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[52..56]);
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[62..64]);
            var minimumDistance = BinaryPrimitives.ReadSingleLittleEndian(header[64..68]);
            var maximumDistance = BinaryPrimitives.ReadSingleLittleEndian(header[68..72]);
            if (decodedSamples32 is 0 or > int.MaxValue
                || compressedSize32 is 0 or > int.MaxValue
                || loopStart32 > decodedSamples32
                || loopEnd32 > decodedSamples32
                || loopStart32 > loopEnd32
                || sampleRate is < 4000 or > 192000
                || channels is < 1 or > 2
                || !float.IsFinite(minimumDistance)
                || !float.IsFinite(maximumDistance)
                || minimumDistance < 0
                || maximumDistance < minimumDistance)
            {
                return false;
            }

            var modeWithoutLoops = mode & ~FsoundLoopMask;
            Fsb3AudioCodec codec;
            if (modeWithoutLoops is MpegMonoMode or MpegStereoMode)
            {
                codec = Fsb3AudioCodec.MpegLayer3;
                if (headerSize != SampleHeaderSize)
                    return false;
            }
            else if (modeWithoutLoops is XmaMonoMode or XmaStereoMode)
            {
                codec = Fsb3AudioCodec.Xma1;
                if (!ValidateXmaHeader(header, compressedSize32))
                    return false;
            }
            else
            {
                return false;
            }

            var stereoMode = (modeWithoutLoops & FsoundStereo) != 0;
            if (channels != (stereoMode ? 2 : 1)
                || relativeDataOffset > dataSize32 - (long)compressedSize32)
            {
                return false;
            }

            samples.Add(new Fsb3SampleInfo(
                sampleIndex,
                name,
                codec,
                (int)decodedSamples32,
                (int)compressedSize32,
                sampleRate,
                channels,
                (int)loopStart32,
                (int)loopEnd32,
                mode,
                headerSize,
                MainHeaderSize + (long)sampleHeadersSize + relativeDataOffset));
            relativeDataOffset += compressedSize32;
            headerOffset += headerSize;
        }

        var paddingBytes = sampleHeaders.Length - headerOffset;
        if (paddingBytes > MaximumHeaderPadding
            || sampleHeaders.AsSpan(headerOffset).IndexOfAnyExcept((byte)0) >= 0
            || relativeDataOffset != dataSize32)
        {
            return false;
        }

        Span<byte> signature = stackalloc byte[4];
        foreach (var sample in samples)
        {
            stream.Position = sample.DataOffset;
            if (!TryReadExactly(stream, signature))
                return false;

            if (sample.Codec == Fsb3AudioCodec.MpegLayer3)
            {
                if (!IsMpegLayer3Header(signature))
                    return false;
            }
            else if (signature[0] != 0x08
                     || signature[1] != 0
                     || signature[2] != 0
                     || signature[3] != 0)
            {
                return false;
            }
        }

        bank = new Fsb3BankInfo(
            version,
            flags,
            sampleHeadersSize,
            paddingBytes,
            MainHeaderSize + (long)sampleHeadersSize,
            dataSize32,
            samples);
        return true;
    }

    private static bool ValidateXmaHeader(ReadOnlySpan<byte> header, uint compressedSize)
    {
        if (header.Length < 108 || compressedSize % XmaPacketSize != 0)
            return false;

        var packetCount = checked((int)(compressedSize / XmaPacketSize));
        var seekDataSize = BinaryPrimitives.ReadUInt32LittleEndian(header[92..96]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[80..84]) != 0x20
            || seekDataSize != header.Length - 96
            || seekDataSize != checked((uint)((packetCount + 2) * sizeof(uint)))
            || BinaryPrimitives.ReadUInt32LittleEndian(header[96..100]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header[100..104]) != (uint)packetCount)
        {
            return false;
        }

        var previous = 0U;
        for (var packetIndex = 0; packetIndex < packetCount; packetIndex++)
        {
            var seekValue = BinaryPrimitives.ReadUInt32LittleEndian(
                header.Slice(104 + packetIndex * sizeof(uint), sizeof(uint)));
            if (packetIndex == 0 && seekValue != 0 || packetIndex > 0 && seekValue < previous)
                return false;

            previous = seekValue;
        }

        return true;
    }

    private static bool TryReadName(ReadOnlySpan<byte> bytes, out string name)
    {
        var nullIndex = bytes.IndexOf((byte)0);
        var nameBytes = nullIndex >= 0 ? bytes[..nullIndex] : bytes;
        if (nameBytes.IsEmpty || nameBytes.IndexOfAnyInRange((byte)0, (byte)31) >= 0)
        {
            name = "";
            return false;
        }

        name = Encoding.Latin1.GetString(nameBytes);
        return true;
    }

    private static bool IsMpegLayer3Header(ReadOnlySpan<byte> header)
    {
        var bits = BinaryPrimitives.ReadUInt32BigEndian(header);
        var version = (bits >> 19) & 0x3;
        var layer = (bits >> 17) & 0x3;
        var bitrateIndex = (bits >> 12) & 0xF;
        var sampleRateIndex = (bits >> 10) & 0x3;
        return (bits & 0xFFE00000) == 0xFFE00000
               && version != 1
               && layer == 1
               && bitrateIndex is > 0 and < 15
               && sampleRateIndex != 3;
    }

    private static void WritePlayableStream(
        Stream input,
        Fsb3SampleInfo sample,
        Stream output)
    {
        if (sample.Codec == Fsb3AudioCodec.Xma1)
        {
            Span<byte> xmaHeader = stackalloc byte[XmaWaveHeaderSize];
            WriteXmaWaveHeader(xmaHeader, sample);
            output.Write(xmaHeader);
        }

        input.Position = sample.DataOffset;
        CopyExactly(input, output, sample.CompressedSize);
    }

    internal static void WriteXmaWaveHeader(Span<byte> header, Fsb3SampleInfo sample)
    {
        if (sample.Codec != Fsb3AudioCodec.Xma1)
            throw new ArgumentException("Sample is not XMA1", nameof(sample));
        XmaRiffAudio.WriteWaveHeader(
            header,
            sample.Channels,
            sample.SampleRate,
            sample.CompressedSize,
            sample.DecodedSampleCount);
    }

    private static string PrepareBankDirectory(string outputStem, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputStem)
            || Path.IsPathRooted(outputStem)
            || !Path.GetFileName(outputStem).Equals(outputStem, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Output stem must be a non-empty file-name stem without path components.",
                nameof(outputStem));
        }

        return Path.Combine(outputDirectory, SanitizeStem(outputStem));
    }

    private static string GetSampleStem(Fsb3SampleInfo sample)
    {
        var normalized = sample.Name.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(leaf);
        return $"{sample.Index:D5}_{SanitizeStem(stem)}";
    }

    private static string SanitizeStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => character < ' ' || invalid.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
            return "audio";

        return IsReservedDeviceName(cleaned) ? "_" + cleaned : cleaned;
    }

    private static bool IsReservedDeviceName(string stem)
    {
        var device = stem.Split('.', 2)[0];
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return device.Length == 4
               && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && device[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }

    private static string GetBankStem(string inputPath)
    {
        var name = Path.GetFileName(inputPath);
        const string xenSuffix = ".fsb.xen";
        const string ps3Suffix = ".fsb.ps3";
        const string plainSuffix = ".fsb";
        if (name.EndsWith(xenSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^xenSuffix.Length];
        if (name.EndsWith(ps3Suffix, StringComparison.OrdinalIgnoreCase))
            return name[..^ps3Suffix.Length];
        if (name.EndsWith(plainSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^plainSuffix.Length];

        return Path.GetFileNameWithoutExtension(name);
    }

    private static bool RunFfmpeg(string inputPath, string outputPath, out string error)
    {
        return XmaRiffAudio.RunFfmpeg(inputPath, outputPath, out error);
    }

    private static bool IsWaveFile(string path, Fsb3SampleInfo sample)
    {
        return XmaRiffAudio.IsPcm16WaveFile(path, sample.SampleRate, sample.Channels);
    }

    private static void CopyExactly(Stream input, Stream output, int count)
    {
        var buffer = new byte[81_920];
        var remaining = count;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException("FSB sample payload ended early");

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = stream.Read(destination[totalRead..]);
            if (read == 0)
                return false;
            totalRead += read;
        }

        return true;
    }

    private static AudioConvertResult NotThisFormat(string error)
    {
        return new AudioConvertResult { Skipped = true, ErrorMessage = error };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup for staged files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for staged files.
        }
    }
}
