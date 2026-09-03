using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Vid1;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class AudioCommand
{
    private static readonly string[] SupportedExtensions =
    [
        ".adx", ".xa", ".vab", ".kat", ".sfx", ".seq", ".vag", ".pcm", ".snd", ".dee", ".smo", ".pss", ".pmf", ".vid", ".fsb",
        ..HandheldAudioFormatSupport.Extensions,
        ..StandardAudioFormatSupport.Extensions,
        // PSP ATRAC3/ATRAC3plus, and the Wii builds' VID1 audio-only movies,
        // which ship misnamed .ogg (their PAYLOAD really is Ogg Vorbis, so the
        // name is honest one level down). Both routed 2026-08-26.
        ".ogg"
    ];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description =
                "Path to directory containing supported audio files, including THAW XMA DAT/WAD pairs, FSB3 banks, THPS4 PC DEE/SMO, PSP PMF soundtracks, late .wav.ps3/.wav.xen, and extensionless Wii DSP-ADPCM (.adx, .xa, .vab, .vag, .kat, .sfx, .pcm, .snd, .dee, .smo, .pss, .pmf, .vid, .fsb, .fsb.ps3, .fsb.xen, .wav.ps3, .wav.xen, .at3, .ogg, .swav, .strm, .hwas, .wav, .wma)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted WAV files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var sampleRateOption = new Option<int>("-r", "--sample-rate")
        {
            Description = "Sample rate override for VAB (default: 11025) / VAG (default: 22050)",
            DefaultValueFactory = _ => 0
        };

        var command = new Command(
            "audio",
            "Convert Neversoft, THPS4 PC DEE/SMO, PSP PMF, late PS3/X360, THAW XMA/FSB3, handheld (including Wii DSP), WAV, and WMA audio files to WAV");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(sampleRateOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(verboseOption),
                parseResult.GetValue(sampleRateOption),
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Directory not found: {Markup.Escape(input)}");
            return 1;
        }

        var allFiles = Directory.GetFiles(input);
        var audioFiles = SelectNamedCandidatePaths(allFiles).ToList();

        // Probe extensionless files for Nintendo DSP-ADPCM first, then the
        // older raw SPU-ADPCM family. The Wii format has a complete header and
        // exact payload identity, so it must win before the permissive raw gate.
        var extensionlessFiles = allFiles
            .Where(f => string.IsNullOrEmpty(Path.GetExtension(f)))
            .Where(IsSupportedHeaderlessAudio)
            .ToList();
        audioFiles.AddRange(extensionlessFiles);

        if (audioFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported audio files found in the specified directory.[/]");
            return 0;
        }

        var outputStems = AudioOutputStemPlanner.Plan(audioFiles
            .Select(file => new AudioOutputStemInput(
                Path.GetFileName(file),
                Path.GetRelativePath(input, file)))
            .ToArray());

        Directory.CreateDirectory(output);
        if (extensionlessFiles.Count > 0)
            AnsiConsole.MarkupLine(
                $"Found [green]{audioFiles.Count}[/] audio file(s) " +
                $"({extensionlessFiles.Count} detected as headerless audio)");
        else
            AnsiConsole.MarkupLine($"Found [green]{audioFiles.Count}[/] audio file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalConverted = 0;
        var totalSamples = 0;
        var failed = 0;
        var skipped = 0;

        for (var fileIndex = 0; fileIndex < audioFiles.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = audioFiles[fileIndex];
            var filename = Path.GetFileName(file);
            var ext = LatePlatformAudio.HasSupportedFileName(file)
                ? ".lateaudio"
                : ThawXmaBank.HasSupportedFileName(file)
                ? ".xmawad"
                : Fsb3AudioBank.HasSupportedFileName(file)
                    ? ".fsb"
                    : Path.GetExtension(file).ToLowerInvariant();
            var result = ConvertFile(
                file,
                ext,
                outputStems[fileIndex],
                output,
                sampleRate);

            if (result.Success)
            {
                totalConverted++;
                totalSamples += result.SamplesWritten;
            }
            else if (result.Skipped)
            {
                // Structurally not the format its extension claims — counted, but not an error.
                skipped++;
            }
            else
            {
                failed++;
            }

            if (verbose)
                ReportFile(filename, result);
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{totalConverted}[/]/{audioFiles.Count} files " +
            $"({totalSamples} WAV files, {failed} failed) in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (skipped == 0 ? string.Empty : $" ([yellow]{skipped} not this format[/])"));

        return failed == 0 ? 0 : 1;
    }

    private static void ReportFile(string filename, AudioConvertResult result)
    {
        var detail = result switch
        {
            { Success: true, SamplesWritten: > 1 } => $"[green]{result.SamplesWritten} samples[/]",
            { Success: true } => "[green]OK[/]",
            { Skipped: true } =>
                $"[yellow]{Markup.Escape(result.ErrorMessage ?? "Not this format")}[/]",
            _ => $"[red]{Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]"
        };

        AnsiConsole.MarkupLine($"  {Markup.Escape(filename)}: {detail}");
    }

    private static AudioConvertResult ConvertFile(
        string file,
        string extension,
        string outputStem,
        string outputDirectory,
        int sampleRate)
    {
        if (extension == ".xmawad")
            return ThawXmaBank.ConvertToWav(file, outputStem, outputDirectory);

        if (extension == ".lateaudio")
            return LatePlatformAudio.ConvertToWav(file, outputStem, outputDirectory);

        if (extension == ".fsb")
            return Fsb3AudioBank.ConvertToWav(file, outputStem, outputDirectory);

        if (extension == ".dee")
            return Thps4PcDeeAudio.ConvertToWav(file, outputStem, outputDirectory);

        if (extension == ".smo")
            return Thps4PcSmoAudio.ConvertToWav(file, outputStem, outputDirectory);

        if (extension == ".pmf")
            return PsmfAudioExtractor.ConvertToWav(file, outputStem, outputDirectory);

        var standardFormat = StandardAudioFormatSupport.DetectFormat(extension);
        if (standardFormat != null)
        {
            try
            {
                return StandardAudioFormatSupport.ConvertToWav(
                           standardFormat,
                           File.ReadAllBytes(file),
                           outputStem,
                           outputDirectory)
                       ?? new AudioConvertResult { ErrorMessage = "Unsupported standard audio format" };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AudioConvertResult { ErrorMessage = ex.Message };
            }
        }

        var legacyStem = Path.GetFileNameWithoutExtension(file);
        if (outputStem.Equals(legacyStem, StringComparison.Ordinal))
        {
            return extension switch
            {
                ".adx" => AdxDecoder.ConvertToWav(file, outputDirectory),
                ".xa" => XaDecoder.ConvertToWav(file, outputDirectory),
                ".vab" => VabExtractor.ExtractToWav(
                    file,
                    outputDirectory,
                    sampleRate > 0 ? sampleRate : VabExtractor.DefaultSampleRate),
                ".vag" => VagDecoder.ConvertToWav(file, outputDirectory, sampleRate),
                ".pcm" => XboxPcmDecoder.ConvertToWav(file, outputDirectory),
                // Nintendo Nitro waves: SWAV effects out of the DS GOB container,
                // STRM music out of a cart's SDAT sound archive.
                ".swav" or ".strm" or ".hwas" => NdsAudioDecoder.ConvertToWav(file, outputDirectory),
                ".snd" => Thug2PcSndDecoder.ConvertToWav(file, outputDirectory),
                ".dee" => Thps4PcDeeAudio.ConvertToWav(file, outputDirectory),
                ".pss" => PssAudioExtractor.ConvertToWav(file, outputDirectory),
                // .ogg here is a VID1 audio-only movie, not Ogg Vorbis at the
                // container level; ConvertVid1Audio gates on the magic so a real
                // Vorbis file is skipped rather than failing the run.
                ".vid" or ".ogg" => ConvertVid1Audio(file, outputDirectory),
                ".at3" => At3Decoder.ConvertToWav(file, outputDirectory),
                ".kat" => KatExtractor.ExtractToWav(file, outputDirectory),
                ".sfx" => SfxExtractor.ExtractToWav(file, outputDirectory),
                ".seq" => SeqExtractor.ConvertToWav(file, outputDirectory),
                "" => ConvertHeaderlessAudio(file, outputStem, outputDirectory, sampleRate),
                _ => new AudioConvertResult { ErrorMessage = "Unsupported format" }
            };
        }

        if (extension == ".sfx")
            return SfxExtractor.ExtractToWav(file, outputStem, outputDirectory);


        // SEQ needs its same-stem sibling VAB, so it converts by path even when
        // the output stem is disambiguated.
        if (extension == ".seq")
            return SeqExtractor.ConvertToWav(file, outputDirectory, outputStem);

        try
        {
            var data = File.ReadAllBytes(file);
            return extension switch
            {
                ".adx" => AdxDecoder.ConvertToWav(data, outputStem, outputDirectory),
                ".xa" => XaDecoder.ConvertToWav(data, outputStem, outputDirectory),
                ".vab" => VabExtractor.ExtractToWav(
                    data,
                    outputStem,
                    outputDirectory,
                    sampleRate > 0 ? sampleRate : VabExtractor.DefaultSampleRate),
                ".vag" => VagDecoder.ConvertToWav(data, outputStem, outputDirectory, sampleRate),
                ".pcm" => XboxPcmDecoder.ConvertToWav(data, outputStem, outputDirectory),
                ".swav" or ".strm" or ".hwas" => NdsAudioDecoder.ConvertToWav(data, outputStem, outputDirectory),
                ".snd" => Thug2PcSndDecoder.ConvertToWav(data, outputStem, outputDirectory),
                ".dee" => Thps4PcDeeAudio.ConvertToWav(data, outputStem, outputDirectory),
                ".pss" => PssAudioExtractor.ConvertToWav(data, outputStem, outputDirectory),
                ".vid" or ".ogg" => ConvertVid1Audio(data, outputStem, outputDirectory),
                ".at3" => At3Decoder.ConvertToWav(data, outputStem, outputDirectory),
                ".kat" => KatExtractor.ExtractToWav(data, outputStem, outputDirectory),
                "" => WiiDspAudio.IsWiiDsp(data)
                    ? WiiDspAudio.ConvertToWav(data, outputStem, outputDirectory)
                    : VagDecoder.ConvertToWav(data, outputStem, outputDirectory, sampleRate),
                _ => new AudioConvertResult { ErrorMessage = "Unsupported format" }
            };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    internal static string[] SelectNamedCandidatePaths(IEnumerable<string> paths)
    {
        return paths
            .Where(static path =>
            {
                if (LatePlatformAudio.HasSupportedFileName(path))
                    return LatePlatformAudio.Probe(path) != null;

                if (ThawXmaBank.HasSupportedFileName(path)
                    || Fsb3AudioBank.HasSupportedFileName(path))
                {
                    return true;
                }

                var extension = Path.GetExtension(path).ToLowerInvariant();
                return extension switch
                {
                    ".dee" => Thps4PcDeeAudio.Probe(path) != null,
                    ".smo" => Thps4PcSmoAudio.Probe(path) != null,
                    ".pmf" => PsmfAudioExtractor.Probe(path)?.HasAudio == true,
                    _ => SupportedExtensions.Contains(extension)
                };
            })
            .ToArray();
    }

    private static bool IsSupportedHeaderlessAudio(string filePath)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            return WiiDspAudio.IsWiiDsp(data)
                   || (data.Length > 0 && data.Length % 16 == 0 && VagDecoder.Probe(data) != null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AudioConvertResult ConvertHeaderlessAudio(
        string filePath,
        string outputStem,
        string outputDirectory,
        int sampleRate)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            return WiiDspAudio.IsWiiDsp(data)
                ? WiiDspAudio.ConvertToWav(data, outputStem, outputDirectory)
                : VagDecoder.ConvertToWav(data, outputStem, outputDirectory, sampleRate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    ///     Converts a VID1 movie's audio. The Wii builds name their audio-only
    ///     VID1 movies <c>.ogg</c>, so that extension routes here — but gated on
    ///     the VID1 magic, because a genuine Ogg Vorbis file fed to the VID1
    ///     reader fails hard ("VID1 chunk extends beyond the file") and would
    ///     turn a harmless no-op into a red error for anyone pointing the tool
    ///     at ordinary Vorbis audio. There is no real Ogg Vorbis anywhere in the
    ///     57-build corpus, so the gate only ever protects outside content.
    /// </summary>
    private static AudioConvertResult ConvertVid1Audio(string file, string outputDirectory)
    {
        if (!IsVid1(file))
            return NotVid1();

        return Vid1AudioExtractor.ConvertToWav(file, outputDirectory);
    }

    private static AudioConvertResult ConvertVid1Audio(byte[] data, string outputStem, string outputDirectory)
    {
        if (data.Length < 4 || data[0] != 'V' || data[1] != 'I' || data[2] != 'D' || data[3] != '1')
            return NotVid1();

        return Vid1AudioExtractor.ConvertToWav(data, outputStem, outputDirectory);
    }

    private static bool IsVid1(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4
                   && magic[0] == 'V' && magic[1] == 'I' && magic[2] == 'D' && magic[3] == '1';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static AudioConvertResult NotVid1()
    {
        return new AudioConvertResult
        {
            Skipped = true,
            ErrorMessage = "Not a VID1 movie"
        };
    }
}
