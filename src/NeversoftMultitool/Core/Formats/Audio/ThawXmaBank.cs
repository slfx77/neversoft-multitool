using System.Buffers.Binary;
using QbKeyLookup = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Audio;

public sealed record ThawXmaSampleInfo(
    int Index,
    uint NameHash,
    string Name,
    long DataOffset,
    int CompressedSize,
    int SampleRate,
    int Channels,
    ushort Flags)
{
    public bool HasResolvedName => !Name.StartsWith("0x", StringComparison.Ordinal);
}

public sealed record ThawXmaBankInfo(
    int IndexSize,
    long DataSize,
    IReadOnlyList<ThawXmaSampleInfo> Samples);

/// <summary>
///     Strict reader for Tony Hawk's American Wasteland's Xbox 360 paired
///     <c>xma.dat</c>/<c>xma.wad</c> banks. The big-endian DAT is a hash-sorted
///     20-byte index whose ranges form a permutation that tiles the raw-XMA WAD.
/// </summary>
/// <remarks>
///     The accepted dialects are the two measured banks: 22,050 Hz mono effects
///     with flags 0, and 48 kHz stereo music with flags 0/0x100/0x200. Every
///     range must be 2 KiB-aligned, in bounds, gapless, non-overlapping, and
///     begin with the measured raw-XMA packet marker.
/// </remarks>
public static class ThawXmaBank
{
    public const int IndexHeaderSize = 4;
    public const int IndexEntrySize = 20;
    public const int XmaPacketSize = XmaRiffAudio.PacketSize;
    public const int XmaWaveHeaderSize = XmaRiffAudio.WaveHeaderSize;

    private const int MaximumEntryCount = 100_000;
    private const int EffectsSampleRate = 22_050;
    private const int MusicSampleRate = 48_000;
    private const ushort MusicFlagA = 0x0100;
    private const ushort MusicFlagB = 0x0200;

    internal delegate bool AudioTranscoder(string inputPath, string outputPath, out string error);

