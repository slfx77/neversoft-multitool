using System.Diagnostics;
using System.Text;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Converts PS1 STR (MDEC) video files to MP4.
///     Pipeline: demux → decode frames (MdecDecoder) → pipe raw RGB to ffmpeg stdin.
///     Audio: extract XA sectors → XaDecoder → temp WAV → ffmpeg muxes audio+video.
/// </summary>
public static class StrConverter
{
    /// <summary>
    ///     Probes an STR file for metadata without fully decoding it.
    /// </summary>
    public static StrProbeResult? Probe(string inputPath)
    {
        try
        {
            var data = File.ReadAllBytes(inputPath);
            if (!StrDemuxer.IsStrFile(data))
                return null;

            var firstFrame = StrDemuxer.EnumerateFrames(data).FirstOrDefault();
            if (firstFrame == null)
                return null;

            return new StrProbeResult
            {
                Width = firstFrame.Width,
                Height = firstFrame.Height,
                FrameCount = StrDemuxer.CountFrames(data),
                HasAudio = StrDemuxer.HasAudio(data),
                FileSize = new FileInfo(inputPath).Length
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Converts an STR file to MP4 using MDEC decoding + ffmpeg encoding.
    /// </summary>
    public static SfdConvertResult ConvertToMp4(
        string inputPath,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new SfdConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".mp4");

        string? tempXaPath = null;
        string? tempWavPath = null;

        try
        {
            var data = File.ReadAllBytes(inputPath);
            if (!StrDemuxer.IsStrFile(data))
                return new SfdConvertResult { ErrorMessage = "Not a valid PS1 STR file" };

            var frames = StrDemuxer.EnumerateFrames(data).ToList();
            if (frames.Count == 0)
                return new SfdConvertResult { ErrorMessage = "No video frames found in STR file" };

            var width = frames[0].Width;
            var height = frames[0].Height;

            // Prepare audio track if present
            (tempXaPath, tempWavPath) = PrepareAudio(data, inputPath);

            // Build ffmpeg args and run
            var ffmpegArgs = BuildFfmpegArgs(width, height, tempWavPath, outputPath);
            return RunFfmpegPipeline(ffmpeg, ffmpegArgs, frames,
                outputPath, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new SfdConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDeleteFile(tempXaPath);
            TryDeleteFile(tempWavPath);
        }
    }

    private static (string? tempXaPath, string? tempWavPath) PrepareAudio(byte[] data, string inputPath)
    {
        if (!StrDemuxer.HasAudio(data))
            return (null, null);

        var audioSectors = StrDemuxer.ExtractAudioSectors(data);
        if (audioSectors.Length == 0)
            return (null, null);

        var tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "StrConvert");
        Directory.CreateDirectory(tempDir);

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var tempXaPath = Path.Combine(tempDir, stem + ".xa");
        File.WriteAllBytes(tempXaPath, audioSectors);

        var audioResult = XaDecoder.ConvertToWav(tempXaPath, tempDir);
        if (!audioResult.Success)
            return (tempXaPath, null);

        // XaDecoder creates {stem}.wav or a subdirectory for multi-channel
        var expectedWav = Path.Combine(tempDir, stem + ".wav");
        if (File.Exists(expectedWav))
            return (tempXaPath, expectedWav);

        // Multi-channel: use first channel
        var subDir = Path.Combine(tempDir, stem);
        if (Directory.Exists(subDir))
        {
            var wavFiles = Directory.GetFiles(subDir, "*.wav");
            if (wavFiles.Length > 0)
                return (tempXaPath, wavFiles[0]);
        }

        return (tempXaPath, null);
    }

    private static string BuildFfmpegArgs(int width, int height, string? audioPath, string outputPath)
    {
        var videoInput = $"-y -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r 15 -i pipe:0";

        return audioPath != null
            ? $"{videoInput} -i \"{audioPath}\" -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 128k -shortest \"{outputPath}\""
            : $"{videoInput} -c:v libx264 -preset fast -crf 23 -an \"{outputPath}\"";
    }

    private static SfdConvertResult RunFfmpegPipeline(
        string ffmpeg, string args, List<StrDemuxer.StrFrame> frames,
        string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var width = frames[0].Width;
        var height = frames[0].Height;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var stderrOutput = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderrOutput.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        PipeFrames(process.StandardInput.BaseStream, frames,
            width, height, progress, cancellationToken, out var cancelled);

        if (cancelled)
        {
            process.Kill();
            TryDeleteFile(outputPath);
            return new SfdConvertResult { ErrorMessage = "Cancelled" };
        }

        try
        {
            process.StandardInput.BaseStream.Close();
        }
        catch
        {
            /* pipe may already be broken */
        }

        process.WaitForExit(60_000);

        // Pipe break with exit code 0 is normal when -shortest ends encoding early
        if (process.ExitCode != 0)
        {
            TryDeleteFile(outputPath);
            var lastLine = stderrOutput.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim();
            return new SfdConvertResult
            {
                ErrorMessage = lastLine ?? $"ffmpeg exited with code {process.ExitCode}"
            };
        }

        return new SfdConvertResult { Success = true, OutputPath = outputPath };
    }

    private static void PipeFrames(Stream stdin, List<StrDemuxer.StrFrame> frames,
        int width, int height, IProgress<double>? progress,
        CancellationToken cancellationToken, out bool cancelled)
    {
        cancelled = false;
        for (var i = 0; i < frames.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return;
            }

            try
            {
                var rgb = MdecDecoder.DecodeFrame(frames[i].Data, width, height);
                stdin.Write(rgb, 0, rgb.Length);
            }
            catch (IOException)
            {
                return; // ffmpeg died — pipe broken
            }
            catch
            {
                // Decode error — write black frame
                try
                {
                    stdin.Write(new byte[width * height * 3]);
                }
                catch (IOException)
                {
                    return;
                }
            }

            progress?.Report((double)(i + 1) / frames.Count);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (path == null) return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }
}
