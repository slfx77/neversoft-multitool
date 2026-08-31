using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool;

internal static class AudioConverterTabOperations
{
    private static readonly string[] SupportedExtensions =
    [
        ".adx", ".xa", ".vab", ".vag", ".kat", ".sfx", ".seq", ".pcm", ".snd", ".pss", ".vid",
        ".swav", ".strm", ".hwas",
        // PSP ATRAC3/ATRAC3plus and the Wii builds' audio-only VID1 movies,
        // which ship named .ogg (content-gated on the VID1 magic downstream).
        ".at3", ".ogg"
    ];

    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public static bool IsSfxCueSheet(string path)
    {
        return Path.GetExtension(path).Equals(".sfx", StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> FindAudioFiles(string inputDir)
    {
        var allFiles = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories);
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
            ".pcm" => "PCM",
            ".swav" => "SWAV",
            ".strm" => "STRM",
            ".snd" => "SND",
            ".pss" => "PSS",
            ".vid" => "VID",
            ".ogg" => "VID",
            ".at3" => "AT3",
            ".kat" => "KAT",
            ".sfx" => "SFX",
            ".seq" => "SEQ",
            "" => "VAG",
            _ => "Unknown"
        };
    }

    public static List<AudioSampleEntry> EnumerateChildren(AudioFileEntry parent)
    {
        return EnumerateChildren(parent, parent.Source.ReadBytes());
    }

    /// <summary>
    ///     Byte-based overload used by the background metadata pass so an XA
    ///     file is read only once for both its parent duration and child rows.
    /// </summary>
    internal static List<AudioSampleEntry> EnumerateChildren(AudioFileEntry parent, byte[] data)
    {
        return parent.AudioFormat switch
        {
            "VAB" => EnumerateVabChildren(parent, data),
            "KAT" => EnumerateKatChildren(parent, data),
            "XA" => EnumerateXaChannels(data, parent),
            _ => []
        };
    }

    private static List<AudioSampleEntry> EnumerateVabChildren(AudioFileEntry parent, byte[] data)
    {
        var cues = EnumerateResolvedCueChildren(parent, data);
        return cues.Count > 0
            ? cues
            : VabExtractor.EnumerateSamples(data)
                .Select(sample => new AudioSampleEntry
                {
                    Parent = parent,
                    SampleIndex = sample.Index,
                    Encoding = "SPU-ADPCM",
                    SampleRate = sample.SampleRate,
                    Channels = 1,
                    DataSize = sample.DataSize,
                    DurationSeconds = AudioDurationProbe.FromDecodedFrames(
                        sample.DecodedFrameCount,
                        sample.SampleRate)
                })
                .ToList();
    }

    private static List<AudioSampleEntry> EnumerateKatChildren(AudioFileEntry parent, byte[] data)
    {
        var cues = EnumerateResolvedCueChildren(parent, data);
        return cues.Count > 0
            ? cues
            : KatExtractor.EnumerateSamples(data)
                .Select(sample => new AudioSampleEntry
                {
                    Parent = parent,
                    SampleIndex = sample.Index,
                    Encoding = sample.Encoding,
                    SampleRate = sample.SampleRate,
                    Channels = sample.Channels,
                    DataSize = sample.DataSize,
                    DurationSeconds = AudioDurationProbe.FromDecodedFrames(
                        sample.DecodedFrameCount,
                        sample.SampleRate)
                })
                .ToList();
    }

    private static List<AudioSampleEntry> EnumerateResolvedCueChildren(AudioFileEntry parent, byte[] bankData)
    {
        if (parent.CueSheets.Count == 0)
            return [];

        var bank = new SfxExtractor.SfxBankBytes(bankData, parent.AudioFormat);
        var decodedFrames = EnumerateDecodedFrames(parent.AudioFormat, bankData);
        var children = new List<AudioSampleEntry>();
        foreach (var sheet in parent.CueSheets)
        {
            try
            {
                var sfxData = sheet.Source.ReadBytes();
                if (!SfxExtractor.TryResolveSamples(sfxData, bank, out var resolution, out _) ||
                    resolution.Kind != SfxResolutionKind.ResolvedCues)
                    continue;

                children.AddRange(resolution.Samples.Select(sample =>
                {
                    decodedFrames.TryGetValue(sample.BankSampleIndex, out var decodedFrameCount);
                    return new AudioSampleEntry
                    {
                        Parent = parent,
                        SampleIndex = sample.BankSampleIndex,
                        CueIndex = sample.CueIndex,
                        CueSheet = sheet,
                        Encoding = $"{sample.Encoding} via {sheet.FileName}",
                        SampleRate = sample.SampleRate,
                        Channels = sample.Channels,
                        DataSize = sample.DataSize,
                        DurationSeconds = AudioDurationProbe.FromDecodedFrames(
                            decodedFrameCount,
                            sample.SampleRate)
                    };
                }));
            }
            catch
            {
                // One unreadable sheet must not suppress other resolved sheets
                // or the raw bank fallback below.
            }
        }

        return children;
    }

