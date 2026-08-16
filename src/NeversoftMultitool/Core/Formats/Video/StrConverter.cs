using System.Diagnostics;
using System.Globalization;
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
    private const string ScratchAudioStem = "audio";

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

        string? tempAudioDirectory = null;
        string? tempWavPath = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = File.ReadAllBytes(inputPath);

            cancellationToken.ThrowIfCancellationRequested();

            if (!StrDemuxer.IsStrFile(data))
                return new SfdConvertResult { ErrorMessage = "Not a valid PS1 STR file" };

            var frames = StrDemuxer.EnumerateFrames(data).ToList();
            if (frames.Count == 0)
                return new SfdConvertResult { ErrorMessage = "No video frames found in STR file" };

            cancellationToken.ThrowIfCancellationRequested();

            var width = frames[0].Width;
            var height = frames[0].Height;

            // Prepare audio track if present
            tempAudioDirectory = Path.Combine(
                Path.GetTempPath(),
                "NeversoftMultitool",
                "StrConvert",
                Guid.NewGuid().ToString("N"));
            tempWavPath = PrepareAudio(data, inputPath, tempAudioDirectory);

            cancellationToken.ThrowIfCancellationRequested();

            // Build ffmpeg args and run
            var frameRate = StrDemuxer.GetFrameRate(data);
            var ffmpegArgs = BuildFfmpegArgs(width, height, frameRate, tempWavPath, outputPath);
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
            TryDeleteDirectory(tempAudioDirectory);
        }
    }

    internal static string? PrepareAudio(byte[] data, string inputPath, string scratchDirectory)
    {
        if (!StrDemuxer.HasAudio(data))
            return null;

        var audioSectors = StrDemuxer.ExtractAudioSectors(data);
        if (audioSectors.Length == 0)
            return null;

        Directory.CreateDirectory(scratchDirectory);
        // Never derive a path inside owned scratch from an input leaf: names
        // such as "...str" reduce to ".." and would escape the GUID directory.
        _ = inputPath;
        var channelInfo = XaExtractor.EnumerateChannels(audioSectors);
        var audioResult = XaDecoder.ConvertToWav(audioSectors, ScratchAudioStem, scratchDirectory);
        if (!audioResult.Success)
            return null;

        string? expectedWav = null;
        if (channelInfo.Count == 1)
        {
            expectedWav = Path.Combine(scratchDirectory, ScratchAudioStem + ".wav");
        }
        else if (channelInfo.Count > 1)
        {
            expectedWav = Path.Combine(
                scratchDirectory,
                ScratchAudioStem,
                $"ch{channelInfo[0].ChannelNumber:D2}.wav");
        }

        return expectedWav != null && File.Exists(expectedWav) ? expectedWav : null;
    }

    private static string BuildFfmpegArgs(int width, int height, double frameRate, string? audioPath, string outputPath)
    {
        var formattedFrameRate = frameRate.ToString("F2", CultureInfo.InvariantCulture);
        var videoInput = $"-y -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r {formattedFrameRate} -i pipe:0";

        return audioPath != null
            ? $"{videoInput} -i \"{audioPath}\" -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -movflags +faststart " +
              $"-c:a aac -b:a 128k -shortest \"{outputPath}\""
            : $"{videoInput} -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -movflags +faststart -an \"{outputPath}\"";
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

        // Register cancellation to kill the process if it's still running
        using var processKillRegistration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch
            {
                /* process may have already exited */
            }
        });

        process.Start();
        process.BeginErrorReadLine();

        PipeFrames(process.StandardInput.BaseStream, frames,
            width, height, progress, cancellationToken, out var cancelled, out var blackFrames);

        if (cancelled || cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                /* already dead */
            }

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

        return new SfdConvertResult
        {
            Success = true,
            OutputPath = outputPath,
            BlackFramesSubstituted = blackFrames
        };
    }

    /// <summary>
    ///     Writes each decoded frame to ffmpeg. A frame that fails to decode is
    ///     written as black rather than aborting the conversion, and
    ///     <paramref name="blackFrames" /> reports how many, because the
    ///     substitution is otherwise silent — a stream that fails on every frame
    ///     yields a wholly black video and a successful result.
    /// </summary>
    private static void PipeFrames(Stream stdin, List<StrDemuxer.StrFrame> frames,
        int width, int height, IProgress<double>? progress,
        CancellationToken cancellationToken, out bool cancelled, out int blackFrames)
    {
        cancelled = false;
        blackFrames = 0;
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
                blackFrames++;
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

    internal static void TryDeleteDirectory(string? path)
    {
        if (path == null) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            /* best effort */
        }
    }
}
