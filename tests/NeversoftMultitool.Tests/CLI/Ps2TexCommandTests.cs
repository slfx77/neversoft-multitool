using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class Ps2TexCommandTests
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

        var validPath = Path.Combine(input, "[good].img.ps2");
        var invalidPath = Path.Combine(input, "[bad].img.ps2");
        File.WriteAllBytes(validPath, BuildOnePixelPsmct32Img());
        File.WriteAllBytes(invalidPath, BuildTruncatedVersion2Img());

        var result = Ps2TexCommand.Execute(
            input, output, verbose: true, gifQwordOrderText: "0123", CancellationToken.None);

        Assert.Equal(1, result);
        var pngPath = Path.Combine(output, "[good]", "DEADC0DE.png");
        Assert.Equal(pngPath, Assert.Single(Directory.EnumerateFiles(
            output, "*.png", SearchOption.AllDirectories)));
        Assert.Equal(PngSignature, File.ReadAllBytes(pngPath)[..PngSignature.Length]);
        Assert.False(Directory.Exists(Path.Combine(output, "[bad]")));

        var validOnlyOutput = Path.Combine(temp.Path, "valid-only-output");
        Assert.Equal(0, Ps2TexCommand.Execute(
            validPath,
            validOnlyOutput,
            verbose: true,
            gifQwordOrderText: "0123",
            CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(validOnlyOutput, "[good]", "DEADC0DE.png")));

        var invalidOnlyOutput = Path.Combine(temp.Path, "invalid-only-output");
        Assert.Equal(1, Ps2TexCommand.Execute(
            invalidPath,
            invalidOnlyOutput,
            verbose: true,
            gifQwordOrderText: "0123",
            CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(
            invalidOnlyOutput, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_NestedDuplicateStandardStems_MirrorOnlyCollisions()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var left = Path.Combine(input, "left");
        var right = Path.Combine(input, "right");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllBytes(
            Path.Combine(left, "shared.img.ps2"), BuildOnePixelPsmct32Img());
        File.WriteAllBytes(
            Path.Combine(right, "shared.img.ps2"), BuildOnePixelPsmct32Img());

        var result = Ps2TexCommand.Execute(
            input, output, verbose: true, gifQwordOrderText: "0123", CancellationToken.None);

        Assert.Equal(0, result);
        string[] expected =
        [
            Path.Combine(output, "left", "shared", "DEADC0DE.png"),
            Path.Combine(output, "right", "shared", "DEADC0DE.png")
        ];
        var actual = Directory.EnumerateFiles(output, "*.png", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
        Assert.All(actual, path =>
            Assert.Equal(PngSignature, File.ReadAllBytes(path)[..PngSignature.Length]));
        Assert.False(File.Exists(Path.Combine(output, "shared", "DEADC0DE.png")));
    }

    [Fact]
    public void Execute_MissingPathFailsAndEmptyDirectorySucceeds()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, Ps2TexCommand.Execute(
            missing,
            missingOutput,
            verbose: true,
            gifQwordOrderText: "0123",
            CancellationToken.None));
        Assert.Equal(0, Ps2TexCommand.Execute(
            empty,
            emptyOutput,
            verbose: true,
            gifQwordOrderText: "0123",
            CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotWritePng()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].img.ps2");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildOnePixelPsmct32Img());

        Assert.Throws<OperationCanceledException>(() => Ps2TexCommand.Execute(
            input,
            output,
            verbose: true,
            gifQwordOrderText: "0123",
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_PreCancelled_TakesPrecedenceOverInvalidGifOrder()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.img.ps2");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildOnePixelPsmct32Img());

        Assert.Throws<OperationCanceledException>(() => Ps2TexCommand.Execute(
            input,
            output,
            verbose: true,
            gifQwordOrderText: "[0123]",
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_InvalidBracketedGifOrder_ReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.img.ps2");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildOnePixelPsmct32Img());

        Assert.Equal(1, Ps2TexCommand.Execute(
            input,
            output,
            verbose: true,
            gifQwordOrderText: "[0123]",
            CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    private static byte[] BuildOnePixelPsmct32Img()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2); // IMG version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0xDEADC0DE); // checksum
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0); // TW: 1 pixel
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0); // TH: 1 pixel
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0); // PSMCT32
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 0); // CPSM (unused)
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0); // MXL
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 1); // original width
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(30), 1); // original height
        data[32] = 0x11; // red
        data[33] = 0x22; // green
        data[34] = 0x33; // blue
        data[35] = 0x80; // PS2 nominal full alpha
        return data;
    }

    private static byte[] BuildTruncatedVersion2Img()
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-ps2tex-{Guid.NewGuid():N}");
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
