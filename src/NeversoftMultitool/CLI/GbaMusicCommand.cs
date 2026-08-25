using System.CommandLine;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio.Gba;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Renders the sequenced music from a GAX Sound Engine GBA ROM to WAV. The note
///     sequence is decoded faithfully (<see cref="GbaGaxMusic" />); tempo and timbre
///     are documented approximations (<see cref="GaxRenderer" />).
/// </summary>
public static class GbaMusicCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a GAX Sound Engine GBA ROM"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the rendered WAV songs"
        };
        var songOption = new Option<int>("--song")
        {
            Description = "Render only this song index (default: all songs)",
            DefaultValueFactory = _ => -1
        };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "List each song" };

        var command = new Command("gba-music", "Render GAX Sound Engine songs from a GBA ROM to WAV");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(songOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(songOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input, string? outputDir, int song, bool verbose,
        CancellationToken cancellationToken = default)
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

        var banner = GbaGaxMusic.GetBanner(rom);
        if (banner == null)
        {
            AnsiConsole.MarkupLine("[red]Not a GAX Sound Engine ROM[/] (engine signature not found)");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(banner)}[/]");
        var headers = GbaGaxMusic.FindSongHeaders(rom);
        if (headers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No GAX songs found[/] (this ROM may use a later GAX header layout)");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{headers.Count}[/] songs");
        var dir = outputDir ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gax-music");
        Directory.CreateDirectory(dir);

        var rendered = 0;
        for (var i = 0; i < headers.Count; i++)
        {
            if (song >= 0 && i != song)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            // The mix rate is call-site in the game: the boot/title song (address
            // order 0) plays at 18158 Hz, everything else at 15769 Hz.
            var options = new GaxRenderer.Options
            {
                RequestedRateHz = i == 0 ? GaxRenderer.TitleRateHz : GaxRenderer.DefaultRateHz
            };
            var pcm = GaxRenderer.RenderSong(rom, headers[i], options, out var sampleRate);
            if (pcm.Length == 0)
                continue;
            var path = Path.Combine(dir, $"song_{i:D2}.wav");
            WavWriter.WritePcm16(path, sampleRate, 1, pcm);
            rendered++;
            if (verbose)
                AnsiConsole.MarkupLine(
                    $"  song_{i:D2}.wav  hdr 0x{headers[i].Address:X8}  {headers[i].ChannelCount} ch  "
                    + $"{headers[i].OrderLength} patterns  {pcm.Length / sampleRate}s @ {sampleRate} Hz");
        }

        AnsiConsole.MarkupLine($"Rendered [green]{rendered}[/] songs to [green]{Markup.Escape(dir)}[/]");
        AnsiConsole.MarkupLine(
            "[grey]Real timbre: instrument PCM waves, envelopes, vibrato and the songs' own tempo, "
            + "via the frame-faithful GAX engine port.[/]");
        return 0;
    }
}
