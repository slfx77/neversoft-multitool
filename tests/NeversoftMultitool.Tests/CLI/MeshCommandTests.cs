using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

namespace NeversoftMultitool.Tests.CLI;

public sealed class MeshCommandTests
{
    [Fact]
    public void Execute_ExplicitMalformedFileFailsButDirectoryRemainsNoWorkSuccess()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var malformed = Path.Combine(input, "[bad].psx");
        var explicitOutput = Path.Combine(temp.Path, "explicit-output");
        var directoryOutput = Path.Combine(temp.Path, "directory-output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(malformed, "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(malformed, explicitOutput));
        Assert.Equal(0, Execute(input, directoryOutput));
        Assert.False(Directory.Exists(explicitOutput));
        Assert.False(Directory.Exists(directoryOutput));
    }

    [Fact]
    public void Execute_MissingBracketedPathFailsWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[missing]");
        var output = Path.Combine(temp.Path, "output");

        Assert.Equal(1, Execute(input, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_PreCancelledRecognizedFilePropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].psx");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, [0x03, 0x00, 0x02, 0x00]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Execute(
            input,
            output,
            cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private static int Execute(
        string input,
        string output,
        CancellationToken? cancellationToken = null)
    {
        return MeshCommand.Execute(
            input,
            output,
            texturePath: null,
            skeletonPath: null,
            ddxPath: null,
            psxPath: null,
            ddmTexturePath: null,
            coordinateScale: 1f,
            worldzoneTimeOfDay: WorldzoneTimeOfDay.All,
            psxLightPreset: null,
            verbose: true,
            format: MeshOutputFormat.Glb,
            blenderHelperPath: null,
            includeN64Animations: false,
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-mesh-command-{Guid.NewGuid():N}");
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
