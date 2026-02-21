using System.CommandLine;
using System.Text;
using NeversoftMultitool.CLI;
using Spectre.Console;

namespace NeversoftMultitool;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        if (!IsCliMode(args))
        {
            return GuiEntryPoint.Run(args);
        }
#endif

        return RunCli(args);
    }

    private static int RunCli(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        AnsiConsole.Write(
            new FigletText("Neversoft Multitool")
                .Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]Neversoft Game Data Extraction Utilities - CLI Mode[/]");
        AnsiConsole.WriteLine();

        var rootCommand = BuildRootCommand();

        rootCommand.Subcommands.Add(PsxCommand.Create());
        rootCommand.Subcommands.Add(RleCommand.Create());
        rootCommand.Subcommands.Add(ArchiveCommand.Create());
        rootCommand.Subcommands.Add(PvrCommand.Create());
        rootCommand.Subcommands.Add(DdmCommand.Create());
        rootCommand.Subcommands.Add(AudioCommand.Create());
        rootCommand.Subcommands.Add(QbKeyCommand.Create());
        rootCommand.Subcommands.Add(TrgCommand.Create());
        rootCommand.Subcommands.Add(SfdCommand.Create());
        rootCommand.Subcommands.Add(StrCommand.Create());
        rootCommand.Subcommands.Add(PsxMeshCommand.Create());

        return rootCommand.Parse(args).Invoke();
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("Neversoft Game Data Extraction Utilities");

        var noGuiOption = new Option<bool>("-n", "--no-gui")
        {
            Description = "Run in command-line mode without GUI (Windows only)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        rootCommand.Options.Add(noGuiOption);
        rootCommand.Options.Add(verboseOption);

        rootCommand.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            PrintUsage();
            return Task.FromResult(0);
        });

        return rootCommand;
    }

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  NeversoftMultitool [green]<command>[/] [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        AnsiConsole.MarkupLine("  [green]psx[/]       Extract textures from PSX model files");
        AnsiConsole.MarkupLine("  [green]rle[/]       Convert RLE/BMR bitmap files to PNG");
        AnsiConsole.MarkupLine("  [green]archive[/]   Extract files from WAD/PKR/PRE/DDX/BON archives");
        AnsiConsole.MarkupLine("  [green]pvr[/]       Convert Dreamcast PVR texture files to PNG");
        AnsiConsole.MarkupLine("  [green]ddm[/]       Convert DDM mesh files to glTF (.glb)");
        AnsiConsole.MarkupLine("  [green]audio[/]     Convert ADX/XA/VAB/KAT audio files to WAV");
        AnsiConsole.MarkupLine("  [green]qbkey[/]     QBKey hash utilities (cross-reference, lookup)");
        AnsiConsole.MarkupLine("  [green]trg[/]       Parse TRG level trigger/script files to JSON");
        AnsiConsole.MarkupLine("  [green]sfd[/]       Convert SFD (Sofdec) video files to MP4");
        AnsiConsole.MarkupLine("  [green]str[/]       Convert PS1 STR (MDEC) video files to MP4");
        AnsiConsole.MarkupLine("  [green]psx-mesh[/]  Convert PSX model files to glTF (.glb)");
#if WINDOWS_GUI
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]For GUI mode:[/]");
        AnsiConsole.MarkupLine("  NeversoftMultitool");
#endif
    }

#if WINDOWS_GUI
    private static bool IsCliMode(string[] args)
    {
        return args.Length > 0 && (
            args.Any(a => a.Equals("--no-gui", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("-n", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("psx", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("rle", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("archive", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("pvr", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("ddm", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("audio", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("qbkey", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("trg", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("sfd", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("str", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("psx-mesh", StringComparison.OrdinalIgnoreCase)));
    }
#endif
}
