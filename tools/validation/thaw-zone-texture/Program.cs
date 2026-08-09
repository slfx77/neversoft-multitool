using System.CommandLine;
using ThawZoneTexAnalyzer.Commands;

namespace ThawZoneTexAnalyzer;

internal static class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("THAW zone TEX debugging tool");
        rootCommand.Subcommands.Add(ArchiveStexDiagnosticsCommand.Create());
        rootCommand.Subcommands.Add(ArchiveImgAlphaDiagnosticsCommand.Create());
        rootCommand.Subcommands.Add(DecodeProvenanceCommand.Create());
        rootCommand.Subcommands.Add(ContentSearchCommand.Create());
        return rootCommand.Parse(args).Invoke();
    }
}