    private static IReadOnlyDictionary<int, long?> EnumerateDecodedFrames(string bankFormat, byte[] bankData)
    {
        return bankFormat switch
        {
            "VAB" => VabExtractor.EnumerateSamples(bankData)
                .ToDictionary(static sample => sample.Index, static sample => sample.DecodedFrameCount),
            "KAT" => KatExtractor.EnumerateSamples(bankData)
                .ToDictionary(static sample => sample.Index, static sample => sample.DecodedFrameCount),
            _ => new Dictionary<int, long?>()
        };
    }

    /// <summary>
    ///     One child row per interleaved XA channel. Single-channel (and raw,
    ///     non-sectored) XA yields no children so the file stays a flat row.
    /// </summary>
    private static List<AudioSampleEntry> EnumerateXaChannels(byte[] data, AudioFileEntry parent)
    {
        var channels = XaExtractor.EnumerateChannels(data);
        if (channels.Count <= 1)
            return [];

        return channels.Select(channel => new AudioSampleEntry
        {
            Parent = parent,
            SampleIndex = channel.ChannelNumber,
            Encoding = $"Channel {channel.ChannelNumber:D2}",
            SampleRate = channel.SampleRate,
            Channels = channel.IsStereo ? 2 : 1,
            DataSize = channel.DataSize,
            DurationSeconds = channel.DurationSeconds
        }).ToList();
    }

    public static AudioConvertResult ConvertFile(
        AudioFileEntry entry,
        string outputDir,
        int vabSampleRate,
        string outputStem)
    {
        var source = entry.Source;
        var data = source.ReadBytes();

        return entry.AudioFormat switch
        {
            "ADX" => AdxDecoder.ConvertToWav(data, outputStem, outputDir),
            "XA" => XaDecoder.ConvertToWav(data, outputStem, outputDir),
            "VAB" => ConvertVabBank(entry, data, outputStem, outputDir, vabSampleRate),
            "VAG" => VagDecoder.ConvertToWav(data, outputStem, outputDir),
            "PCM" => XboxPcmDecoder.ConvertToWav(data, outputStem, outputDir),
            "SND" => Thug2PcSndDecoder.ConvertToWav(data, outputStem, outputDir),
            "PSS" => PssAudioExtractor.ConvertToWav(data, outputStem, outputDir),
            "VID" => Vid1AudioExtractor.ConvertToWav(data, outputStem, outputDir),
            "KAT" => ConvertKatBank(entry, data, outputStem, outputDir),
            "SEQ" => ConvertSeqSong(entry, data, outputStem, outputDir),
            _ => new AudioConvertResult { ErrorMessage = "Unknown format" }
        };
    }

    private static AudioConvertResult ConvertSeqSong(
        AudioFileEntry entry,
        byte[] data,
        string outputStem,
        string outputDir)
    {
        // The song is MIDI-like; its instruments live in the same-stem VAB
        // sibling (Apocalypse ships every .seq with one). The companion API
        // resolves it for loose files and archive entries alike.
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var vabData = entry.Source.TryReadCompanion(stem + ".vab");
        if (vabData == null)
        {
            return new AudioConvertResult
            {
                ErrorMessage = "No same-stem .vab bank beside the SEQ"
            };
        }

        return SeqExtractor.ConvertToWav(data, vabData, outputStem, outputDir);
    }

