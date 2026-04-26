using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool;

internal static class AudioConverterTabOperations
{
    private static readonly string[] SupportedExtensions = [".adx", ".xa", ".vab", ".vag", ".kat", ".sfx", ".pss", ".vid"];

    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public static List<string> FindAudioFiles(string inputDir)
    {
        var allFiles = Directory.GetFiles(inputDir);
        var audioFiles = allFiles
            .Where(static file => SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .ToList();

        audioFiles.AddRange(allFiles
            .Where(static file => string.IsNullOrEmpty(Path.GetExtension(file)))
            .Where(static file => VagDecoder.Probe(file) != null));

        return audioFiles;
    }

    public static string DetectFormat(string extension)
    {
        return extension switch
        {
            ".adx" => "ADX",
            ".xa" => "XA",
            ".vab" => "VAB",
            ".vag" => "VAG",
            ".pss" => "PSS",
            ".vid" => "VID",
            ".kat" => "KAT",
            ".sfx" => "SFX",
            "" => "VAG",
            _ => "Unknown"
        };
    }

    public static List<AudioSampleEntry> EnumerateChildren(AssetSource source, string parentFileName, string audioFormat)
    {
        var data = source.ReadBytes();
        return audioFormat switch
        {
            "VAB" => VabExtractor.EnumerateSamples(data)
                .Select(sample => new AudioSampleEntry
                {
                    ParentFileName = parentFileName,
                    SampleIndex = sample.Index,
                    Encoding = "SPU-ADPCM",
                    SampleRate = sample.SampleRate,
                    Channels = 1,
                    DataSize = sample.DataSize
                })
                .ToList(),
            "KAT" => KatExtractor.EnumerateSamples(data)
                .Select(sample => new AudioSampleEntry
                {
                    ParentFileName = parentFileName,
                    SampleIndex = sample.Index,
                    Encoding = sample.Encoding,
                    SampleRate = sample.SampleRate,
                    Channels = sample.Channels,
                    DataSize = sample.DataSize
                })
                .ToList(),
            "SFX" => EnumerateSfxSamples(source, data, parentFileName),
            _ => []
        };
    }

    private static List<AudioSampleEntry> EnumerateSfxSamples(AssetSource source, byte[] data, string parentFileName)
    {
        var bankBytes = TryResolveSfxBankFromSource(source);
        var filesystemPath = source.FileSystemPath;

        var samples = bankBytes is { } bb
            ? SfxExtractor.EnumerateSamples(data, bb)
            : (filesystemPath != null ? SfxExtractor.EnumerateSamples(filesystemPath) : []);

        return samples.Select(sample => new AudioSampleEntry
        {
            ParentFileName = parentFileName,
            SampleIndex = sample.CueIndex,
            Encoding = $"{sample.Encoding} via {sample.BankFormat} #{sample.BankSampleIndex:D3}",
            SampleRate = sample.SampleRate,
            Channels = sample.Channels,
            DataSize = sample.DataSize
        }).ToList();
    }

    /// <summary>
    ///     Try to pull a companion KAT/VAB from the same <see cref="AssetSource"/>
    ///     (works for both filesystem and archive sources because of how
    ///     <see cref="AssetSource.TryReadCompanion(string)"/> resolves siblings).
    /// </summary>
    private static SfxExtractor.SfxBankBytes? TryResolveSfxBankFromSource(AssetSource source)
    {
        var stem = Path.GetFileNameWithoutExtension(source.EntryName);

        var katBytes = source.TryReadCompanion(stem + ".kat") ?? source.TryReadCompanion(stem + ".KAT");
        if (katBytes != null) return new SfxExtractor.SfxBankBytes(katBytes, "KAT");

        var vabBytes = source.TryReadCompanion(stem + ".vab") ?? source.TryReadCompanion(stem + ".VAB");
        if (vabBytes != null) return new SfxExtractor.SfxBankBytes(vabBytes, "VAB");

        return null;
    }

    public static AudioConvertResult ConvertFile(
        AudioFileEntry entry,
        string outputDir,
        int vabSampleRate)
    {
        var source = entry.Source;
        var data = source.ReadBytes();
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);

        return entry.AudioFormat switch
        {
            "ADX" => AdxDecoder.ConvertToWav(data, stem, outputDir),
            "XA" => XaDecoder.ConvertToWav(data, stem, outputDir),
            "VAB" => VabExtractor.ExtractToWav(data, stem, outputDir, vabSampleRate),
            "VAG" => VagDecoder.ConvertToWav(data, stem, outputDir),
            "PSS" => PssAudioExtractor.ConvertToWav(data, stem, outputDir),
            "VID" => Vid1AudioExtractor.ConvertToWav(data, stem, outputDir),
            "KAT" => KatExtractor.ExtractToWav(data, stem, outputDir),
            "SFX" => ConvertSfxFile(source, data, stem, outputDir),
            _ => new AudioConvertResult { ErrorMessage = "Unknown format" }
        };
    }

