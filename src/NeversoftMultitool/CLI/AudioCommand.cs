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
        ".adx", ".xa", ".vab", ".kat", ".sfx", ".seq", ".vag", ".pcm", ".snd", ".pss", ".vid",
        ".swav", ".strm", ".hwas",
        // PSP ATRAC3/ATRAC3plus, and the Wii builds' VID1 audio-only movies,
        // which ship misnamed .ogg (their PAYLOAD really is Ogg Vorbis, so the
        // name is honest one level down). Both routed 2026-08-26.
        ".at3", ".ogg"
    ];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description =
                "Path to directory containing audio files (.adx, .xa, .vab, .vag, .kat, .sfx, .pcm, .snd, .pss, .vid, .at3, .ogg, .swav, .strm, .hwas)"
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

        var command = new Command("audio", "Convert ADX/XA/VAB/VAG/KAT/SFX/PCM/SND/PSS/VID audio files to WAV");
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
        var audioFiles = allFiles
            .Where(f => SupportedExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        // Probe extensionless files for SPU-ADPCM audio
        var extensionlessFiles = allFiles
            .Where(f => string.IsNullOrEmpty(Path.GetExtension(f)))
            .Where(f => VagDecoder.Probe(f) != null)
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
                $"({extensionlessFiles.Count} detected as headerless SPU-ADPCM)");
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
            var ext = Path.GetExtension(file).ToLowerInvariant();
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
                ".pss" => PssAudioExtractor.ConvertToWav(file, outputDirectory),
                // .ogg here is a VID1 audio-only movie, not Ogg Vorbis at the
                // container level; ConvertVid1Audio gates on the magic so a real
                // Vorbis file is skipped rather than failing the run.
                ".vid" or ".ogg" => ConvertVid1Audio(file, outputDirectory),
                ".at3" => At3Decoder.ConvertToWav(file, outputDirectory),
                ".kat" => KatExtractor.ExtractToWav(file, outputDirectory),
                ".sfx" => SfxExtractor.ExtractToWav(file, outputDirectory),
                ".seq" => SeqExtractor.ConvertToWav(file, outputDirectory),
                "" => VagDecoder.ConvertToWav(file, outputDirectory, sampleRate),
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
                ".pss" => PssAudioExtractor.ConvertToWav(data, outputStem, outputDirectory),
                ".vid" or ".ogg" => ConvertVid1Audio(data, outputStem, outputDirectory),
                ".at3" => At3Decoder.ConvertToWav(data, outputStem, outputDirectory),
                ".kat" => KatExtractor.ExtractToWav(data, outputStem, outputDirectory),
                "" => VagDecoder.ConvertToWav(data, outputStem, outputDirectory, sampleRate),
                _ => new AudioConvertResult { ErrorMessage = "Unsupported format" }
            };
        }
        catch (Exception ex)
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
