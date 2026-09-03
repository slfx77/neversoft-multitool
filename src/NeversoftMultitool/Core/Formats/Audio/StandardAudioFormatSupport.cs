using System.Buffers.Binary;
using System.Diagnostics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Content-gated routing for ordinary RIFF/WAVE and Windows Media Audio.
///     WAV is already the Audio tab's destination format, so valid input is
///     copied losslessly. WMA is an ASF container and is decoded to PCM16 by
///     ffmpeg, the same optional dependency used for ATRAC3 and video.
/// </summary>
internal static class StandardAudioFormatSupport
{
    private static readonly byte[] AsfHeaderObjectGuid =
        [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static readonly byte[] AsfDataObjectGuid =
        [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static readonly byte[] AsfFilePropertiesObjectGuid =
        [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

    private static readonly byte[] AsfStreamPropertiesObjectGuid =
        [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

    private static readonly byte[] AsfAudioMediaGuid =
        [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

    public static readonly string[] Extensions = [".wav", ".wma"];

    internal delegate bool WmaTranscoder(string inputPath, string outputPath, out string error);

    internal readonly record struct ProbeResult(double? DurationSeconds);

    public static string? DetectFormat(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".wav" => "WAV",
            ".wma" => "WMA",
            _ => null
        };
    }

    public static ProbeResult? ProbeWave(ReadOnlySpan<byte> data)
    {
        if (!RiffWaveReader.TryRead(data, out var info)
            || info.FormatTag <= 0
            || info.Channels <= 0
            || info.SampleRate <= 0
            || info.BlockAlign <= 0
            || info.DataLength <= 0)
        {
            return null;
        }

        // RiffWaveReader deliberately clamps corrupt Neversoft declarations for
        // .pcm/.snd. A file presented as an ordinary .wav must instead contain
        // its complete declared payload; otherwise pass-through would bless a
        // truncated file that MediaPlayer cannot reliably seek.
        var declaredDataLength = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(info.DataOffset - sizeof(uint), sizeof(uint)));
        if (declaredDataLength > data.Length - info.DataOffset)
            return null;

        var declaredRiffLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var declaredDataEnd = info.DataOffset + (long)declaredDataLength;
        if ((long)declaredRiffLength + 8 > data.Length
            || (long)declaredRiffLength + 8 < declaredDataEnd)
            return null;

        var duration = info.AvgBytesPerSec > 0
            ? info.DataLength / (double)info.AvgBytesPerSec
            : (double?)null;
        return new ProbeResult(duration is > 0 && double.IsFinite(duration.Value) ? duration : null);
    }

    /// <summary>
    ///     Validates the ASF object graph far enough to distinguish WMA from a
    ///     video-only WMV: a File Properties object, an audio Stream Properties
    ///     object with WAVEFORMATEX data, and its matching Data object are all
    ///     required and bounds-checked.
    /// </summary>
    public static ProbeResult? ProbeWindowsMediaAudio(ReadOnlySpan<byte> data)
    {
        const int headerObjectBytes = 30;
        const int objectHeaderBytes = 24;
        const int filePropertiesBytes = 104;
        const int streamPropertiesFixedBytes = 78;
        const int waveFormatExBytes = 16;
        const int dataObjectBytes = 50;

        if (data.Length < headerObjectBytes
            || !data[..16].SequenceEqual(AsfHeaderObjectGuid)
            || data[28] != 0x01
            || data[29] != 0x02)
        {
            return null;
        }

        var headerSize64 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16, 8));
        if (headerSize64 < headerObjectBytes || headerSize64 > (ulong)data.Length || headerSize64 > int.MaxValue)
            return null;

        var headerSize = (int)headerSize64;
        var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4));
        if (objectCount == 0 || objectCount > (headerSize - headerObjectBytes) / objectHeaderBytes)
            return null;

        ReadOnlySpan<byte> fileId = default;
        ulong declaredPacketCount = 0;
        double? durationSeconds = null;
        var hasAudioStream = false;
        var offset = headerObjectBytes;

