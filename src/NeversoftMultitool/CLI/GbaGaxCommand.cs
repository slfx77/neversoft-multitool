using System.CommandLine;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio.Gba;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Extracts the PCM sound samples from a GAX Sound Engine GBA ROM to WAV
///     (the Vicarious Visions Tony Hawk GBA line and other GAX titles). The
///     samples are located from the complete self-validating sparse wave tables;
///     see <see cref="GbaGaxAudio" />.
/// </summary>
public static class GbaGaxCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a GBA ROM using the GAX Sound Engine"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the extracted WAV samples"
        };
        var rateOption = new Option<int>("--rate")
        {
            Description = "WAV playback sample rate (GAX rate is engine-global; default is an inspection rate)",
            DefaultValueFactory = _ => GbaGaxAudio.DefaultSampleRate
        };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "List each extracted sample" };

        var command = new Command("gba-audio", "Extract GAX Sound Engine samples from a GBA ROM to WAV");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(rateOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(rateOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input, string? outputDir, int rate, bool verbose, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(input);
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var banner = GbaGaxAudio.GetVersionBanner(rom);
        if (banner == null)
        {
            AnsiConsole.MarkupLine("[red]Not a GAX Sound Engine ROM[/] (engine signature not found)");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(banner)}[/]");
        var waveSets = GbaGaxAudio.FindWaveSets(rom);
        if (waveSets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No GAX wave set found[/] (no structurally valid sample table)");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Found [green]{waveSets.Count}[/] wave sets with "
            + $"[green]{waveSets.Sum(set => set.Samples.Count)}[/] populated samples");

        var dir = outputDir ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gax");
        Directory.CreateDirectory(dir);
        var written = 0;
        foreach (var waveSet in waveSets)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  bank {waveSet.Index:D2} at 0x{waveSet.TableOffset:X}: "
                    + $"{waveSet.Samples.Count}/{waveSet.SlotCount} populated slots, {waveSet.Encoding}");
            }

            foreach (var sample in waveSet.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pcm = GbaGaxAudio.DecodeToPcm16(
                    GbaGaxAudio.GetSampleBytes(rom, sample),
                    waveSet.Encoding);
                var fileName = $"bank_{waveSet.Index:D2}_sample_{sample.Index:D3}.wav";
                var path = Path.Combine(dir, fileName);
                WavWriter.WritePcm16(path, rate, 1, pcm);
                written++;
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"    {fileName}  0x{sample.Address:X8}  {sample.Size} bytes");
                }
            }
        }

        AnsiConsole.MarkupLine($"Extracted [green]{written}[/] WAV samples to [green]{Markup.Escape(dir)}[/]");
        return 0;
    }
}
