using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool;

internal static class VideoConverterTabOperations
{
    public static bool IsVideoFile(string path)
    {
        // Ffmpeg-backed formats use the shared suffix registry. VID and STR
        // have their own in-process decoders, while TGR additionally needs a
        // content check because Neversoft also used that extension for data.
        return (FfmpegVideoFormats.HasVideoSuffix(path)
                && (!OrdinalFileName.HasExtension(path, ".tgr") || FfmpegVideoFormats.IsBink(path))
                && (!OrdinalFileName.HasExtension(path, ".smo") || FfmpegVideoFormats.IsThps4PcSmo(path)))
               || OrdinalFileName.HasExtension(path, ".vid")
               || OrdinalFileName.HasExtension(path, ".str");
    }

    /// <summary>
    ///     Archive-backed counterpart to <see cref="IsVideoFile(string)" />.
    ///     TGR is overloaded, so its bytes must identify Bink before the entry
    ///     is shown. Other formats preserve their established name-only gate.
    /// </summary>
    public static bool IsVideoFile(AssetSource source)
    {
        var isTgr = OrdinalFileName.HasExtension(source.EntryName, ".tgr");
        var isSmo = OrdinalFileName.HasExtension(source.EntryName, ".smo");
        if (!isTgr && !isSmo)
            return FfmpegVideoFormats.HasVideoSuffix(source.EntryName)
                   || OrdinalFileName.HasExtension(source.EntryName, ".vid")
                   || OrdinalFileName.HasExtension(source.EntryName, ".str");

        try
        {
            var data = source.ReadBytes();
            return isSmo
                ? FfmpegVideoFormats.IsThps4PcSmo(data)
                : FfmpegVideoFormats.IsBink(data);
        }
        catch
        {
            return false;
        }
    }

