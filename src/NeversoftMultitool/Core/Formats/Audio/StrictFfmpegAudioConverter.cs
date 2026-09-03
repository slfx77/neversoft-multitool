using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core.Formats.Audio;

internal delegate bool AudioPcmTranscoder(string inputPath, string outputPath, out string error);

/// <summary>
///     Shared fail-closed ffmpeg bridge for single-stream compressed audio.
///     Inputs staged from archives use unguessable names, decoder output is
///     validated before publication, and an existing destination is untouched
///     unless a complete PCM16 WAV has been produced.
/// </summary>
internal static class StrictFfmpegAudioConverter
{
    internal static AudioConvertResult ConvertPath(
        string inputPath,
        string outputStem,
        string outputDirectory,
        int sampleRate,
        int channels,
        string formatName,
        AudioPcmTranscoder? transcoder = null)
    {
        return ConvertValidatedInput(
            inputPath,
            outputStem,
            outputDirectory,
            sampleRate,
            channels,
            formatName,
            transcoder ?? RunFfmpeg);
    }

    internal static AudioConvertResult ConvertBytes(
        byte[] data,
        string stagedExtension,
        string outputStem,
        string outputDirectory,
        int sampleRate,
        int channels,
        string formatName,
        AudioPcmTranscoder? transcoder = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(stagedExtension)
            || stagedExtension[0] != '.'
            || stagedExtension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("A safe staged-file extension is required.", nameof(stagedExtension));
        }

        string? stagedInputPath = null;
        try
        {
            var stagingDirectory = Path.Combine(
                Path.GetTempPath(), "NeversoftMultitool", "CompressedAudio");
            Directory.CreateDirectory(stagingDirectory);
            stagedInputPath = Path.Combine(
                stagingDirectory, $"{Guid.NewGuid():N}{stagedExtension}");
            using (var stagedInput = new FileStream(
                       stagedInputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stagedInput.Write(data);
            }

            return ConvertValidatedInput(
                stagedInputPath,
                outputStem,
                outputDirectory,
                sampleRate,
                channels,
                formatName,
                transcoder ?? RunFfmpeg);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDelete(stagedInputPath);
        }
    }

    private static AudioConvertResult ConvertValidatedInput(
        string inputPath,
        string outputStem,
        string outputDirectory,
        int sampleRate,
        int channels,
        string formatName,
        AudioPcmTranscoder transcoder)
    {
        string? stagedOutputPath = null;
        try
        {
            var outputPath = PrepareOutputPath(outputStem, outputDirectory);
            stagedOutputPath = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.wav");

            if (!transcoder(inputPath, stagedOutputPath, out var error))
                return new AudioConvertResult { ErrorMessage = error };

            if (!File.Exists(stagedOutputPath)
                || !XmaRiffAudio.IsPcm16WaveFile(stagedOutputPath, sampleRate, channels))
            {
                return new AudioConvertResult
                {
                    ErrorMessage =
                        $"ffmpeg produced no playable PCM16 WAV with the authored {formatName} audio layout"
                };
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
            TryDelete(stagedOutputPath);
        }
    }

    private static bool RunFfmpeg(string inputPath, string outputPath, out string error)
    {
        return RunFfmpegCore(inputPath, outputPath, failOnDecodeError: false, out error);
    }

    /// <summary>
    ///     Decoder-errors-are-fatal variant for rewrapped streams whose entire
    ///     authored payload has already passed a strict structural parser.
    /// </summary>
    internal static bool RunFfmpegWithXError(string inputPath, string outputPath, out string error)
    {
        return RunFfmpegCore(inputPath, outputPath, failOnDecodeError: true, out error);
    }

    private static bool RunFfmpegCore(
        string inputPath,
        string outputPath,
        bool failOnDecodeError,
        out string error)
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

        if (failOnDecodeError)
            process.StartInfo.ArgumentList.Insert(1, "-xerror");

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and Kill.
            }

            error = "ffmpeg timed out while decoding compressed audio";
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
            // Best-effort cleanup for staged inputs and failed outputs.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for staged inputs and failed outputs.
        }
    }
}
