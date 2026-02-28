using System.CommandLine;

namespace NeversoftMultitool.CLI;

public static class QbKeyCommand
{
    public static Command Create()
    {
        var command = new Command("qbkey", "QBKey hash utilities");
        command.Subcommands.Add(QbKeyCrossRefCommand.CreateCommand());
        command.Subcommands.Add(QbKeyImportCommand.CreateCommand());
        return command;
    }
}
