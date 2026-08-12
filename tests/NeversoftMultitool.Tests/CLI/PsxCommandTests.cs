using NeversoftMultitool.CLI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxCommandTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Execute_AmbiguousFailureStates_ReturnFailureAndPreservePartialPng()
    {
        using var temp = new TempDirectory();

        var invalid = BuildInvalidMagicPsx();
        var partial = BuildPartialThenUnsupportedPsx();
        var incomplete = BuildMissingPalettePsx();
        Assert.Equal(4, invalid.Length);
        Assert.Equal(112, partial.Length);
        Assert.Equal(68, incomplete.Length);

        var invalidInput = CreateCaseDirectory(temp.Path, "invalid", "[bad].psx", invalid);
        var invalidOutput = Path.Combine(temp.Path, "invalid-output");
        Assert.Equal(1, PsxCommand.Execute(
            invalidInput,
            invalidOutput,
            subdirs: true,
            verbose: true,
            noDds: true,
            CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(
            invalidOutput, "*", SearchOption.AllDirectories));

        var partialInput = CreateCaseDirectory(temp.Path, "partial", "[partial].psx", partial);
        var partialOutput = Path.Combine(temp.Path, "partial-output");
        Assert.Equal(1, PsxCommand.Execute(
            partialInput,
            partialOutput,
            subdirs: true,
            verbose: true,
            noDds: true,
            CancellationToken.None));
        AssertPng(Path.Combine(
            partialOutput, "[partial]", "[partial]_00000034.png"));
        Assert.Empty(Directory.EnumerateFiles(
            partialOutput, "*.dds", SearchOption.AllDirectories));

        var incompleteInput = CreateCaseDirectory(
            temp.Path, "incomplete", "[incomplete].psx", incomplete);
        var incompleteOutput = Path.Combine(temp.Path, "incomplete-output");
        Assert.Equal(1, PsxCommand.Execute(
            incompleteInput,
            incompleteOutput,
            subdirs: true,
            verbose: true,
            noDds: true,
            CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(
            incompleteOutput, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_MixedBracketedDirectory_PreservesValidSiblingAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var positive = BuildPositiveRectanglePsx();
        Assert.Equal(76, positive.Length);
        File.WriteAllBytes(Path.Combine(input, "[good].psx"), positive);
        File.WriteAllBytes(Path.Combine(input, "[bad].psx"), BuildInvalidMagicPsx());

        var result = PsxCommand.Execute(
            input,
            output,
            subdirs: true,
            verbose: true,
            noDds: true,
            CancellationToken.None);

        Assert.Equal(1, result);
        var pngPath = Path.Combine(output, "[good]", "[good]_0000002C.png");
        Assert.Equal(pngPath, Assert.Single(Directory.EnumerateFiles(
            output, "*.png", SearchOption.AllDirectories)));
        AssertPng(pngPath);
        Assert.False(Directory.Exists(Path.Combine(output, "[bad]")));
        Assert.Empty(Directory.EnumerateFiles(output, "*.dds", SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_ValidAndLegitimateSkipOnly_ReturnsSuccess()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var skipped = BuildZeroTexturePsx();
        Assert.Equal(36, skipped.Length);
        File.WriteAllBytes(Path.Combine(input, "[good].psx"), BuildPositiveRectanglePsx());
        File.WriteAllBytes(Path.Combine(input, "[skip].psx"), skipped);

        Assert.Equal(0, PsxCommand.Execute(
            input,
            output,
            subdirs: true,
            verbose: true,
            noDds: true,
            CancellationToken.None));
        AssertPng(Path.Combine(output, "[good]", "[good]_0000002C.png"));
        Assert.False(Directory.Exists(Path.Combine(output, "[skip]")));
    }

    [Fact]
    public void Execute_MissingEmptyDirectoryOnlyAndCancellationBoundariesArePinned()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var existingFile = Path.Combine(temp.Path, "[single].psx");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        var fileOutput = Path.Combine(temp.Path, "file-output");
        Directory.CreateDirectory(empty);
        File.WriteAllBytes(existingFile, BuildPositiveRectanglePsx());

        Assert.Equal(1, PsxCommand.Execute(
            missing, missingOutput, false, true, true, CancellationToken.None));
        Assert.Equal(0, PsxCommand.Execute(
            empty, emptyOutput, false, true, true, CancellationToken.None));
        Assert.Equal(1, PsxCommand.Execute(
            existingFile, fileOutput, false, true, true, CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(fileOutput));

        var cancelledInput = Path.Combine(temp.Path, "cancelled-input");
        var cancelledOutput = Path.Combine(temp.Path, "cancelled-output");
        Directory.CreateDirectory(cancelledInput);
        File.WriteAllBytes(
            Path.Combine(cancelledInput, "[cancelled].psx"), BuildPositiveRectanglePsx());

        Assert.Throws<OperationCanceledException>(() => PsxCommand.Execute(
            cancelledInput,
            cancelledOutput,
            subdirs: true,
            verbose: true,
            noDds: true,
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(cancelledOutput));
    }

    private static byte[] BuildPositiveRectanglePsx()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteLibraryPrefix(writer, [0xDEADC0DE]);
        writer.Write(1u); // actual texture count
        writer.Write(0x48u); // pixel-data pointer
        WriteRectangleHeader(writer, index: 0, pixelFormat: 0x0901, size: 4);
        writer.Write((ushort)0xF800); // red, RGB565
        writer.Write((ushort)0x07E0); // green, RGB565
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildPartialThenUnsupportedPsx()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteLibraryPrefix(writer, [0xDEADC0DE, 0xBADC0FFE]);
        writer.Write(2u); // actual texture count
        writer.Write(0x50u); // first pixel-data pointer
        writer.Write(0x70u); // second pixel-data pointer

        WriteRectangleHeader(writer, index: 0, pixelFormat: 0x0901, size: 4);
        writer.Write((ushort)0xF800);
        writer.Write((ushort)0x07E0);

        // The first texture has already been written when this source-valid header
        // shape reaches the intentionally unsupported encoding. PsxLibrary then
        // returns ErrorMessage + Success because it rewrites TotalTextures=Written.
        WriteRectangleHeader(writer, index: 1, pixelFormat: 0x0001, size: 0);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildMissingPalettePsx()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteLibraryPrefix(writer, [0xDEADC0DE]);
        writer.Write(1u); // actual texture count
        writer.Write(0x40u); // pixel-data pointer
        writer.Write(0u); // unknown
        writer.Write(16u); // 4-bit texture, but no matching palette was declared
        writer.Write(0x12345678u); // unmatched palette id
        writer.Write(0u); // texture-name index
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(0u); // padded 4-bit index data
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildZeroTexturePsx()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteLibraryPrefix(writer, []);
        writer.Write(0u); // actual texture count
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildInvalidMagicPsx()
    {
        return "NOPE"u8.ToArray();
    }

    private static void WriteLibraryPrefix(BinaryWriter writer, uint[] textureNameHashes)
    {
        writer.Write(new byte[] { 0x04, 0x00, 0x02, 0x00 });
        writer.Write(0x10u); // tagged-chunk pointer
        writer.Write(0u); // object count
        writer.Write(0u); // mesh count
        writer.Write(uint.MaxValue); // tagged-chunk terminator
        writer.Write((uint)textureNameHashes.Length);
        foreach (var hash in textureNameHashes)
            writer.Write(hash);
        writer.Write(0u); // 4-bit palette count
        writer.Write(0u); // 8-bit palette count
    }

    private static void WriteRectangleHeader(
        BinaryWriter writer,
        uint index,
        uint pixelFormat,
        uint size)
    {
        writer.Write(0u); // unknown
        writer.Write(65536u); // direct 16-bit pixels
        writer.Write(0u); // texture id
        writer.Write(index);
        writer.Write((ushort)1); // width
        writer.Write((ushort)2); // height (minimum accepted by the PVR decoder)
        writer.Write(pixelFormat);
        writer.Write(size);
    }

    private static string CreateCaseDirectory(
        string root,
        string name,
        string fileName,
        byte[] data)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), data);
        return directory;
    }

    private static void AssertPng(string path)
    {
        Assert.True(File.Exists(path), $"Missing PNG: {path}");
        Assert.Equal(PngSignature, File.ReadAllBytes(path)[..PngSignature.Length]);
        using var image = Image.Load<Rgba32>(path);
        Assert.Equal(1, image.Width);
        Assert.Equal(2, image.Height);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-{Guid.NewGuid():N}");
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
