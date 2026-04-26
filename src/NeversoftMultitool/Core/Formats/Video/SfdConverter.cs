using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Converts CRI Sofdec (SFD) video files to MP4 using ffmpeg.
///     SFD files are MPEG-PS containers with MPEG-1 video and ADX audio,
///     used in Dreamcast games (Spider-Man DC, THPS2 DC).
/// </summary>
public static partial class SfdConverter
{
    private static string? _ffmpegPath;
    private static string? _ffprobePath;
    private static bool _searched;

    /// <summary>
    ///     Finds ffmpeg on the system PATH. Caches the result.
    /// </summary>
    public static string? FindFfmpeg()
    {
        if (!_searched)
        {
            _ffmpegPath = FindExecutable("ffmpeg");
            _ffprobePath = FindExecutable("ffprobe");
            _searched = true;
        }

        return _ffmpegPath;
    }

    /// <summary>
    ///     Finds ffprobe on the system PATH. Calls FindFfmpeg() if not yet searched.
    /// </summary>
    public static string? FindFfprobe()
    {
        if (!_searched) FindFfmpeg();
        return _ffprobePath;
    }

    /// <summary>
    ///     Probes an SFD file for metadata using ffprobe.
    ///     Returns null if ffprobe is not available or the file cannot be probed.
    /// </summary>
    public static SfdProbeResult? Probe(string inputPath)
    {
        var ffprobe = FindFfprobe();
        var isPss = Path.GetExtension(inputPath).Equals(".pss", StringComparison.OrdinalIgnoreCase);
        var pssAudio = isPss ? PssAudioExtractor.Probe(inputPath) : null;
        if (ffprobe == null)
            return null;

        return RunProbe(ffprobe, $"-v quiet -print_format json -show_format -show_streams \"{inputPath}\"",
            inputPath, pssAudio, stdinData: null);
    }

    /// <summary>
    ///     In-memory variant of <see cref="Probe(string)"/>. Pipes the bytes to
    ///     ffprobe via stdin. PSS audio probe is not run (PSS-in-archive is rare
    ///     and its probe requires path-based parsing).
    /// </summary>
    public static SfdProbeResult? Probe(byte[] data)
    {
        var ffprobe = FindFfprobe();
        if (ffprobe == null) return null;

        return RunProbe(ffprobe, "-v quiet -print_format json -show_format -show_streams -i -",
            inputPathLabel: "<stdin>", pssAudio: null, stdinData: data);
    }

