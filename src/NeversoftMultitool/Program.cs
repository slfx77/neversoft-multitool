using System.CommandLine;
using System.Text;
using NeversoftMultitool.CLI;
using Spectre.Console;

namespace NeversoftMultitool;

public static class Program
{
    private static readonly Func<Command>[] CliCommandFactories =
    [
        PsxCommand.Create,
        RleCommand.Create,
        ArchiveCommand.Create,
        CasCommand.Create,
        WgtCommand.Create,
        PvrCommand.Create,
        NgcTexCommand.Create,
        N64TexCommand.Create,
        N64AudioInspectCommand.Create,
        N64AudioFxInspectCommand.Create,
        N64AudioRuntimeInspectCommand.Create,
        N64SfxInspectCommand.Create,
        N64AudioDecodeCommand.Create,
        DdmCommand.Create,
        AudioCommand.Create,
        QbKeyCommand.Create,
        TrgCommand.Create,
        SfdCommand.Create,
        VidCommand.Create,
        StrCommand.Create,
        MeshCommand.Create,
        PsxMeshCommand.Create,
        PsxMeshDumpCommand.Create,
        PsxAnimDumpCommand.Create,
        PsxAnimExportCommand.Create,
        PsxAnimTraceCommand.Create,
        PsxAnimSurveyCommand.Create,
        Ps2TexCommand.Create,
        RwDffCommand.Create,
        RwBspCommand.Create,
        ColCommand.Create,
        NgcColCommand.Create,
        XbxTexCommand.Create,
        UnpackCommand.Create,
        QbCommand.Create,
        GlbRenderCommand.Create,
        GlbGifCommand.Create,
        GsDumpCommand.Create,
        SkaCommand.Create,
        Thps2XFrontendAnimCommand.Create,
        FntCommand.Create,
        GbaGaxCommand.Create,
        GbaImageCommand.Create,
        GbaMusicCommand.Create
    ];

    private static readonly Lazy<HashSet<string>> CliCommandNames = new(
        static () => CliCommandFactories
            .Select(static factory => factory().Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

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

        return rootCommand.Parse(args).Invoke();
    }

    internal static RootCommand BuildRootCommand()
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

        foreach (var commandFactory in CliCommandFactories)
        {
            rootCommand.Subcommands.Add(commandFactory());
        }

        rootCommand.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            PrintUsage(rootCommand);
            return Task.FromResult(0);
        });

        return rootCommand;
    }

    private static void PrintUsage(RootCommand rootCommand)
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  NeversoftMultitool [green]<command>[/] [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        var commandNameWidth = rootCommand.Subcommands.Max(static command => command.Name.Length);
        foreach (var command in rootCommand.Subcommands)
        {
            var name = Markup.Escape(command.Name.PadRight(commandNameWidth));
            var description = Markup.Escape(command.Description ?? string.Empty);
            AnsiConsole.MarkupLine($"  [green]{name}[/]  {description}");
        }
#if WINDOWS_GUI
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]For GUI mode:[/]");
        AnsiConsole.MarkupLine("  NeversoftMultitool");
#endif
    }

    internal static bool IsCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && args.Any(argument =>
            argument.Equals("--no-gui", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("-n", StringComparison.OrdinalIgnoreCase) ||
            CliCommandNames.Value.Contains(argument));
    }
}
