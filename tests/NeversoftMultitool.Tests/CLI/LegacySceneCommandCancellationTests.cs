using System.CommandLine;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class LegacySceneCommandCancellationTests
{
    [Theory]
    [InlineData("ps2geom")]
    [InlineData("ps2scene")]
    [InlineData("ps2scene-worldzone")]
    [InlineData("xbxscene")]
    public async Task Command_EmptyDirectoryPreCancelled_PropagatesWithoutOutput(
        string commandName)
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var command = commandName switch
        {
            "ps2geom" => Ps2GeomCommand.Create(),
            "ps2scene" => Ps2SceneCommand.Create(),
            "ps2scene-worldzone" => Ps2SceneCommand.Create(),
            "xbxscene" => XbxSceneCommand.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(commandName))
        };
        string[] arguments = commandName == "ps2scene-worldzone"
            ? new[] { input, "-o", output, "--worldzone" }
            : [input, "-o", output];
        var parseResult = command.Parse(arguments);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            parseResult.InvokeAsync(
                new InvocationConfiguration
                {
                    EnableDefaultExceptionHandler = false
                },
                cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-legacy-scene-cancel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
