using NeversoftMultitool.CLI;
using NeversoftMultitool.Tests.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Tests.CLI;

public sealed class N64TexCommandTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Execute_MixedBracketedBatch_PreservesValidLevelsAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var validPath = Path.Combine(input, "[good].tex.n64");
        var invalidPath = Path.Combine(input, "[bad].tex.n64");
        File.WriteAllBytes(
            validPath,
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());
        File.WriteAllBytes(invalidPath, "NOPE"u8.ToArray());

        Assert.Equal(1, N64TexCommand.Execute(
            input, output, verbose: true, CancellationToken.None));
        AssertPngSet(
            output,
            Path.Combine(output, "[good].png"),
            Path.Combine(output, "[good]_mip1.png"));
        Assert.False(File.Exists(Path.Combine(output, "[bad].png")));

        var validOnlyOutput = Path.Combine(temp.Path, "valid-only-output");
        Assert.Equal(0, N64TexCommand.Execute(
            validPath, validOnlyOutput, verbose: true, CancellationToken.None));
        AssertPngSet(
            validOnlyOutput,
            Path.Combine(validOnlyOutput, "[good].png"),
            Path.Combine(validOnlyOutput, "[good]_mip1.png"));

        var invalidOnlyOutput = Path.Combine(temp.Path, "invalid-only-output");
        Assert.Equal(1, N64TexCommand.Execute(
            invalidPath, invalidOnlyOutput, verbose: true, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(
            invalidOnlyOutput, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_MissingAndNoWorkInputs_PreserveExistingContract()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var ignored = Path.Combine(temp.Path, "ignored");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        var ignoredOutput = Path.Combine(temp.Path, "ignored-output");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(ignored);
        File.WriteAllBytes(
            Path.Combine(ignored, "[ignored].bin"),
            N64TexTestBuilder.CreateImageRecord());

        Assert.Equal(1, N64TexCommand.Execute(
            missing, missingOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, N64TexCommand.Execute(
            empty, emptyOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, N64TexCommand.Execute(
            ignored, ignoredOutput, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(ignoredOutput));
    }

    [Fact]
    public void Execute_DirectWrongSuffix_UsesLegacyFirstDotStem()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[direct].copy.bin");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(
            input,
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());

        Assert.Equal(0, N64TexCommand.Execute(
            input, output, verbose: true, CancellationToken.None));
        AssertPngSet(
            output,
            Path.Combine(output, "[direct].png"),
            Path.Combine(output, "[direct]_mip1.png"));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotWritePng()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].tex.n64");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(
            input,
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());

        Assert.Throws<OperationCanceledException>(() => N64TexCommand.Execute(
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
            N64TexCommand.Execute(
                input,
                output,
                verbose: true,
                new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_MalformedSameStem_IsExcludedFromCollisionPlan()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var valid = Path.Combine(input, "valid");
        var malformed = Path.Combine(input, "malformed");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(valid);
        Directory.CreateDirectory(malformed);

        File.WriteAllBytes(
            Path.Combine(valid, "[shared].tex.n64"),
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());
        File.WriteAllBytes(
            Path.Combine(malformed, "[shared].img.n64"),
            "NOPE"u8.ToArray());

        Assert.Equal(1, N64TexCommand.Execute(
            input, output, verbose: true, CancellationToken.None));

        AssertPngSet(
            output,
            Path.Combine(output, "[shared].png"),
            Path.Combine(output, "[shared]_mip1.png"));
        Assert.False(Directory.Exists(Path.Combine(output, "valid")));
        Assert.False(Directory.Exists(Path.Combine(output, "malformed")));
    }

    [Fact]
    public void Execute_NestedDuplicateAndUniqueStems_MirrorOnlyDuplicates()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var left = Path.Combine(input, "left");
        var right = Path.Combine(input, "right", "nested");
        var unique = Path.Combine(input, "unique", "nested");
        var ignored = Path.Combine(input, "ignored");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        Directory.CreateDirectory(unique);
        Directory.CreateDirectory(ignored);

        File.WriteAllBytes(
            Path.Combine(left, "[shared].tex.n64"),
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());
        File.WriteAllBytes(
            Path.Combine(right, "[shared].IMG.N64"),
            N64TexTestBuilder.CreateImageRecord());
        File.WriteAllBytes(
            Path.Combine(unique, "slot.unique.tex.n64"),
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());
        File.WriteAllBytes(
            Path.Combine(ignored, "[shared].bin"),
            N64TexTestBuilder.CreateImageRecord());

        Assert.Equal(0, N64TexCommand.Execute(
            input, output, verbose: true, CancellationToken.None));

        AssertPngSet(
            output,
            Path.Combine(output, "left", "[shared].png"),
            Path.Combine(output, "left", "[shared]_mip1.png"),
            Path.Combine(output, "right", "nested", "[shared].png"),
            Path.Combine(output, "slot.png"),
            Path.Combine(output, "slot_mip1.png"));
        Assert.False(File.Exists(Path.Combine(output, "[shared].png")));
        Assert.False(Directory.Exists(Path.Combine(output, "unique")));
        Assert.False(Directory.Exists(Path.Combine(output, "ignored")));
    }

    [Fact]
    public void Execute_MipAliasCollision_MirrorsAndOrdinalizesWithoutOverwrite()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var same = Path.Combine(input, "same");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(same);

        File.WriteAllBytes(
            Path.Combine(same, "foo.tex.n64"),
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());
        File.WriteAllBytes(
            Path.Combine(same, "foo_mip1.img.n64"),
            N64TexTestBuilder.CreateImageRecord());

        Assert.Equal(0, N64TexCommand.Execute(
            input, output, verbose: true, CancellationToken.None));

        AssertPngSet(
            output,
            Path.Combine(output, "same", "foo.png"),
            Path.Combine(output, "same", "foo_mip1.png"),
            Path.Combine(output, "same", "foo_mip1_2.png"));
        Assert.False(File.Exists(Path.Combine(output, "foo.png")));
        Assert.False(File.Exists(Path.Combine(output, "foo_mip1.png")));
    }

    [Fact]
    public void Execute_SameDirectoryFirstDotCollision_HonorsPlannedStem()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var same = Path.Combine(input, "same");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(same);

        File.WriteAllBytes(
            Path.Combine(same, "slot.one.img.n64"),
            N64TexTestBuilder.CreateImageRecord());
        File.WriteAllBytes(
            Path.Combine(same, "slot.two.tex.n64"),
            N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain());

        Assert.Equal(0, N64TexCommand.Execute(
            input, output, verbose: true, CancellationToken.None));

        AssertPngSet(
            output,
            Path.Combine(output, "same", "slot.png"),
            Path.Combine(output, "same", "slot_2.png"),
            Path.Combine(output, "same", "slot_2_mip1.png"));
        Assert.False(File.Exists(Path.Combine(output, "slot.png")));
    }

    private static void AssertPngSet(string output, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal).ToArray(),
            Directory.EnumerateFiles(output, "*.png", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(expected, AssertPng);
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
                System.IO.Path.GetTempPath(), $"nmt-n64tex-{Guid.NewGuid():N}");
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