    private static AudioConvertResult ConvertVabBank(
        AudioFileEntry entry,
        byte[] data,
        string outputStem,
        string outputDir,
        int sampleRate)
    {
        return TryConvertOwnedCueSheets(entry, data, outputStem, outputDir, out var result)
            ? result
            : VabExtractor.ExtractToWav(data, outputStem, outputDir, sampleRate);
    }

    private static AudioConvertResult ConvertKatBank(
        AudioFileEntry entry,
        byte[] data,
        string outputStem,
        string outputDir)
    {
        return TryConvertOwnedCueSheets(entry, data, outputStem, outputDir, out var result)
            ? result
            : KatExtractor.ExtractToWav(data, outputStem, outputDir);
    }

    private static bool TryConvertOwnedCueSheets(
        AudioFileEntry entry,
        byte[] bankData,
        string outputStem,
        string outputDir,
        out AudioConvertResult result)
    {
        var sheets = new List<SfxCueSheetBatchInput>(entry.CueSheets.Count);
        foreach (var binding in entry.CueSheets)
        {
            try
            {
                sheets.Add(new SfxCueSheetBatchInput(
                    binding.FileName,
                    binding.RelativePath,
                    binding.Source.ReadBytes()));
            }
            catch
            {
                // An unreadable sheet is not a reason to hide other resolved
                // cues, and cannot suppress the raw-bank fallback when none do.
            }
        }

        if (SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                outputStem,
                new SfxExtractor.SfxBankBytes(bankData, entry.AudioFormat),
                outputDir,
                out var cueResult))
        {
            result = cueResult;
            return true;
        }

        result = new AudioConvertResult();
        return false;
    }

    public static string? ConvertForPreview(
        IListEntry item,
        string tempDir,
        int vabSampleRate)
    {
        // Unique per-conversion directory: the decoders derive output names
        // from the file stem, so rapid selection switches would otherwise race
        // on the same path (the old decode keeps writing — decoders don't
        // observe cancellation — and the MediaPlayer holds a lock on the WAV
        // it is playing). See "sound fails to preview while another plays".
        var conversionDir = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(conversionDir);

        if (item is AudioFileEntry parent)
            return ConvertFilePreview(parent, conversionDir);

        if (item is AudioSampleEntry sample)
            return ConvertSamplePreview(sample, conversionDir, vabSampleRate);

        return null;
    }

    public static string FormatTime(TimeSpan ts)
    {
        return TimeDisplay.Format(ts);
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
            "PCM" => XboxPcmDecoder.ConvertToWav(data, stem, tempDir),
            "SND" => Thug2PcSndDecoder.ConvertToWav(data, stem, tempDir),
            "PSS" => PssAudioExtractor.ConvertToWav(data, stem, tempDir),
            "VID" => Vid1AudioExtractor.ConvertToWav(data, stem, tempDir),
            "SEQ" => ConvertSeqSong(parent, data, stem, tempDir),
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
        int vabSampleRate)
    {
        var parentEntry = sample.Parent;

        var data = parentEntry.Source.ReadBytes();
        var stem = Path.GetFileNameWithoutExtension(parentEntry.FileName);

        if (sample.CueSheet is { } sheet && sample.CueIndex is { } cueIndex)
            return ConvertCueSamplePreview(parentEntry, sheet, data, stem, cueIndex, tempDir);

        return parentEntry.AudioFormat switch
        {
            "VAB" => VabExtractor.ExtractSingleToWav(data, stem, sample.SampleIndex, tempDir, vabSampleRate),
            "KAT" => KatExtractor.ExtractSingleToWav(data, stem, sample.SampleIndex, tempDir),
            "XA" => XaExtractor.ExtractChannelToWav(data, stem, sample.SampleIndex, tempDir),
            _ => null
        };
    }

    private static string? ConvertCueSamplePreview(
        AudioFileEntry parent,
        AudioCueSheetBinding sheet,
        byte[] bankData,
        string stem,
        int cueIndex,
        string tempDir)
    {
        if (parent.AudioFormat is not ("VAB" or "KAT"))
            return null;

        try
        {
            return SfxExtractor.ExtractSingleToWav(
                sheet.Source.ReadBytes(),
                stem,
                cueIndex,
                new SfxExtractor.SfxBankBytes(bankData, parent.AudioFormat),
                tempDir);
        }
        catch
        {
            return null;
        }
    }
}
