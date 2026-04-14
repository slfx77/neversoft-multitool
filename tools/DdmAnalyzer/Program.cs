using System.CommandLine;
using DdmAnalyzer.Commands;

namespace DdmAnalyzer;

internal class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("DDM Analyzer - DDM/PSX level placement analysis tool");

        rootCommand.Subcommands.Add(PlacementCommand.Create());
        rootCommand.Subcommands.Add(DumpCommand.Create());
        rootCommand.Subcommands.Add(DdmInfoCommand.Create());
        rootCommand.Subcommands.Add(VerifyCommand.Create());

        return rootCommand.Parse(args).Invoke();
    }
}
