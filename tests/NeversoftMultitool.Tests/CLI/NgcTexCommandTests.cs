using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.CLI;

public sealed class NgcTexCommandTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Execute_MixedBracketedBatch_PreservesValidPngAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var validPath = Path.Combine(input, "[good].tex.ngc");
        var invalidPath = Path.Combine(input, "[bad].tex.ngc");
        File.WriteAllBytes(validPath, NgcTexTestBuilder.CreateDictionary());
        File.WriteAllBytes(invalidPath, BuildProbeAcceptedTruncatedCmprDictionary());

        var result = NgcTexCommand.Execute(
            input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        var pngPath = Path.Combine(output, "[good]", "12345678.png");
        Assert.Equal(pngPath, Assert.Single(Directory.EnumerateFiles(
            output, "*.png", SearchOption.AllDirectories)));
        Assert.Equal(PngSignature, File.ReadAllBytes(pngPath)[..PngSignature.Length]);
        Assert.False(Directory.Exists(Path.Combine(output, "[bad]")));

        var validOnlyOutput = Path.Combine(temp.Path, "valid-only-output");
        Assert.Equal(0, NgcTexCommand.Execute(
            validPath, validOnlyOutput, verbose: true, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(validOnlyOutput, "[good]", "12345678.png")));

        var invalidOnlyOutput = Path.Combine(temp.Path, "invalid-only-output");
        Assert.Equal(1, NgcTexCommand.Execute(
            invalidPath, invalidOnlyOutput, verbose: true, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(
            invalidOnlyOutput, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_MissingAndProbeRejectedInputsPreserveNoWorkContract()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var unsupported = Path.Combine(temp.Path, "[unsupported].tex.ngc");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        var unsupportedOutput = Path.Combine(temp.Path, "unsupported-output");
        Directory.CreateDirectory(empty);
        File.WriteAllBytes(unsupported, NgcTexTestBuilder.CreateDictionary(0, 0));

        Assert.Equal(1, NgcTexCommand.Execute(
            missing, missingOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, NgcTexCommand.Execute(
            empty, emptyOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, NgcTexCommand.Execute(
            unsupported, unsupportedOutput, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(unsupportedOutput));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotWritePng()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].tex.ngc");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, NgcTexTestBuilder.CreateDictionary());

        Assert.Throws<OperationCanceledException>(() => NgcTexCommand.Execute(
            input,
            output,
            verbose: true,
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_RecursiveDuplicateStems_PreserveBothRelativeOutputs()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var left = Path.Combine(input, "left");
        var right = Path.Combine(input, "right");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllBytes(
            Path.Combine(left, "[shared].tex.ngc"),
            NgcTexTestBuilder.CreateDictionary());
        File.WriteAllBytes(
            Path.Combine(right, "[shared].tex.ngc"),
            NgcTexTestBuilder.CreateDictionary());

        var relativeInput = Path.GetRelativePath(Directory.GetCurrentDirectory(), input);
        Assert.Equal(0, NgcTexCommand.Execute(
            relativeInput, output, verbose: true, CancellationToken.None));

        var expected = new[]
        {
            Path.Combine(output, "left", "[shared]", "12345678.png"),
            Path.Combine(output, "right", "[shared]", "12345678.png")
        };
        Assert.Equal(
            expected,
            Directory.EnumerateFiles(output, "*.png", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(expected, AssertPng);
        Assert.False(File.Exists(Path.Combine(output, "[shared]", "12345678.png")));
    }

    [Fact]
    public void Execute_BareImgDirectoryAndDirectInput_UseCompoundStem()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var imagePath = Path.Combine(input, "[image].img.ngc");
        var directoryOutput = Path.Combine(temp.Path, "directory-output");
        var directOutput = Path.Combine(temp.Path, "direct-output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(imagePath, BuildBareImg());

        Assert.Equal(0, NgcTexCommand.Execute(
            input, directoryOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, NgcTexCommand.Execute(
            imagePath, directOutput, verbose: true, CancellationToken.None));

        var expectedDirectoryPng = Path.Combine(
            directoryOutput, "[image]", "12345678.png");
        var expectedDirectPng = Path.Combine(
            directOutput, "[image]", "12345678.png");
        Assert.Equal(expectedDirectoryPng, Assert.Single(Directory.EnumerateFiles(
            directoryOutput, "*.png", SearchOption.AllDirectories)));
        Assert.Equal(expectedDirectPng, Assert.Single(Directory.EnumerateFiles(
            directOutput, "*.png", SearchOption.AllDirectories)));
        AssertPng(expectedDirectoryPng);
        AssertPng(expectedDirectPng);
        Assert.False(Directory.Exists(Path.Combine(directoryOutput, "[image].img")));
        Assert.False(Directory.Exists(Path.Combine(directOutput, "[image].img")));
    }

    private static byte[] BuildProbeAcceptedTruncatedCmprDictionary()
    {
        var data = NgcTexTestBuilder.CreateDictionary();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 1);
        return data;
    }

    private static byte[] BuildBareImg()
    {
        var dictionary = NgcTexTestBuilder.CreateDictionary();
        var data = new byte[64];
        dictionary.AsSpan(8, 32).CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 32);
        dictionary.AsSpan(40, 32).CopyTo(data.AsSpan(32));
        return data;
    }

    private static void AssertPng(string path)
    {
        Assert.True(File.Exists(path), $"Missing PNG: {path}");
        Assert.Equal(PngSignature, File.ReadAllBytes(path)[..PngSignature.Length]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-ngctex-{Guid.NewGuid():N}");
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
