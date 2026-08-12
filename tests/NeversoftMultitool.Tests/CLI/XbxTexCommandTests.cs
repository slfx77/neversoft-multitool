using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class XbxTexCommandTests
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

        var valid = BuildOnePixelRawBgraImg();
        File.WriteAllBytes(Path.Combine(input, "[good].img.xbx"), valid);
        File.WriteAllBytes(Path.Combine(input, "[bad].img.xbx"), valid[..^1]);

        var result = XbxTexCommand.Execute(input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        var pngPath = Path.Combine(output, "[good].png");
        Assert.Equal(pngPath, Assert.Single(Directory.EnumerateFiles(output, "*.png")));
        Assert.Equal(PngSignature, File.ReadAllBytes(pngPath)[..PngSignature.Length]);
        Assert.False(File.Exists(Path.Combine(output, "[bad].png")));

        var validOnlyOutput = Path.Combine(temp.Path, "valid-only-output");
        Assert.Equal(0, XbxTexCommand.Execute(
            Path.Combine(input, "[good].img.xbx"),
            validOnlyOutput,
            verbose: true,
            CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(validOnlyOutput, "[good].png")));
    }

    [Fact]
    public void Execute_DuplicateNestedStems_MirrorRelativeSourceDirectories()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        var first = Path.Combine(input, "first");
        var second = Path.Combine(input, "second", "nested");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var valid = BuildOnePixelRawBgraImg();
        Assert.Equal(36, valid.Length);
        File.WriteAllBytes(Path.Combine(first, "[shared].img.xbx"), valid);
        File.WriteAllBytes(Path.Combine(second, "[shared].img.xbx"), valid);

        var relativeInput = Path.GetRelativePath(Directory.GetCurrentDirectory(), input);
        Assert.Equal(0, XbxTexCommand.Execute(
            relativeInput, output, verbose: true, CancellationToken.None));

        var expected = new[]
        {
            Path.Combine(output, "first", "[shared].png"),
            Path.Combine(output, "second", "nested", "[shared].png")
        };
        Assert.Equal(
            expected,
            Directory.EnumerateFiles(output, "*.png", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(expected, path =>
            Assert.Equal(PngSignature, File.ReadAllBytes(path)[..PngSignature.Length]));
        Assert.False(File.Exists(Path.Combine(output, "[shared].png")));
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

        Assert.Equal(1, XbxTexCommand.Execute(
            missing, missingOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, XbxTexCommand.Execute(
            empty, emptyOutput, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotWritePng()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].img.xbx");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildOnePixelRawBgraImg());

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => XbxTexCommand.Execute(
            input, output, verbose: true, cancellationSource.Token));
        Assert.Empty(Directory.EnumerateFiles(output, "*.png"));
    }

    private static byte[] BuildOnePixelRawBgraImg()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2); // version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 8); // retained header value
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1); // width
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1); // height
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), 1); // pitch width
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1); // pitch height
        data[32] = 0x33; // blue
        data[33] = 0x22; // green
        data[34] = 0x11; // red
        data[35] = 0xFF; // alpha
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-xbxtex-{Guid.NewGuid():N}");
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
