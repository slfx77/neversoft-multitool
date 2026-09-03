using System.Buffers.Binary;
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
    internal const string InvalidMp4OutputError =
        "ffmpeg completed successfully but did not produce a recognizable MP4 file.";

    internal delegate SfdConvertResult FfmpegRunner(
        string ffmpeg,
        string arguments,
        string outputPath,
        double totalSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        byte[]? stdinData = null);

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
        var psmfAudio = FfmpegVideoFormats.IsPsmf(inputPath)
            ? PsmfAudioExtractor.Probe(inputPath)
            : null;
        if (ffprobe == null)
            return null;

        return RunProbe(ffprobe, $"-v quiet -print_format json -show_format -show_streams \"{inputPath}\"",
            inputPath, pssAudio, psmfAudio, null);
    }

    /// <summary>
    ///     In-memory variant of <see cref="Probe(string)" />. Pipes the bytes to
    ///     ffprobe via stdin. PSS audio probe is not run (PSS-in-archive is rare
    ///     and its probe requires path-based parsing).
    /// </summary>
    public static SfdProbeResult? Probe(byte[] data)
    {
        var ffprobe = FindFfprobe();
        if (ffprobe == null) return null;

        var psmfAudio = FfmpegVideoFormats.IsPsmf(data)
            ? PsmfAudioExtractor.Probe(data)
            : null;
        return RunProbe(ffprobe, "-v quiet -print_format json -show_format -show_streams -i -",
            "<stdin>", null, psmfAudio, data);
    }

    private static SfdProbeResult? RunProbe(
        string ffprobe, string arguments, string inputPathLabel,
        PssAudioExtractor.PssAudioProbeResult? pssAudio,
        PsmfAudioProbeResult? psmfAudio,
        byte[]? stdinData)
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
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        /* already closed */
                    }
                }
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0) return null;

            return ParseProbeJson(
                json,
                inputPathLabel,
                pssAudio,
                stdinData?.LongLength,
                psmfAudio);
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
        bool previewQuality = false,
        CancellationToken cancellationToken = default)
    {
        return ConvertToMp4(
            inputPath,
            outputDir,
            FindFfmpeg,
            Probe,
            RunFfmpeg,
            progress,
            previewQuality,
            outputStem: null,
            cancellationToken: cancellationToken);
    }

    internal static SfdConvertResult ConvertToMp4WithStem(
        string inputPath,
        string outputDir,
        string outputStem,
        IProgress<double>? progress = null,
        bool previewQuality = false,
        CancellationToken cancellationToken = default)
    {
        return ConvertToMp4(
            inputPath,
            outputDir,
            FindFfmpeg,
            Probe,
            RunFfmpeg,
            progress,
            previewQuality,
            outputStem,
            cancellationToken);
    }

    internal static SfdConvertResult ConvertToMp4(
        string inputPath,
        string outputDir,
        Func<string?> findFfmpeg,
        Func<string, SfdProbeResult?> probe,
        FfmpegRunner runFfmpeg,
        IProgress<double>? progress = null,
        bool previewQuality = false,
        string? outputStem = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(findFfmpeg);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(runFfmpeg);

        if (cancellationToken.IsCancellationRequested)
            return new SfdConvertResult { ErrorMessage = "Cancelled" };

        var ffmpeg = findFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        if (outputStem != null && !VideoOutputStemPlanner.IsSafeOutputStem(outputStem))
            return new SfdConvertResult { ErrorMessage = "Output stem must be a safe file-name stem." };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir,
            (outputStem ?? FfmpegVideoFormats.GetOutputStem(inputPath)) + ".mp4");
        var stagedOutputPath = CreateStagedOutputPath(outputDir);

        var probeResult = probe(inputPath);
        var totalSeconds = probeResult?.Duration.TotalSeconds ?? 0;
        string? tempAudioPath = null;

        try
        {
            string arguments;

            if (Path.GetExtension(inputPath).Equals(".pss", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryBuildPssArguments(inputPath, stagedOutputPath, previewQuality, out arguments,
                        out tempAudioPath,
                        out var tempError))
                {
                    throw new InvalidOperationException(tempError);
                }
            }
            else if (FfmpegVideoFormats.IsPsmf(inputPath))
            {
                if (!TryBuildPsmfArguments(
                        inputPath,
                        null,
                        stagedOutputPath,
                        previewQuality,
                        out arguments,
                        out tempAudioPath,
                        out var tempError))
                {
                    throw new InvalidOperationException(tempError);
                }
            }
            else
            {
                // Without an explicit map ffmpeg keeps only the FIRST audio
                // stream. Bink ships language tracks — 361 of the corpus's
                // 1,062 .bik.xen carry more than one (up to 16) — so the
                // default silently discarded them. Both maps are optional so
                // video-only and audio-only sources still convert.
                arguments =
                    $"-y -i \"{inputPath}\" -map 0:v:0? -map 0:a? {VideoEncodeArgs(previewQuality)} " +
                    $"-c:a aac -b:a 192k \"{stagedOutputPath}\"";
            }

            var result = runFfmpeg(
                ffmpeg,
                arguments,
                stagedOutputPath,
                totalSeconds,
                progress,
                cancellationToken);
            if (!result.Success)
                return new SfdConvertResult { ErrorMessage = result.ErrorMessage };

            if (cancellationToken.IsCancellationRequested)
                return new SfdConvertResult { ErrorMessage = "Cancelled" };

            if (!IsRecognizableMp4(stagedOutputPath))
                return new SfdConvertResult { ErrorMessage = InvalidMp4OutputError };

            if (cancellationToken.IsCancellationRequested)
                return new SfdConvertResult { ErrorMessage = "Cancelled" };

            File.Move(stagedOutputPath, outputPath, overwrite: true);
            return new SfdConvertResult { Success = true, OutputPath = outputPath };
        }
        catch (Exception ex)
        {
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDeleteFile(tempAudioPath);
            TryDeletePath(stagedOutputPath);
        }
    }

    /// <summary>
    ///     In-memory variant: pipes SFD bytes to ffmpeg via stdin (<c>-i -</c>).
    ///     Used for archive-sourced videos where no filesystem path exists.
    ///     PSS format is not supported via stdin (it needs a second audio-stream
    ///     temp-file input); callers with a PSS byte blob should fall back to a
    ///     temp file + the path-based overload. PSMF is supported: its private
    ///     ATRAC3+ stream is losslessly staged as OMA beside the piped video.
    /// </summary>
    public static SfdConvertResult ConvertToMp4(
        byte[] data,
        string stem,
        string outputDir,
        IProgress<double>? progress = null,
        bool previewQuality = false,
        CancellationToken cancellationToken = default)
    {
        return ConvertToMp4(
            data,
            stem,
            outputDir,
            FindFfmpeg,
            Probe,
            RunFfmpeg,
            progress,
            previewQuality,
            cancellationToken);
    }

    internal static SfdConvertResult ConvertToMp4(
        byte[] data,
        string stem,
        string outputDir,
        Func<string?> findFfmpeg,
        Func<byte[], SfdProbeResult?> probe,
        FfmpegRunner runFfmpeg,
        IProgress<double>? progress = null,
        bool previewQuality = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(findFfmpeg);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(runFfmpeg);

        if (cancellationToken.IsCancellationRequested)
            return new SfdConvertResult { ErrorMessage = "Cancelled" };

        var ffmpeg = findFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        if (!VideoOutputStemPlanner.IsSafeOutputStem(stem))
            return new SfdConvertResult { ErrorMessage = "Output stem must be a safe file-name stem." };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, stem + ".mp4");
        var stagedOutputPath = CreateStagedOutputPath(outputDir);

        var probeResult = probe(data);
        var totalSeconds = probeResult?.Duration.TotalSeconds ?? 0;
        string? tempAudioPath = null;

        try
        {
            string arguments;
            if (FfmpegVideoFormats.IsPsmf(data))
            {
                if (!TryBuildPsmfArguments(
                        null,
                        data,
                        stagedOutputPath,
                        previewQuality,
                        out arguments,
                        out tempAudioPath,
                        out var tempError))
                {
                    throw new InvalidOperationException(tempError);
                }
            }
            else
            {
                // ffmpeg stdin input — `-i -` tells ffmpeg to read from stdin.
                arguments =
                    $"-y -i - {VideoEncodeArgs(previewQuality)} -c:a aac -b:a 192k \"{stagedOutputPath}\"";
            }

            var result = runFfmpeg(
                ffmpeg,
                arguments,
                stagedOutputPath,
                totalSeconds,
                progress,
                cancellationToken,
                data);
            if (!result.Success)
                return new SfdConvertResult { ErrorMessage = result.ErrorMessage };

            if (cancellationToken.IsCancellationRequested)
                return new SfdConvertResult { ErrorMessage = "Cancelled" };

            if (!IsRecognizableMp4(stagedOutputPath))
                return new SfdConvertResult { ErrorMessage = InvalidMp4OutputError };

            if (cancellationToken.IsCancellationRequested)
                return new SfdConvertResult { ErrorMessage = "Cancelled" };

            File.Move(stagedOutputPath, outputPath, overwrite: true);
            return new SfdConvertResult { Success = true, OutputPath = outputPath };
        }
        catch (Exception ex)
        {
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDeleteFile(tempAudioPath);
            TryDeletePath(stagedOutputPath);
        }
    }

    private static bool TryBuildPsmfArguments(
        string? inputPath,
        byte[]? inputData,
        string outputPath,
        bool previewQuality,
        out string arguments,
        out string? tempAudioPath,
        out string error)
    {
        var candidateAudioPath = Path.Combine(
            Path.GetTempPath(),
            "NeversoftMultitool",
            "PsmfAudio",
            $"{Guid.NewGuid():N}.oma");
        tempAudioPath = candidateAudioPath;

        var extracted = inputPath != null
            ? PsmfAudioExtractor.TryWriteOma(inputPath, candidateAudioPath, out var audio, out error)
            : PsmfAudioExtractor.TryWriteOma(inputData!, candidateAudioPath, out audio, out error);
        if (!extracted)
        {
            arguments = "";
            return false;
        }

        var inputArgument = inputPath != null ? $"\"{inputPath}\"" : "-";
        if (!audio.HasAudio)
        {
            tempAudioPath = null;
            arguments =
                $"-y -i {inputArgument} -map 0:v:0 -an {VideoEncodeArgs(previewQuality)} " +
                $"\"{outputPath}\"";
            error = "";
            return true;
        }

        // FFmpeg's MPEG-PS demuxer does not expose PSP's private ATRAC stream,
        // but its OMA demuxer and native ATRAC3+ decoder do. -xerror makes a
        // decoder error fail the conversion instead of leaving shortened audio.
        arguments =
            $"-y -xerror -i {inputArgument} -i \"{candidateAudioPath}\" " +
            $"-map 0:v:0 -map 1:a:0 {VideoEncodeArgs(previewQuality)} " +
            $"-c:a aac -b:a 192k -shortest \"{outputPath}\"";
        error = "";
        return true;
    }

    private static bool TryBuildPssArguments(
        string inputPath,
        string outputPath,
        bool previewQuality,
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
            $"-y -i \"{inputPath}\" -i \"{tempAudioPath}\" -map 0:v:0 -map 1:a:0 {VideoEncodeArgs(previewQuality)} -c:a aac -b:a 192k -shortest \"{outputPath}\"";
        error = "";
        return true;
    }

    /// <summary>
    ///     Shared H.264 encode options. faststart moves the moov atom to the
    ///     front (seekable/streamable output, matching the STR/VID paths);
    ///     previews trade compression for startup latency with ultrafast.
    /// </summary>
    private static string VideoEncodeArgs(bool previewQuality)
    {
        var preset = previewQuality ? "ultrafast" : "fast";
        return $"-c:v libx264 -preset {preset} -crf 23 -pix_fmt yuv420p -movflags +faststart";
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
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        /* already closed */
                    }
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

    internal static SfdProbeResult? ParseProbeJson(
        string json,
        string inputPath,
        PssAudioExtractor.PssAudioProbeResult? pssAudio,
        long? fileSize = null,
        PsmfAudioProbeResult? psmfAudio = null)
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
            var hasUsableVideo = false;
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
                        var streamWidth = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        var streamHeight = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        if (streamWidth > 0 && streamHeight > 0)
                        {
                            hasUsableVideo = true;
                            width = streamWidth;
                            height = streamHeight;
                            videoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                            frameRate = 0;
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

            if (!hasUsableVideo)
                return null;

            if (audioCodec == null && pssAudio != null)
            {
                audioCodec = pssAudio.CodecName;
                audioSampleRate = pssAudio.SampleRate;
                audioChannels = pssAudio.Channels;
            }
            else if (audioCodec == null && psmfAudio?.HasAudio == true)
            {
                audioCodec = "atrac3p";
                audioSampleRate = psmfAudio.SampleRate;
                audioChannels = psmfAudio.Channels;
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
                FileSize = fileSize ?? new FileInfo(inputPath).Length
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

    private static string CreateStagedOutputPath(string outputDirectory)
    {
        return Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.tmp.mp4");
    }

    /// <summary>
    ///     A successful process exit is not sufficient: ffmpeg can leave an
    ///     empty or partial file behind after a broken pipe. Require a complete
    ///     leading ISO-BMFF file-type box before replacing a prior destination.
    /// </summary>
    internal static bool IsRecognizableMp4(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 16)
                return false;

            Span<byte> header = stackalloc byte[16];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                header.Length,
                FileOptions.SequentialScan);
            stream.ReadExactly(header);

            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            return header[4] == 'f'
                   && header[5] == 't'
                   && header[6] == 'y'
                   && header[7] == 'p'
                   && boxSize >= 16
                   && boxSize <= info.Length;
        }
        catch
        {
            return false;
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

    private static void TryDeletePath(string path)
    {
        TryDeleteFile(path);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})")]
    private static partial Regex TimePattern();
}
