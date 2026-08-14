using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class Thps2XFrontendAnimCommandTests
{
    [Fact]
    public void Execute_MalformedBracketedAnimReturnsFailureWithoutJson()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[bad].ANIM");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        var result = Thps2XFrontendAnimCommand.Execute(
            input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(output, "[bad].anim.json")));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_MissingDirectoryFailsAndEmptyDirectorySucceeds()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, Thps2XFrontendAnimCommand.Execute(
            missing, missingOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, Thps2XFrontendAnimCommand.Execute(
            empty, emptyOutput, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_MinimalBracketedAnimWritesJsonAndSucceeds()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[good].ANIM");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildMinimalAnim());

        var result = Thps2XFrontendAnimCommand.Execute(
            input, output, verbose: true, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(
            Path.Combine(output, "[good].anim.json"),
            Assert.Single(Directory.EnumerateFiles(output, "*.json")));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotWriteJson()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].ANIM");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildMinimalAnim());

        Assert.Throws<OperationCanceledException>(() =>
            Thps2XFrontendAnimCommand.Execute(
                input,
                output,
                verbose: true,
                new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_EmptyDirectoryPreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        Assert.Throws<OperationCanceledException>(() =>
            Thps2XFrontendAnimCommand.Execute(
                input,
                output,
                verbose: true,
                new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    private static byte[] BuildMinimalAnim()
    {
        var data = new byte[16];
        "Anm\0"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-thps2x-anim-{Guid.NewGuid():N}");
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
