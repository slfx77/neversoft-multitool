using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool;

internal static class AudioConverterTabOperations
{
    private static readonly string[] SupportedExtensions = [".adx", ".xa", ".vab", ".vag", ".kat", ".pss"];

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
            ".vag" or ".pss" => "VAG",
            ".kat" => "KAT",
            "" => "VAG",
            _ => "Unknown"
        };
    }

    public static List<AudioSampleEntry> EnumerateChildren(string inputFile, string parentFileName, string audioFormat)
    {
        return audioFormat switch
        {
            "VAB" => VabExtractor.EnumerateSamples(inputFile)
                .Select(sample => new AudioSampleEntry
                {
                    ParentFileName = parentFileName,
                    SampleIndex = sample.Index,
                    Encoding = "SPU-ADPCM",
                    SampleRate = 0,
                    Channels = 1,
                    DataSize = sample.DataSize
                })
                .ToList(),
            "KAT" => KatExtractor.EnumerateSamples(inputFile)
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
            _ => []
        };
    }

    public static AudioConvertResult ConvertFile(
        string inputFile,
        string outputDir,
        string audioFormat,
        int vabSampleRate)
    {
        return audioFormat switch
        {
            "ADX" => AdxDecoder.ConvertToWav(inputFile, outputDir),
            "XA" => XaDecoder.ConvertToWav(inputFile, outputDir),
            "VAB" => VabExtractor.ExtractToWav(inputFile, outputDir, vabSampleRate),
            "VAG" => VagDecoder.ConvertToWav(inputFile, outputDir),
            "KAT" => KatExtractor.ExtractToWav(inputFile, outputDir),
            _ => new AudioConvertResult { ErrorMessage = "Unknown format" }
        };
    }

    public static string? ConvertForPreview(
        IListEntry item,
        string inputDir,
        string tempDir,
        IReadOnlyList<AudioFileEntry> parentFiles,
        int vabSampleRate)
    {
        Directory.CreateDirectory(tempDir);

        if (item is AudioFileEntry parent)
            return ConvertFilePreview(parent, inputDir, tempDir);

        if (item is AudioSampleEntry sample)
            return ConvertSamplePreview(sample, inputDir, tempDir, parentFiles, vabSampleRate);

        return null;
    }

    public static string FormatTime(TimeSpan ts)
    {
        return ts.TotalMinutes >= 60
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static string? ConvertFilePreview(AudioFileEntry parent, string inputDir, string tempDir)
    {
        var inputFile = Path.Combine(inputDir, parent.FileName);
        var result = parent.AudioFormat switch
        {
            "ADX" => AdxDecoder.ConvertToWav(inputFile, tempDir),
            "XA" => XaDecoder.ConvertToWav(inputFile, tempDir),
            "VAG" => VagDecoder.ConvertToWav(inputFile, tempDir),
            _ => null
        };

        if (result is not { Success: true })
            return null;

        var stem = Path.GetFileNameWithoutExtension(parent.FileName);
        var wavPath = Path.Combine(tempDir, stem + ".wav");
        if (File.Exists(wavPath))
            return wavPath;

        var channelPath = Path.Combine(tempDir, stem, "ch00.wav");
        return File.Exists(channelPath) ? channelPath : null;
    }

    private static string? ConvertSamplePreview(
        AudioSampleEntry sample,
        string inputDir,
        string tempDir,
        IReadOnlyList<AudioFileEntry> parentFiles,
        int vabSampleRate)
    {
        var inputFile = Path.Combine(inputDir, sample.ParentFileName);
        var parentEntry = parentFiles.FirstOrDefault(parent => parent.FileName == sample.ParentFileName);
        if (parentEntry == null)
            return null;

        return parentEntry.AudioFormat switch
        {
            "VAB" => VabExtractor.ExtractSingleToWav(inputFile, sample.SampleIndex, tempDir, vabSampleRate),
            "KAT" => KatExtractor.ExtractSingleToWav(inputFile, sample.SampleIndex, tempDir),
            _ => null
        };
    }
}
