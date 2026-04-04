using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool;

internal static class VideoConverterTabOperations
{
    public static IEnumerable<string> FindVideoFiles(string inputDir)
    {
        return Directory.EnumerateFiles(inputDir, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => OrdinalFileName.HasExtension(path, ".sfd")
                                  || OrdinalFileName.HasExtension(path, ".pss")
                                  || OrdinalFileName.HasExtension(path, ".bik")
                                  || (OrdinalFileName.HasExtension(path, ".str") && IsStrVideoFile(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    public static SfdFileEntry CreateEntry(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var (duration, resolution) = ProbeFile(filePath);

        return new SfdFileEntry
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            DurationDisplay = duration,
            ResolutionDisplay = resolution,
            SizeDisplay = FormatFileSize(fileInfo.Length)
        };
    }

    public static bool IsStrFormat(string path)
    {
        return OrdinalFileName.HasExtension(path, ".str");
    }

    public static bool IsFfmpegPassthroughFormat(string path)
    {
        return OrdinalFileName.HasExtension(path, ".sfd")
               || OrdinalFileName.HasExtension(path, ".pss")
               || OrdinalFileName.HasExtension(path, ".bik");
    }

    public static bool IsStrVideoFile(string path)
    {
        if (!BinaryProbeReader.TryReadHeader(path, 16, out var header, out var bytesRead) || bytesRead < 16)
            return false;

        return !(header[0] == 'A' && header[1] == 'F' && header[2] == 'S' && header[3] == 0);
    }

    public static (string duration, string resolution) ProbeFile(string path)
    {
        if (IsStrFormat(path))
        {
            var probe = StrConverter.Probe(path);
            return (probe?.DurationDisplay ?? string.Empty, probe?.ResolutionDisplay ?? string.Empty);
        }

        // SFD, PSS, BIK — all probed via ffprobe
        var sfdProbe = SfdConverter.Probe(path);
        return (sfdProbe?.DurationDisplay ?? string.Empty, sfdProbe?.ResolutionDisplay ?? string.Empty);
    }

    public static SfdConvertResult ConvertFile(
        string path,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // STR uses custom MDEC decoder; SFD/PSS/BIK use ffmpeg passthrough
        return IsStrFormat(path)
            ? StrConverter.ConvertToMp4(path, outputDir, progress, cancellationToken)
            : SfdConverter.ConvertToMp4(path, outputDir, progress, cancellationToken);
    }

    public static string FormatTime(TimeSpan ts)
    {
        return ts.TotalMinutes >= 60
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    public static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }
}
