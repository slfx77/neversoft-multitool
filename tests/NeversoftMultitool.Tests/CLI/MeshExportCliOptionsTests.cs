using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.CLI;

public sealed class MeshExportCliOptionsTests
{
    [Fact]
    public void ExportFiles_PreCancelled_PropagatesWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].psx");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        Assert.Throws<OperationCanceledException>(() => Export(
            input,
            output,
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ExportFiles_InvalidFileStillReturnsAggregateFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[bad].psx");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        Assert.Equal(1, Export(
            input,
            output,
            TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void ExportFiles_ValidEmptyCollision_ReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.col");
        var output = Path.Combine(temp.Path, "output");
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);
        File.WriteAllBytes(input, data);

        var result = MeshExportCliOptions.ExportFiles(
            [input],
            output,
            ModelSourceKind.Collision,
            MeshOutputFormat.Glb,
            blenderHelperPath: null,
            verbose: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static int Export(
        string input,
        string output,
        CancellationToken cancellationToken)
    {
        return MeshExportCliOptions.ExportFiles(
            [input],
            output,
            ModelSourceKind.Psx,
            MeshOutputFormat.Glb,
            blenderHelperPath: null,
            verbose: true,
            cancellationToken: cancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-mesh-export-cli-{Guid.NewGuid():N}");
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
