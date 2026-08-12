using System.CommandLine;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class VideoCommandCancellationTests
{
    [Theory]
    [InlineData("sfd")]
    [InlineData("vid")]
    [InlineData("str")]
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
            "sfd" => SfdCommand.Create(),
            "vid" => VidCommand.Create(),
            "str" => StrCommand.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(commandName))
        };
        var parseResult = command.Parse([input, "-o", output]);

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
                System.IO.Path.GetTempPath(), $"nmt-video-command-cancel-{Guid.NewGuid():N}");
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