    public static IEnumerable<string> FindVideoFiles(string inputDir)
    {
        return Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
            .Where(static path => (OrdinalFileName.HasExtension(path, ".sfd") && IsMpegPsVideoFile(path))
                                  || (OrdinalFileName.HasExtension(path, ".pss") && IsMpegPsVideoFile(path))
                                  || OrdinalFileName.HasExtension(path, ".bik")
                                  || OrdinalFileName.HasSuffix(Path.GetFileName(path), ".bik.xen")
                                     && FfmpegVideoFormats.IsBink(path)
                                  || OrdinalFileName.HasSuffix(Path.GetFileName(path), ".pmf")
                                     && FfmpegVideoFormats.IsPsmf(path)
                                  || OrdinalFileName.HasExtension(path, ".tgr")
                                     && FfmpegVideoFormats.IsBink(path)
                                  || OrdinalFileName.HasExtension(path, ".smo")
                                     && FfmpegVideoFormats.IsThps4PcSmo(path)
                                  || (OrdinalFileName.HasExtension(path, ".vid") && IsVidVideoFile(path))
                                  || (OrdinalFileName.HasExtension(path, ".str") && IsStrVideoFile(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetRelativePath(inputDir, path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Creates an entry without probing: recursive scans can hit hundreds of
    ///     files, and ffprobe spawns a process per file. Duration/resolution are
    ///     filled in by a background pass (see the tab's ProbeEntriesAsync).
    /// </summary>
    public static SfdFileEntry CreateEntry(string filePath, string inputDir)
    {
        var fileInfo = new FileInfo(filePath);

        return new SfdFileEntry
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            SizeDisplay = FormatFileSize(fileInfo.Length),
            Source = new FileSystemAssetSource(filePath),
            RelativePath = Path.GetRelativePath(inputDir, filePath)
        };
    }

    /// <summary>
    ///     Creates an entry for an archive-backed video. We skip ffprobe during
    ///     scan to avoid spawning a process per archive entry; duration/resolution
    ///     remain empty and get populated the first time the user previews.
    /// </summary>
    public static SfdFileEntry CreateEntryForArchiveEntry(
        ArchiveAssetBackend backend, Core.Formats.Archives.ArchiveEntry archiveEntry)
    {
        var source = new ArchiveAssetSource(backend, archiveEntry);
        return new SfdFileEntry
        {
            FileName = archiveEntry.Name,
            FilePath = source.DisplayName,
            DurationDisplay = "",
            ResolutionDisplay = "",
            SizeDisplay = FormatFileSize(archiveEntry.Size),
            Source = source,
            // The full in-archive path is the stable identity used when two
            // entries share a leaf/output stem. Do not hash the archive's
            // absolute filesystem location into otherwise identical results.
            RelativePath = archiveEntry.FullName
        };
    }

    public static bool IsStrFormat(string path)
    {
        return OrdinalFileName.HasExtension(path, ".str");
    }

    public static bool IsVidFormat(string path)
    {
        return OrdinalFileName.HasExtension(path, ".vid");
    }

    public static bool IsFfmpegPassthroughFormat(string path)
    {
        return FfmpegVideoFormats.HasVideoSuffix(path);
    }

    public static bool IsStrVideoFile(string path)
    {
        if (!BinaryProbeReader.TryReadHeader(path, 16, out var header, out var bytesRead) || bytesRead < 16)
            return false;

        return !(header[0] == 'A' && header[1] == 'F' && header[2] == 'S' && header[3] == 0);
    }

    public static bool IsVidVideoFile(string path)
    {
        return Vid1VideoConverter.Probe(path) != null;
    }

    /// <summary>
    ///     SFD/PSS are MPEG program streams (pack header 00 00 01 BA). The sniff
    ///     keeps unrelated same-extension files (e.g. FontForge .sfd sources) out
    ///     of recursive scans over arbitrary folders.
    /// </summary>
    public static bool IsMpegPsVideoFile(string path)
    {
        return MpegProgramStreamProbe.HasPackHeader(path);
    }

    public static (string duration, string resolution) ProbeFile(string path)
    {
        if (IsStrFormat(path))
        {
            var probe = StrConverter.Probe(path);
            return (probe?.DurationDisplay ?? string.Empty, probe?.ResolutionDisplay ?? string.Empty);
        }

        if (OrdinalFileName.HasExtension(path, ".vid"))
        {
            var probe = Vid1VideoConverter.Probe(path);
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
        bool previewQuality = false,
        string? outputStem = null,
        CancellationToken cancellationToken = default)
    {
        if (IsStrFormat(path))
            return outputStem == null
                ? StrConverter.ConvertToMp4(path, outputDir, progress, cancellationToken)
                : StrConverter.ConvertToMp4WithStem(
                    path, outputDir, outputStem, progress, cancellationToken);

        if (OrdinalFileName.HasExtension(path, ".vid"))
            return outputStem == null
                ? Vid1VideoConverter.ConvertToMp4(path, outputDir, progress, cancellationToken)
                : Vid1VideoConverter.ConvertToMp4WithStem(
                    path, outputDir, outputStem, progress, cancellationToken);

        return outputStem == null
            ? SfdConverter.ConvertToMp4(path, outputDir, progress, previewQuality, cancellationToken)
            : SfdConverter.ConvertToMp4WithStem(
                path, outputDir, outputStem, progress, previewQuality, cancellationToken);
    }

    public static SfdConvertResult ConvertFromSource(
        SfdFileEntry entry,
        string outputDir,
        IProgress<double>? progress = null,
        string? outputStem = null,
        CancellationToken cancellationToken = default)
    {
        // Filesystem-backed entries go through the existing path-based pipeline
        // (preserves PSS audio muxing + STR/VID codepaths).
        if (entry.Source.FileSystemPath is { } filePath)
            return ConvertFile(
                filePath,
                outputDir,
                progress,
                outputStem: outputStem,
                cancellationToken: cancellationToken);

        // Archive-backed: for SFD, pipe bytes to ffmpeg stdin. For STR/VID, fall
        // back to a temp file since those converters need a seekable path for
        // their custom decoders.
        var stem = outputStem ?? FfmpegVideoFormats.GetOutputStem(entry.FileName);
        var data = entry.Source.ReadBytes();

        if (OrdinalFileName.HasExtension(entry.FileName, ".sfd") ||
            OrdinalFileName.HasExtension(entry.FileName, ".bik"))
        {
            return SfdConverter.ConvertToMp4(data, stem, outputDir, progress, cancellationToken: cancellationToken);
        }

        // Temp-file fallback for STR / VID / PSS from archives. Keep the
        // original leaf name so converters retain deterministic output stems
        // and VID1 can classify intro/atvi by basename.
        using var staged = ArchiveVideoTempFile.Write("ArchiveVideo", entry.FileName, data);
        return ConvertFile(
            staged.Path,
            outputDir,
            progress,
            outputStem: outputStem,
            cancellationToken: cancellationToken);
    }

    public static string FormatTime(TimeSpan ts)
    {
        return TimeDisplay.Format(ts);
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
