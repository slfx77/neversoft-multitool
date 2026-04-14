using System.CommandLine;
using PsxAnalyzer.Commands;

namespace PsxAnalyzer;

internal class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("PSX Analyzer - Neversoft PSX file structure debugging tool");

        rootCommand.Subcommands.Add(DumpCommand.Create());
        rootCommand.Subcommands.Add(CompareCommand.Create());
        rootCommand.Subcommands.Add(HexCommand.Create());

        return rootCommand.Parse(args).Invoke();
    }
}
