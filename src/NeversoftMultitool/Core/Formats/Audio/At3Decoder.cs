using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Converts Sony ATRAC3 / ATRAC3plus <c>.at3</c> sounds to WAV by handing
///     them to ffmpeg, which carries native <c>atrac3</c> and <c>atrac3p</c>
///     decoders.
/// </summary>
/// <remarks>
///     The PSP builds ship 12,122 of these (1.4 GiB, ~25 hours) — the largest
///     unreachable audio population in the corpus. They are ordinary RIFF/WAVE
///     containers whose <c>fmt </c> tag is 0xFFFE (WAVE_FORMAT_EXTENSIBLE); the
///     measured corpus is 12,122/12,122 RIFF-size-exact and ends its data chunk
///     exactly at EOF, and all 12,122 decode with ffmpeg reporting no errors and
///     a sample count agreeing with the <c>fact</c> chunk.
///     This is a shell-out rather than a native decoder because ATRAC3 is a
///     licensed Sony codec with a large decoder; ffmpeg is already a hard
///     dependency of the video path and of VID1 audio.
/// </remarks>
public static class At3Decoder
{
    /// <summary>True when the bytes are a RIFF/WAVE container (the .at3 shape).</summary>
    public static bool IsAt3(ReadOnlySpan<byte> data)
    {
        return data.Length >= 12
               && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
               && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        return ConvertToWav(inputPath, Path.GetFileNameWithoutExtension(inputPath), outputDir);
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string stem, string outputDir)
    {
        try
        {
            if (!IsAt3(ReadHeader(inputPath)))
            {
                return new AudioConvertResult
                {
                    Skipped = true,
                    ErrorMessage = "Not a RIFF/WAVE ATRAC3 container"
                };
            }

            return Decode(inputPath, stem, outputDir);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    ///     Archive-backed variant. ffmpeg needs a seekable input, so the bytes
    ///     are staged to a temp file rather than piped.
    /// </summary>
    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir)
    {
        if (!IsAt3(data))
        {
            return new AudioConvertResult
            {
                Skipped = true,
                ErrorMessage = "Not a RIFF/WAVE ATRAC3 container"
            };
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "At3");
        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.at3");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempPath, data);
            return Decode(tempPath, stem, outputDir);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static AudioConvertResult Decode(string inputPath, string stem, string outputDir)
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new AudioConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, stem + ".wav");

        if (!TryRunFfmpeg(ffmpeg, inputPath, outputPath, out var error))
        {
            TryDelete(outputPath);
            return new AudioConvertResult { ErrorMessage = error };
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 44)
        {
            TryDelete(outputPath);
            return new AudioConvertResult { ErrorMessage = "ffmpeg produced no audio" };
        }

        // SamplesWritten counts WAV FILES, not PCM samples (the batch summary
        // sums it as "N WAV files" and the per-file line prints "N samples"
        // only for bank extractors that emit more than one).
        return new AudioConvertResult { Success = true, SamplesWritten = 1 };
    }

    private static bool TryRunFfmpeg(string ffmpeg, string inputPath, string outputPath, out string error)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -loglevel error -i \"{inputPath}\" -acodec pcm_s16le \"{outputPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

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

    private static byte[] ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[12];
        var read = stream.Read(header, 0, header.Length);
        return read == header.Length ? header : [];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
