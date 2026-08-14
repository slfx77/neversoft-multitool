using System.CommandLine;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class Ps2GeomCommandEntryCancellationTests
{
    [Fact]
    public async Task Command_PreCancelledTakesPrecedenceOverInvalidFormat()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var parseResult = Ps2GeomCommand.Create()
            .Parse([input, "-o", output, "--format", "invalid"]);

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
                System.IO.Path.GetTempPath(), $"nmt-ps2geom-entry-cancel-{Guid.NewGuid():N}");
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