    private static SfdProbeResult? RunProbe(
        string ffprobe, string arguments, string inputPathLabel,
        PssAudioExtractor.PssAudioProbeResult? pssAudio, byte[]? stdinData)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = stdinData != null,
                CreateNoWindow = true
            };

            process.Start();

            if (stdinData != null)
            {
                try
                {
                    process.StandardInput.BaseStream.Write(stdinData, 0, stdinData.Length);
                    process.StandardInput.BaseStream.Flush();
                }
                catch
                {
                    // ffprobe may close stdin early
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch { /* already closed */ }
                }
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0) return null;

            return ParseProbeJson(json, inputPathLabel, pssAudio);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Converts an SFD file to MP4 (H.264 + AAC) using ffmpeg.
    /// </summary>
    public static SfdConvertResult ConvertToMp4(
        string inputPath,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".mp4");

        var probe = Probe(inputPath);
        var totalSeconds = probe?.Duration.TotalSeconds ?? 0;

        try
        {
            string? tempAudioPath = null;
            string arguments;

            if (Path.GetExtension(inputPath).Equals(".pss", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryBuildPssArguments(inputPath, outputPath, out arguments, out tempAudioPath, out var tempError))
                {
                    TryDeleteFile(tempAudioPath);
                    throw new InvalidOperationException(tempError);
                }
            }
            else
            {
                arguments =
                    $"-y -i \"{inputPath}\" -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 192k \"{outputPath}\"";
            }

            var result = RunFfmpeg(ffmpeg, arguments, outputPath, totalSeconds, progress, cancellationToken);
            TryDeleteFile(tempAudioPath);
            return result;
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    ///     In-memory variant: pipes SFD bytes to ffmpeg via stdin (<c>-i -</c>).
    ///     Used for archive-sourced videos where no filesystem path exists.
    ///     PSS format is not supported via stdin (it needs a second audio-stream
    ///     temp-file input); callers with a PSS byte blob should fall back to a
    ///     temp file + the path-based overload.
    /// </summary>
    public static SfdConvertResult ConvertToMp4(
        byte[] data,
        string stem,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, stem + ".mp4");

        var probe = Probe(data);
        var totalSeconds = probe?.Duration.TotalSeconds ?? 0;

        try
        {
            // ffmpeg stdin input — `-i -` tells ffmpeg to read from stdin.
            var arguments =
                $"-y -i - -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 192k \"{outputPath}\"";
            return RunFfmpeg(ffmpeg, arguments, outputPath, totalSeconds, progress, cancellationToken, data);
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
    }

    private static bool TryBuildPssArguments(
        string inputPath,
        string outputPath,
        out string arguments,
        out string? tempAudioPath,
        out string error)
    {
        tempAudioPath = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "PssAudio",
            $"{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(inputPath)}.wav");

        if (!PssAudioExtractor.TryWriteWav(inputPath, tempAudioPath, out error))
        {
            arguments = "";
            tempAudioPath = null;
            return false;
        }

        arguments =
            $"-y -i \"{inputPath}\" -i \"{tempAudioPath}\" -map 0:v:0 -map 1:a:0 -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 192k -shortest \"{outputPath}\"";
        error = "";
        return true;
    }

    private static SfdConvertResult RunFfmpeg(
        string ffmpeg,
        string arguments,
        string outputPath,
        double totalSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        byte[]? stdinData = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData != null,
            CreateNoWindow = true
        };

        process.Start();

        if (stdinData != null)
        {
            // Feed bytes on a background task so we can keep draining stderr in
            // parallel (ffmpeg's pipe can block otherwise).
            _ = Task.Run(() =>
            {
                try
                {
                    process.StandardInput.BaseStream.Write(stdinData, 0, stdinData.Length);
                    process.StandardInput.BaseStream.Flush();
                }
                catch
                {
                    // ffmpeg may close stdin early on decode errors; stderr will explain.
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch { /* already closed */ }
                }
            }, cancellationToken);
        }

        while (!process.StandardError.EndOfStream)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                TryDeleteFile(outputPath);
                return new SfdConvertResult { ErrorMessage = "Cancelled" };
            }

            var line = process.StandardError.ReadLine();
            if (line != null && totalSeconds > 0)
            {
                var match = TimePattern().Match(line);
                if (match.Success)
                {
                    var currentSeconds =
                        double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                        double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                        double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) +
                        double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) / 100.0;
                    progress?.Report(Math.Min(currentSeconds / totalSeconds, 1.0));
                }
            }
        }

        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            TryDeleteFile(outputPath);
            return new SfdConvertResult { ErrorMessage = $"ffmpeg exited with code {process.ExitCode}" };
        }

        return new SfdConvertResult { Success = true, OutputPath = outputPath };
    }

    private static SfdProbeResult? ParseProbeJson(
        string json,
        string inputPath,
        PssAudioExtractor.PssAudioProbeResult? pssAudio)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var duration = TimeSpan.Zero;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationEl) &&
                double.TryParse(durationEl.GetString(), CultureInfo.InvariantCulture, out var secs))
            {
                duration = TimeSpan.FromSeconds(secs);
            }

            var width = 0;
            var height = 0;
            double frameRate = 0;
            string? videoCodec = null;
            string? audioCodec = null;
            var audioSampleRate = 0;
            var audioChannels = 0;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;

                    if (codecType == "video")
                    {
                        if (stream.TryGetProperty("width", out var w)) width = w.GetInt32();
                        if (stream.TryGetProperty("height", out var h)) height = h.GetInt32();
                        if (stream.TryGetProperty("codec_name", out var cn)) videoCodec = cn.GetString();
                        if (stream.TryGetProperty("r_frame_rate", out var fr))
                        {
                            var parts = fr.GetString()?.Split('/');
                            if (parts?.Length == 2 &&
                                double.TryParse(parts[0], CultureInfo.InvariantCulture, out var num) &&
                                double.TryParse(parts[1], CultureInfo.InvariantCulture, out var den) &&
                                den > 0)
                                frameRate = num / den;
                        }
                    }
                    else if (codecType == "audio")
                    {
                        if (stream.TryGetProperty("codec_name", out var cn)) audioCodec = cn.GetString();
                        if (stream.TryGetProperty("sample_rate", out var sr) &&
                            int.TryParse(sr.GetString(), CultureInfo.InvariantCulture, out var rate))
                            audioSampleRate = rate;
                        if (stream.TryGetProperty("channels", out var ch)) audioChannels = ch.GetInt32();
                    }
                }
            }

            if (audioCodec == null && pssAudio != null)
            {
                audioCodec = pssAudio.CodecName;
                audioSampleRate = pssAudio.SampleRate;
                audioChannels = pssAudio.Channels;
            }

            return new SfdProbeResult
            {
                Duration = duration,
                Width = width,
                Height = height,
                FrameRate = frameRate,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                AudioSampleRate = audioSampleRate,
                AudioChannels = audioChannels,
                FileSize = new FileInfo(inputPath).Length
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            var command = OperatingSystem.IsWindows() ? "where" : "which";
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            process.Start();
            var output = process.StandardOutput.ReadLine()?.Trim();
            process.WaitForExit(5_000);

            return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})")]
    private static partial Regex TimePattern();
}