    /// <summary>
    ///     Matches the two corpus naming families without claiming unrelated
    ///     WAD archives. The companion index and payload are still fully parsed.
    /// </summary>
    public static bool HasSupportedFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var name = Path.GetFileName(path);
        return name.Equals("xma.wad", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_xma.wad", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetCompanionIndexName(string wadPath)
    {
        return Path.GetFileNameWithoutExtension(Path.GetFileName(wadPath)) + ".dat";
    }

    public static ThawXmaBankInfo? Probe(string wadPath)
    {
        try
        {
            var indexPath = GetCompanionIndexPath(wadPath);
            if (!File.Exists(indexPath))
                return null;

            using var wad = File.OpenRead(wadPath);
            return TryRead(wad, File.ReadAllBytes(indexPath), out var bank) ? bank : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ThawXmaBankInfo? Probe(byte[] wadData, byte[] indexData)
    {
        ArgumentNullException.ThrowIfNull(wadData);
        ArgumentNullException.ThrowIfNull(indexData);
        using var wad = new MemoryStream(wadData, writable: false);
        return TryRead(wad, indexData, out var bank) ? bank : null;
    }

    public static byte[] CreatePlayableStream(
        byte[] wadData,
        byte[] indexData,
        int sampleIndex)
    {
        ArgumentNullException.ThrowIfNull(wadData);
        ArgumentNullException.ThrowIfNull(indexData);
        using var wad = new MemoryStream(wadData, writable: false);
        if (!TryRead(wad, indexData, out var bank)
            || sampleIndex < 0
            || sampleIndex >= bank.Samples.Count)
        {
            return [];
        }

        var sample = bank.Samples[sampleIndex];
        var output = new byte[checked(sample.CompressedSize + XmaWaveHeaderSize)];
        using var destination = new MemoryStream(output, writable: true);
        WritePlayableStream(wad, sample, destination);
        return output;
    }

    public static AudioConvertResult ExtractEncoded(
        string wadPath,
        string outputStem,
        string outputDirectory)
    {
        try
        {
            var indexPath = GetCompanionIndexPath(wadPath);
            if (!File.Exists(indexPath))
                return NotThisFormat("Matching XMA .dat index not found");

            using var wad = File.OpenRead(wadPath);
            return ExtractEncoded(
                wad,
                File.ReadAllBytes(indexPath),
                outputStem,
                outputDirectory);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ExtractEncoded(
        byte[] wadData,
        byte[] indexData,
        string outputStem,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(wadData);
        ArgumentNullException.ThrowIfNull(indexData);
        try
        {
            using var wad = new MemoryStream(wadData, writable: false);
            return ExtractEncoded(wad, indexData, outputStem, outputDirectory);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(
        string wadPath,
        string outputStem,
        string outputDirectory)
    {
        try
        {
            var indexPath = GetCompanionIndexPath(wadPath);
            if (!File.Exists(indexPath))
                return NotThisFormat("Matching XMA .dat index not found");

            using var wad = File.OpenRead(wadPath);
            return ConvertToWav(
                wad,
                File.ReadAllBytes(indexPath),
                outputStem,
                outputDirectory,
                XmaRiffAudio.RunFfmpeg);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(
        byte[] wadData,
        byte[] indexData,
        string outputStem,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(wadData);
        ArgumentNullException.ThrowIfNull(indexData);
        try
        {
            using var wad = new MemoryStream(wadData, writable: false);
            return ConvertToWav(
                wad,
                indexData,
                outputStem,
                outputDirectory,
                XmaRiffAudio.RunFfmpeg);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    internal static AudioConvertResult ConvertToWav(
        byte[] wadData,
        byte[] indexData,
        string outputStem,
        string outputDirectory,
        AudioTranscoder transcoder)
    {
        ArgumentNullException.ThrowIfNull(wadData);
        ArgumentNullException.ThrowIfNull(indexData);
        using var wad = new MemoryStream(wadData, writable: false);
        return ConvertToWav(wad, indexData, outputStem, outputDirectory, transcoder);
    }

    public static string? ConvertSingleToWav(
        string wadPath,
        int sampleIndex,
        string outputDirectory)
    {
        try
        {
            var indexPath = GetCompanionIndexPath(wadPath);
            if (!File.Exists(indexPath))
                return null;

            using var wad = File.OpenRead(wadPath);
            return ConvertSingleToWav(
                wad,
                File.ReadAllBytes(indexPath),
                GetBankStem(wadPath),
                sampleIndex,
                outputDirectory,
                XmaRiffAudio.RunFfmpeg);
        }
        catch
        {
            return null;
        }
    }

    public static string? ConvertSingleToWav(
        byte[] wadData,
        byte[] indexData,
        string outputStem,
        int sampleIndex,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(wadData);
        ArgumentNullException.ThrowIfNull(indexData);
        try
        {
            using var wad = new MemoryStream(wadData, writable: false);
            return ConvertSingleToWav(
                wad,
                indexData,
                outputStem,
                sampleIndex,
                outputDirectory,
                XmaRiffAudio.RunFfmpeg);
        }
        catch
        {
            return null;
        }
    }

    private static AudioConvertResult ExtractEncoded(
        Stream wad,
        byte[] indexData,
        string outputStem,
        string outputDirectory)
    {
        if (!TryRead(wad, indexData, out var bank))
            return NotThisFormat("Not an exact THAW Xbox 360 XMA DAT/WAD pair");

        var bankDirectory = PrepareBankDirectory(outputStem, outputDirectory);
        Directory.CreateDirectory(bankDirectory);
        var filesWritten = 0;
        foreach (var sample in bank.Samples)
        {
            var outputPath = Path.Combine(bankDirectory, GetSampleStem(sample) + ".xma");
            var stagedPath = Path.Combine(bankDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(
                           stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WritePlayableStream(wad, sample, output);
                }

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
        Stream wad,
        byte[] indexData,
        string outputStem,
        string outputDirectory,
        AudioTranscoder transcoder)
    {
        if (!TryRead(wad, indexData, out var bank))
            return NotThisFormat("Not an exact THAW Xbox 360 XMA DAT/WAD pair");

        var bankDirectory = PrepareBankDirectory(outputStem, outputDirectory);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Xma");
        Directory.CreateDirectory(bankDirectory);
        Directory.CreateDirectory(tempDirectory);

        var filesWritten = 0;
        foreach (var sample in bank.Samples)
        {
            var encodedPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.xma");
            var outputPath = Path.Combine(bankDirectory, GetSampleStem(sample) + ".wav");
            var stagedOutputPath = Path.Combine(bankDirectory, $".{Guid.NewGuid():N}.wav");
            try
            {
                using (var encoded = new FileStream(
                           encodedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WritePlayableStream(wad, sample, encoded);
                }

                if (!transcoder(encodedPath, stagedOutputPath, out var error))
                    return new AudioConvertResult { ErrorMessage = error };
                if (!XmaRiffAudio.IsPcm16WaveFile(
                        stagedOutputPath, sample.SampleRate, sample.Channels))
                {
                    return new AudioConvertResult
                    {
                        ErrorMessage = $"Decoder produced no playable WAV for XMA sample {sample.Index}"
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
        Stream wad,
        byte[] indexData,
        string outputStem,
        int sampleIndex,
        string outputDirectory,
        AudioTranscoder transcoder)
    {
        if (!TryRead(wad, indexData, out var bank)
            || sampleIndex < 0
            || sampleIndex >= bank.Samples.Count)
        {
            return null;
        }

        var sample = bank.Samples[sampleIndex];
        var bankDirectory = PrepareBankDirectory(outputStem, outputDirectory);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Xma");
        Directory.CreateDirectory(bankDirectory);
        Directory.CreateDirectory(tempDirectory);
        var encodedPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.xma");
        var outputPath = Path.Combine(bankDirectory, GetSampleStem(sample) + ".wav");
        var stagedOutputPath = Path.Combine(bankDirectory, $".{Guid.NewGuid():N}.wav");
        try
        {
            using (var encoded = new FileStream(
                       encodedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WritePlayableStream(wad, sample, encoded);
            }

            if (!transcoder(encodedPath, stagedOutputPath, out _)
                || !XmaRiffAudio.IsPcm16WaveFile(
                    stagedOutputPath, sample.SampleRate, sample.Channels))
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

    private static bool TryRead(
        Stream wad,
        ReadOnlySpan<byte> indexData,
        out ThawXmaBankInfo bank)
    {
        bank = default!;
        if (!wad.CanRead || !wad.CanSeek
            || wad.Length <= 0
            || wad.Length > uint.MaxValue
            || indexData.Length < IndexHeaderSize)
        {
            return false;
        }

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(indexData);
        if (entryCount is 0 or > MaximumEntryCount
            || IndexHeaderSize + (long)entryCount * IndexEntrySize != indexData.Length)
        {
            return false;
        }

        var samples = new List<ThawXmaSampleInfo>(checked((int)entryCount));
        var previousHash = 0u;
        var bankSampleRate = 0;
        var bankChannels = 0;
        for (var index = 0; index < entryCount; index++)
        {
            var offset = checked(IndexHeaderSize + (int)index * IndexEntrySize);
            var entry = indexData.Slice(offset, IndexEntrySize);
            var nameHash = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var sampleRate = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);
            var channels = BinaryPrimitives.ReadUInt16BigEndian(entry[18..]);

            if (nameHash == 0
                || (index > 0 && nameHash <= previousHash)
                || compressedSize == 0
                || compressedSize > int.MaxValue / 2
                || (dataOffset % XmaPacketSize) != 0
                || (compressedSize % XmaPacketSize) != 0
                || (long)dataOffset + compressedSize > wad.Length
                || !IsSupportedDialect(sampleRate, channels, flags))
            {
                return false;
            }

            if (index == 0)
            {
                bankSampleRate = checked((int)sampleRate);
                bankChannels = channels;
            }
            else if (sampleRate != bankSampleRate || channels != bankChannels)
            {
                return false;
            }

            samples.Add(new ThawXmaSampleInfo(
                checked((int)index),
                nameHash,
                ResolveName(nameHash),
                dataOffset,
                checked((int)compressedSize),
                checked((int)sampleRate),
                channels,
                flags));
            previousHash = nameHash;
        }

        long expectedOffset = 0;
        Span<byte> marker = stackalloc byte[4];
        foreach (var sample in samples.OrderBy(sample => sample.DataOffset))
        {
            if (sample.DataOffset != expectedOffset)
                return false;

            wad.Position = sample.DataOffset;
            if (!TryReadExactly(wad, marker)
                || marker[0] != 0x08
                || marker[1] != 0
                || marker[2] != 0
                || marker[3] != 0)
            {
                return false;
            }

            expectedOffset = checked(expectedOffset + sample.CompressedSize);
        }

        if (expectedOffset != wad.Length)
            return false;

        bank = new ThawXmaBankInfo(indexData.Length, wad.Length, samples);
        return true;
    }

    private static bool IsSupportedDialect(uint sampleRate, ushort channels, ushort flags)
    {
        return sampleRate == EffectsSampleRate && channels == 1 && flags == 0
               || sampleRate == MusicSampleRate && channels == 2
               && flags is 0 or MusicFlagA or MusicFlagB;
    }

    private static string ResolveName(uint nameHash)
    {
        var known = QbKeyLookup.TryResolve(nameHash);
        return known != null && QbKeyLookup.HashLower(known) == nameHash
            ? known
            : $"0x{nameHash:X8}";
    }

    private static void WritePlayableStream(
        Stream wad,
        ThawXmaSampleInfo sample,
        Stream output)
    {
        Span<byte> header = stackalloc byte[XmaWaveHeaderSize];
        // This DAT dialect does not store decoded sample counts. The original
        // extractor uses compressedSize*2; ffmpeg terminates from XMA packet
        // metadata, and corpus controls decode identically across surrogate
        // values. Keep the finite, overflow-checked convention for compatibility.
        XmaRiffAudio.WriteWaveHeader(
            header,
            sample.Channels,
            sample.SampleRate,
            sample.CompressedSize,
            checked(sample.CompressedSize * 2));
        output.Write(header);

        wad.Position = sample.DataOffset;
        CopyExactly(wad, output, sample.CompressedSize);
    }

    private static string GetCompanionIndexPath(string wadPath)
    {
        var directory = Path.GetDirectoryName(wadPath);
        return Path.Combine(directory ?? "", GetCompanionIndexName(wadPath));
    }

    private static string GetBankStem(string wadPath)
    {
        return Path.GetFileNameWithoutExtension(Path.GetFileName(wadPath));
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

    private static string GetSampleStem(ThawXmaSampleInfo sample)
    {
        var normalized = sample.Name.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(leaf);
        return $"{sample.Index:D4}_{SanitizeStem(stem)}";
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

    private static void CopyExactly(Stream input, Stream output, int count)
    {
        var buffer = new byte[81_920];
        var remaining = count;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException("THAW XMA sample payload ended early");

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
        catch
        {
            // Best-effort cleanup must not mask the conversion result.
        }
    }
}