        for (var objectIndex = 0U; objectIndex < objectCount; objectIndex++)
        {
            if (offset > headerSize - objectHeaderBytes)
                return null;

            var objectSize64 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 16, 8));
            if (objectSize64 < objectHeaderBytes || objectSize64 > int.MaxValue)
                return null;

            var objectSize = (int)objectSize64;
            if (objectSize > headerSize - offset)
                return null;

            var objectGuid = data.Slice(offset, 16);
            if (objectGuid.SequenceEqual(AsfFilePropertiesObjectGuid))
            {
                if (objectSize < filePropertiesBytes)
                    return null;

                fileId = data.Slice(offset + objectHeaderBytes, 16);
                declaredPacketCount = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 56, 8));
                var playDuration = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 64, 8));
                var prerollMilliseconds = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 80, 8));
                var seconds = playDuration / 10_000_000d - prerollMilliseconds / 1_000d;
                if (seconds > 0 && double.IsFinite(seconds))
                    durationSeconds = seconds;
            }
            else if (objectGuid.SequenceEqual(AsfStreamPropertiesObjectGuid))
            {
                if (objectSize < streamPropertiesFixedBytes)
                    return null;

                var typeSpecificLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 64, 4));
                var errorCorrectionLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 68, 4));
                var requiredSize = (long)streamPropertiesFixedBytes + typeSpecificLength + errorCorrectionLength;
                if (requiredSize > objectSize)
                    return null;

                var isAudio = data.Slice(offset + objectHeaderBytes, 16).SequenceEqual(AsfAudioMediaGuid);
                if (isAudio)
                {
                    if (typeSpecificLength < waveFormatExBytes)
                        return null;

                    var waveFormat = data.Slice(offset + streamPropertiesFixedBytes, waveFormatExBytes);
                    var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(waveFormat);
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(waveFormat.Slice(2, 2));
                    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(waveFormat.Slice(4, 4));
                    var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(waveFormat.Slice(12, 2));
                    var streamNumber = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 72, 2)) & 0x7F;
                    if (formatTag == 0 || channels == 0 || sampleRate == 0 || blockAlign == 0 || streamNumber == 0)
                        return null;

                    hasAudioStream = true;
                }
            }

            offset += objectSize;
        }

        if (offset != headerSize || fileId.IsEmpty || !hasAudioStream)
            return null;

        if (headerSize > data.Length - dataObjectBytes
            || !data.Slice(headerSize, 16).SequenceEqual(AsfDataObjectGuid))
        {
            return null;
        }

        var dataObjectSize = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(headerSize + 16, 8));
        var dataPacketCount = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(headerSize + 40, 8));
        if (dataObjectSize < dataObjectBytes
            || dataObjectSize > (ulong)(data.Length - headerSize)
            || declaredPacketCount == 0
            || dataPacketCount != declaredPacketCount
            || !data.Slice(headerSize + 24, 16).SequenceEqual(fileId)
            || data[headerSize + 48] != 0x01
            || data[headerSize + 49] != 0x01)
        {
            return null;
        }

        return new ProbeResult(durationSeconds);
    }

    /// <summary>
    ///     Converts a supported single-stream format, or returns <see langword="null" />
    ///     when <paramref name="audioFormat" /> belongs to another converter family.
    /// </summary>
    public static AudioConvertResult? ConvertToWav(
        string audioFormat,
        byte[] data,
        string outputStem,
        string outputDirectory)
    {
        return audioFormat.ToUpperInvariant() switch
        {
            "WAV" => CopyWave(data, outputStem, outputDirectory),
            "WMA" => ConvertWindowsMediaToWav(data, outputStem, outputDirectory, RunFfmpeg),
            _ => null
        };
    }

    private static AudioConvertResult CopyWave(byte[] data, string outputStem, string outputDirectory)
    {
        if (ProbeWave(data) == null)
            return NotThisFormat("Not a complete, playable RIFF/WAVE file");

        try
        {
            var outputPath = PrepareOutputPath(outputStem, outputDirectory);
            var stagedOutputPath = CreateStagedOutputPath(outputDirectory);
            try
            {
                File.WriteAllBytes(stagedOutputPath, data);
                File.Move(stagedOutputPath, outputPath, overwrite: true);
            }
            finally
            {
                TryDelete(stagedOutputPath);
            }

            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    internal static AudioConvertResult ConvertWindowsMediaToWav(
        byte[] data,
        string outputStem,
        string outputDirectory,
        WmaTranscoder transcoder)
    {
        if (ProbeWindowsMediaAudio(data) == null)
            return NotThisFormat("Not an ASF container with a valid audio stream");

        string? stagedOutputPath = null;
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Wma");
        var inputPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.wma");
        try
        {
            var outputPath = PrepareOutputPath(outputStem, outputDirectory);
            stagedOutputPath = CreateStagedOutputPath(outputDirectory);
            Directory.CreateDirectory(stagingDirectory);
            File.WriteAllBytes(inputPath, data);

            if (!transcoder(inputPath, stagedOutputPath, out var error))
            {
                return new AudioConvertResult { ErrorMessage = error };
            }

            if (!File.Exists(stagedOutputPath)
                || ProbeWave(File.ReadAllBytes(stagedOutputPath)) == null)
            {
                return new AudioConvertResult { ErrorMessage = "ffmpeg produced no playable WAV audio" };
            }

            File.Move(stagedOutputPath, outputPath, overwrite: true);
            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(stagedOutputPath);
        }
    }

    private static bool RunFfmpeg(string inputPath, string outputPath, out string error)
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
        {
            error = "ffmpeg not found on PATH";
            return false;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-y", "-loglevel", "error", "-i", inputPath,
                     "-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", outputPath
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the timeout and Kill.
            }

            error = "ffmpeg timed out while decoding WMA audio";
            return false;
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode == 0 && File.Exists(outputPath))
        {
            error = "";
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"ffmpeg exited with code {process.ExitCode}"
            : stderr.Trim();
        return false;
    }

    private static string PrepareOutputPath(string outputStem, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputStem)
            || Path.IsPathRooted(outputStem)
            || !Path.GetFileName(outputStem).Equals(outputStem, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Output stem must be a non-empty file-name stem without path components.",
                nameof(outputStem));
        }

        Directory.CreateDirectory(outputDirectory);
        return Path.Combine(outputDirectory, outputStem + ".wav");
    }

    private static string CreateStagedOutputPath(string outputDirectory)
    {
        return Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.wav");
    }

    private static AudioConvertResult NotThisFormat(string error)
    {
        return new AudioConvertResult { Skipped = true, ErrorMessage = error };
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup for staged or failed outputs.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for staged or failed outputs.
        }
    }
}
