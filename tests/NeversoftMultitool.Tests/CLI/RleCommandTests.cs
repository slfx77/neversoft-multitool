using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class RleCommandTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Execute_MixedBracketedBatch_PreservesSuccessAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[good].tga"), BuildOnePixelTga());
        File.WriteAllBytes(Path.Combine(input, "[bad].rle"), "NOPE"u8.ToArray());

        var result = RleCommand.Execute(
            input,
            output,
            width: 0,
            verbose: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        var pngPath = Path.Combine(output, "[good].png");
        Assert.Equal(pngPath, Assert.Single(Directory.EnumerateFiles(output, "*.png")));
        Assert.Equal(PngSignature, File.ReadAllBytes(pngPath)[..PngSignature.Length]);
        Assert.False(File.Exists(Path.Combine(output, "[bad].png")));
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

        Assert.Equal(1, RleCommand.Execute(
            missing,
            missingOutput,
            width: 0,
            verbose: true,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, RleCommand.Execute(
            empty,
            emptyOutput,
            width: 0,
            verbose: true,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_DifferentFormatsWithSameStem_WriteDistinctOutputs()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "same.bmp"), BuildOnePixelBmp());
        File.WriteAllBytes(Path.Combine(input, "same.tga"), BuildOnePixelTga());

        var result = RleCommand.Execute(
            input,
            output,
            width: 0,
            verbose: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        var expected = new[]
        {
            Path.Combine(output, "same.png"),
            Path.Combine(output, "same_2.png")
        };
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(output, "*.png").Order(StringComparer.Ordinal));
        Assert.All(expected, path =>
            Assert.Equal(PngSignature, File.ReadAllBytes(path)[..PngSignature.Length]));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotCreateOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[cancelled].tga"), BuildOnePixelTga());

        Assert.Throws<OperationCanceledException>(() => RleCommand.Execute(
            input,
            output,
            width: 0,
            verbose: true,
            cancellationToken: new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    private static byte[] BuildOnePixelTga()
    {
        var data = new byte[21];
        data[2] = 2; // uncompressed true-color image
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 1);
        data[16] = 24;
        data[17] = 0x20; // top-left origin
        data[18] = 0x33; // blue
        data[19] = 0x22; // green
        data[20] = 0x11; // red
        return data;
    }

    private static byte[] BuildOnePixelBmp()
    {
        var data = new byte[58];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34), 4);
        data[54] = 0x66; // blue
        data[55] = 0x55; // green
        data[56] = 0x44; // red
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-rle-{Guid.NewGuid():N}");
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
