using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class UnpackCommandTests
{
    [Fact]
    public void Execute_MixedBracketedBatch_PreservesSuccessAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);

        File.WriteAllBytes(Path.Combine(input, "[bad].wad"), []);
        WriteSingleEntryWad(input, "[good]");

        var result = UnpackCommand.Execute(input, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal([0x42], File.ReadAllBytes(Path.Combine(input, "[good]", "ok.bin")));
        Assert.False(Directory.Exists(Path.Combine(input, "[bad]")));

        var validOnly = Path.Combine(temp.Path, "valid-only");
        Directory.CreateDirectory(validOnly);
        WriteSingleEntryWad(validOnly, "[only]");

        Assert.Equal(0, UnpackCommand.Execute(
            validOnly, verbose: true, CancellationToken.None));
        Assert.Equal([0x42], File.ReadAllBytes(Path.Combine(validOnly, "[only]", "ok.bin")));
    }

    [Fact]
    public void Execute_MissingDirectoryFailsAndEmptyDirectorySucceeds()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, UnpackCommand.Execute(
            missing, verbose: true, CancellationToken.None));
        Assert.Equal(0, UnpackCommand.Execute(
            empty, verbose: true, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
    }

    [Fact]
    public void Execute_PreCancelledEmptyDirectoryThrowsWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => UnpackCommand.Execute(
            input, verbose: true, cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(input));
    }

    private static void WriteSingleEntryWad(string directory, string stem)
    {
        File.WriteAllBytes(Path.Combine(directory, stem + ".wad"), [0x42]);

        var hed = new byte[17];
        "ok.bin"u8.CopyTo(hed);
        BinaryPrimitives.WriteUInt32LittleEndian(hed.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(hed.AsSpan(12), 1);
        hed[16] = 0xFF;
        File.WriteAllBytes(Path.Combine(directory, stem + ".HED"), hed);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-unpack-{Guid.NewGuid():N}");
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
