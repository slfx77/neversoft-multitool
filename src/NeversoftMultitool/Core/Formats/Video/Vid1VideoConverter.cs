using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Core.Formats.Video;

public static partial class Vid1VideoConverter
{
    public static Vid1VideoProbeResult? Probe(string inputPath)
    {
        return TryProbe(inputPath, out var probe, out _)
            ? probe
            : null;
    }

    public static SfdConvertResult ConvertToMp4(
        string inputPath,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryProbe(inputPath, out var probe, out var error))
            return new SfdConvertResult { ErrorMessage = error };

        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(inputPath) + ".mp4");
        var tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Vid1Video", Guid.NewGuid().ToString("N"));
        var tempVideoPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputPath) + ".m4v");
        var tempAudioDir = Path.Combine(tempDir, "audio");
        var tempAudioPath = Path.Combine(tempAudioDir, Path.GetFileNameWithoutExtension(inputPath) + ".wav");

        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(tempAudioDir);
            progress?.Report(0.05);

            if (!Vid1VideoFile.TryParse(inputPath, out var file, out error))
                return new SfdConvertResult { ErrorMessage = error };

            var audioResult = Vid1AudioExtractor.ConvertToWav(inputPath, tempAudioDir);
            var hasAudio = audioResult.Success && File.Exists(tempAudioPath);

            progress?.Report(0.10);

            if (!RunNativeDecodePipeline(ffmpeg, file!, hasAudio ? tempAudioPath : null, outputPath,
                    progress, cancellationToken, out error))
                return new SfdConvertResult { ErrorMessage = error };

            progress?.Report(1.0);
            return new SfdConvertResult { Success = true, OutputPath = outputPath };
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDeleteFile(tempVideoPath);
            TryDeleteFile(tempAudioPath);
            TryDeleteDirectory(tempAudioDir);
            TryDeleteDirectory(tempDir);
        }
    }

    public static SfdConvertResult DecodeFrames(
        string inputPath,
        string outputDir,
        CancellationToken cancellationToken = default)
    {
        if (!TryProbe(inputPath, out _, out var error))
            return new SfdConvertResult { ErrorMessage = error };

        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Vid1Video", Guid.NewGuid().ToString("N"));
        var tempVideoPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputPath) + ".m4v");

        try
        {
            Directory.CreateDirectory(tempDir);
            if (!TryWriteDeterministicVideoStream(inputPath, tempVideoPath, out error))
                return new SfdConvertResult { ErrorMessage = error };

            var framePattern = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(inputPath)}_%04d.png");
            var arguments =
                $"-y -err_detect ignore_err -i \"{tempVideoPath}\" -vsync 0 \"{framePattern}\"";

            if (!TryRunProcess(ffmpeg, arguments, out _, out error, cancellationToken))
                return new SfdConvertResult { ErrorMessage = error };

            return new SfdConvertResult { Success = true, OutputPath = outputDir };
        }
        finally
        {
            TryDeleteFile(tempVideoPath);
            TryDeleteDirectory(tempDir);
        }
    }

    internal static SfdConvertResult DecodeNativeFrames(
        string inputPath,
        string outputDir,
        CancellationToken cancellationToken = default)
    {
        if (!TryProbe(inputPath, out _, out var error))
            return new SfdConvertResult { ErrorMessage = error };

        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);

        try
        {
            if (!Vid1VideoFile.TryParse(inputPath, out var file, out error))
                return new SfdConvertResult { ErrorMessage = error };

            var framePattern = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(inputPath)}_%04d.png");
            if (!RunNativeFrameExport(ffmpeg, file!, framePattern, cancellationToken, out error))
                return new SfdConvertResult { ErrorMessage = error };

            return new SfdConvertResult { Success = true, OutputPath = outputDir };
        }
        catch (Exception ex)
        {
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
    }

    internal static bool TryProbe(string inputPath, out Vid1VideoProbeResult? probe, out string error)
    {
        probe = null;
        if (!Vid1VideoFile.TryParse(inputPath, out var file, out error))
            return false;

        var audioProbe = Vid1AudioExtractor.Probe(inputPath);
        var fileInfo = new FileInfo(inputPath);
        probe = new Vid1VideoProbeResult
        {
            Duration = file!.Duration,
            Width = file.Width,
            Height = file.Height,
            FrameCount = file.FrameCount,
            FrameRate = file.FrameRate,
            Variant = file.Variant,
            FileSize = fileInfo.Length,
            HasAudio = audioProbe != null,
            AudioSampleRate = audioProbe?.SampleRate ?? 0,
            AudioChannels = audioProbe?.Channels ?? 0
        };
        error = "";
        return true;
    }

    internal static bool TryWriteDeterministicVideoStream(string inputPath, string outputPath, out string error)
    {
        error = "";

        if (!Vid1VideoFile.TryParse(inputPath, out var file, out error))
            return false;

        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
        {
            error = "ffmpeg not found on PATH";
            return false;
        }

        if (!Vid1VideoRebuilder.TryBuildPrefix(ffmpeg, file!.Width, file.Height, file.FrameRate, out var prefix, out error))
            return false;

        var candidate = Vid1VideoRebuilder.BuildDeterministicCandidateStream(prefix, file);
        if (candidate.Length == 0)
        {
            error = "VID1 rebuilder did not emit any video data";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, candidate);
        return true;
    }

    internal static bool TryRunProcess(
        string fileName,
        string arguments,
        out string stderr,
        out string error,
        CancellationToken cancellationToken = default)
    {
        stderr = "";
        error = "";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // The child process already exited while the timeout path was unwinding.
                }

                error = $"{Path.GetFileName(fileName)} timed out";
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                error = "Cancelled";
                return false;
            }

            if (process.ExitCode == 0)
                return true;

            error = string.IsNullOrWhiteSpace(stderr)
                ? $"{Path.GetFileName(fileName)} exited with code {process.ExitCode}"
                : stderr.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool RunNativeDecodePipeline(
        string ffmpegPath,
        Vid1VideoFile file,
        string? audioPath,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        out string error)
    {
        error = "";

        var videoInput = $"-y -f rawvideo -pix_fmt rgb24 -s {file.Width}x{file.Height} " +
                         $"-r {file.FrameRate.ToString("F2", CultureInfo.InvariantCulture)} -i pipe:0";
        var args = audioPath != null
            ? $"{videoInput} -i \"{audioPath}\" -map 0:v:0 -map 1:a:0 -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 192k -shortest \"{outputPath}\""
            : $"{videoInput} -c:v libx264 -preset fast -crf 23 -an \"{outputPath}\"";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var stderrBuf = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuf.AppendLine(e.Data);
        };

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { process.Kill(); } catch { /* already dead */ }
        });

        process.Start();
        process.BeginErrorReadLine();

        var provider = new Vid1PresentationFrameProvider(file);
        var rgbBuffer = new byte[file.Width * file.Height * 3];
        var frameLimit = GetDebugFrameLimit(file.Frames.Count);
        var totalFrames = frameLimit;
        var decodeProgressBase = 0.10;
        var decodeProgressSpan = 0.85;

        try
        {
            using var stdin = process.StandardInput.BaseStream;
            for (var i = 0; i < frameLimit; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { /* already dead */ }
                    TryDeleteFile(outputPath);
                    error = "Cancelled";
                    return false;
                }

                if (!provider.TryDecodeNextFrame(rgbBuffer, out _))
                    break;

                try
                {
                    stdin.Write(rgbBuffer, 0, rgbBuffer.Length);
                }
                catch (IOException)
                {
                    // ffmpeg exited early — stop piping.
                    break;
                }

                if (totalFrames > 0)
                {
                    progress?.Report(decodeProgressBase + (decodeProgressSpan * (i + 1) / totalFrames));
                }
            }
        }
        catch (Exception ex)
        {
            try { process.Kill(); } catch { /* already dead */ }
            TryDeleteFile(outputPath);
            error = $"native decode failed: {ex.Message}";
            return false;
        }

        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch { /* already dead */ }
            TryDeleteFile(outputPath);
            error = "ffmpeg timed out";
            return false;
        }

        if (process.ExitCode != 0)
        {
            TryDeleteFile(outputPath);
            var tail = stderrBuf.ToString();
            error = string.IsNullOrWhiteSpace(tail)
                ? $"ffmpeg exited with code {process.ExitCode}"
                : $"ffmpeg exited with code {process.ExitCode}: {tail.Trim()}";
            return false;
        }

        return true;
    }

    private static bool RunNativeFrameExport(
        string ffmpegPath,
        Vid1VideoFile file,
        string framePattern,
        CancellationToken cancellationToken,
        out string error)
    {
        error = "";

        var args = $"-y -f rawvideo -pix_fmt rgb24 -s {file.Width}x{file.Height} " +
                   $"-r {file.FrameRate.ToString("F2", CultureInfo.InvariantCulture)} -i pipe:0 " +
                   $"-vsync 0 \"{framePattern}\"";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var stderrBuf = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuf.AppendLine(e.Data);
        };

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { process.Kill(); } catch { /* already dead */ }
        });

        process.Start();
        process.BeginErrorReadLine();

        var provider = new Vid1PresentationFrameProvider(file);
        var rgbBuffer = new byte[file.Width * file.Height * 3];
        var frameLimit = GetDebugFrameLimit(file.Frames.Count);

        try
        {
            using var stdin = process.StandardInput.BaseStream;
            for (var i = 0; i < frameLimit; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { /* already dead */ }
                    error = "Cancelled";
                    return false;
                }

                if (!provider.TryDecodeNextFrame(rgbBuffer, out _))
                    break;

                stdin.Write(rgbBuffer, 0, rgbBuffer.Length);
            }
        }
        catch (Exception ex)
        {
            try { process.Kill(); } catch { /* already dead */ }
            error = $"native frame export failed: {ex.Message}";
            return false;
        }

        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(); } catch { /* already dead */ }
            error = "ffmpeg timed out";
            return false;
        }

        if (process.ExitCode == 0)
            return true;

        var tail = stderrBuf.ToString();
        error = string.IsNullOrWhiteSpace(tail)
            ? $"ffmpeg exited with code {process.ExitCode}"
            : $"ffmpeg exited with code {process.ExitCode}: {tail.Trim()}";
        return false;
    }

    private static int GetDebugFrameLimit(int availableFrames)
    {
        var value = Environment.GetEnvironmentVariable("VID1_MAX_FRAMES");
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
            requested <= 0)
        {
            return availableFrames;
        }

        return Math.Min(requested, availableFrames);
    }

    private static bool RunFfmpeg(
        string ffmpegPath,
        string arguments,
        string outputPath,
        double totalSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        out string error)
    {
        error = "";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();

        while (!process.StandardError.EndOfStream)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                TryDeleteFile(outputPath);
                error = "Cancelled";
                return false;
            }

            var line = process.StandardError.ReadLine();
            if (line == null || totalSeconds <= 0)
                continue;

            var match = TimePattern().Match(line);
            if (!match.Success)
                continue;

            var currentSeconds =
                double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) +
                double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) / 100.0;
            progress?.Report(Math.Min((0.35 + (currentSeconds / totalSeconds * 0.65)), 1.0));
        }

        process.WaitForExit(30_000);
        if (process.ExitCode == 0)
            return true;

        TryDeleteFile(outputPath);
        error = $"ffmpeg exited with code {process.ExitCode}";
        return false;
    }

    internal static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [GeneratedRegex(@"time=(\d+):(\d+):(\d+)\.(\d+)")]
    private static partial Regex TimePattern();
}