    private static AudioConvertResult ConvertSfxFile(AssetSource source, byte[] data, string stem, string outputDir)
    {
        var bankBytes = TryResolveSfxBankFromSource(source);
        if (bankBytes is { } bb)
            return SfxExtractor.ExtractToWav(data, stem, bb, outputDir);

        // Filesystem fallback so the cross-sibling alias search still runs
        return source.FileSystemPath != null
            ? SfxExtractor.ExtractToWav(source.FileSystemPath, outputDir)
            : new AudioConvertResult { ErrorMessage = "SFX companion KAT/VAB not found in archive" };
    }

    public static string? ConvertForPreview(
        IListEntry item,
        string tempDir,
        IReadOnlyList<AudioFileEntry> parentFiles,
        int vabSampleRate)
    {
        Directory.CreateDirectory(tempDir);

        if (item is AudioFileEntry parent)
            return ConvertFilePreview(parent, tempDir);

        if (item is AudioSampleEntry sample)
            return ConvertSamplePreview(sample, tempDir, parentFiles, vabSampleRate);

        return null;
    }

    public static string FormatTime(TimeSpan ts)
    {
        return ts.TotalMinutes >= 60
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static string? ConvertFilePreview(AudioFileEntry parent, string tempDir)
    {
        var data = parent.Source.ReadBytes();
        var stem = Path.GetFileNameWithoutExtension(parent.FileName);

        var result = parent.AudioFormat switch
        {
            "ADX" => AdxDecoder.ConvertToWav(data, stem, tempDir),
            "XA" => XaDecoder.ConvertToWav(data, stem, tempDir),
            "VAG" => VagDecoder.ConvertToWav(data, stem, tempDir),
            "PSS" => PssAudioExtractor.ConvertToWav(data, stem, tempDir),
            "VID" => Vid1AudioExtractor.ConvertToWav(data, stem, tempDir),
            _ => null
        };

        if (result is not { Success: true })
            return null;

        var wavPath = Path.Combine(tempDir, stem + ".wav");
        if (File.Exists(wavPath))
            return wavPath;

        var channelPath = Path.Combine(tempDir, stem, "ch00.wav");
        return File.Exists(channelPath) ? channelPath : null;
    }

    private static string? ConvertSamplePreview(
        AudioSampleEntry sample,
        string tempDir,
        IReadOnlyList<AudioFileEntry> parentFiles,
        int vabSampleRate)
    {
        var parentEntry = parentFiles.FirstOrDefault(parent =>
            parent.FileName.Equals(sample.ParentFileName, StringComparison.OrdinalIgnoreCase));
        if (parentEntry == null)
            return null;

        var data = parentEntry.Source.ReadBytes();
        var stem = Path.GetFileNameWithoutExtension(parentEntry.FileName);

        return parentEntry.AudioFormat switch
        {
            "VAB" => VabExtractor.ExtractSingleToWav(data, stem, sample.SampleIndex, tempDir, vabSampleRate),
            "KAT" => KatExtractor.ExtractSingleToWav(data, stem, sample.SampleIndex, tempDir),
            "SFX" => ConvertSfxSamplePreview(parentEntry.Source, data, stem, sample.SampleIndex, tempDir),
            _ => null
        };
    }

    private static string? ConvertSfxSamplePreview(
        AssetSource source, byte[] data, string stem, int cueIndex, string tempDir)
    {
        var bankBytes = TryResolveSfxBankFromSource(source);
        if (bankBytes is { } bb)
            return SfxExtractor.ExtractSingleToWav(data, stem, cueIndex, bb, tempDir);

        return source.FileSystemPath != null
            ? SfxExtractor.ExtractSingleToWav(source.FileSystemPath, cueIndex, tempDir)
            : null;
    }
}
