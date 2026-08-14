using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GlbRenderCommandTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void SelectCandidatePaths_FiltersExtensionCaseInsensitivelyAndRemovesOnlyExactDuplicates()
    {
        var glbPath = Path.Combine("input", "nested", "first.GlB");
        var caseDistinctGlbPath = Path.Combine("input", "nested", "FIRST.GLB");
        var unrelatedPath = Path.Combine("input", "nested", "notes.txt");

        var result = GlbRenderCommand.SelectCandidatePaths(
            [glbPath, glbPath, caseDistinctGlbPath, unrelatedPath]);

        Assert.Equal([glbPath, caseDistinctGlbPath], result);
    }

    [Fact]
    public void FindDuplicateBesideSourceOutputs_UsesExactDirectoryAndStemIdentity()
    {
        var left = Path.Combine("input", "left");
        var right = Path.Combine("input", "right");
        var lowerCasePath = Path.Combine(left, "clip.glb");
        var sameStemPath = Path.Combine(left, "clip.GLB");
        var upperCasePath = Path.Combine(left, "CLIP.GLB");
        var otherDirectoryPath = Path.Combine(right, "clip.GLB");

        Assert.Equal(
            [Path.GetFullPath(Path.Combine(left, "clip"))],
            GlbRenderCommand.FindDuplicateBesideSourceOutputs(
                [lowerCasePath, sameStemPath]));
        Assert.Empty(GlbRenderCommand.FindDuplicateBesideSourceOutputs(
            [lowerCasePath, upperCasePath]));
        Assert.Empty(GlbRenderCommand.FindDuplicateBesideSourceOutputs(
            [lowerCasePath, otherDirectoryPath]));
    }

    [Fact]
    public void Execute_MixedBracketedBatch_PreservesSuccessAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[good].glb"), BuildEmptySceneGlb());
        File.WriteAllBytes(Path.Combine(input, "[bad].glb"), "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(input, output));

        var goodPng = Path.Combine(output, "[good].png");
        AssertPngSet(output, goodPng);
        Assert.False(File.Exists(Path.Combine(output, "[bad].png")));
    }

    [Fact]
    public void Execute_NestedDuplicateStems_MirrorRelativeSourceDirectories()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        var left = Path.Combine(input, "left");
        var right = Path.Combine(input, "right", "nested");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllBytes(Path.Combine(left, "shared.glb"), BuildEmptySceneGlb());
        File.WriteAllBytes(Path.Combine(right, "shared.glb"), BuildEmptySceneGlb());

        var relativeInput = Path.GetRelativePath(Directory.GetCurrentDirectory(), input);
        Assert.Equal(0, Execute(relativeInput, output));

        AssertPngSet(
            output,
            Path.Combine(output, "left", "shared.png"),
            Path.Combine(output, "right", "nested", "shared.png"));
        Assert.False(File.Exists(Path.Combine(output, "shared.png")));
    }

    [Fact]
    public void Execute_UniqueDirectoryInputStaysFlatAndDirectWrongSuffixStaysBesideSource()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var nested = Path.Combine(input, "nested");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "unique.glb"), BuildEmptySceneGlb());

        Assert.Equal(0, Execute(input, output));
        AssertPngSet(output, Path.Combine(output, "unique.png"));
        Assert.False(Directory.Exists(Path.Combine(output, "nested")));

        var direct = Path.Combine(temp.Path, "[direct].bin");
        File.WriteAllBytes(direct, BuildEmptySceneGlb());
        Assert.Equal(0, Execute(direct, output: null));
        AssertPng(Path.Combine(temp.Path, "[direct].png"));
    }

    [Fact]
    public void Execute_MissingAndNoWorkDirectoriesPreserveExistingContract()
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
        File.WriteAllBytes(Path.Combine(ignored, "[ignored].bin"), BuildEmptySceneGlb());

        Assert.Equal(1, Execute(missing, missingOutput));
        Assert.Equal(0, Execute(empty, emptyOutput));
        Assert.Equal(0, Execute(ignored, ignoredOutput));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(ignoredOutput));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotCreateOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].glb");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        Assert.Throws<OperationCanceledException>(() => Execute(
            input,
            output,
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    private static int Execute(
        string input,
        string? output,
        CancellationToken cancellationToken = default)
    {
        return GlbRenderCommand.Execute(
            input,
            output,
            longEdge: 8,
            azimuth: -90f,
            elevation: 10f,
            preset: null,
            animIndex: null,
            time: null,
            verbose: true,
            cancellationToken: cancellationToken);
    }

    private static byte[] BuildEmptySceneGlb()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{}]}");
        var paddedJsonLength = (json.Length + 3) & ~3;
        var data = new byte[12 + 8 + paddedJsonLength];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x46546C67); // glTF
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x4E4F534A); // JSON
        json.AsSpan().CopyTo(data.AsSpan(20));
        data.AsSpan(20 + json.Length, paddedJsonLength - json.Length).Fill(0x20);
        return data;
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
                System.IO.Path.GetTempPath(), $"nmt-glb-render-{Guid.NewGuid():N}");
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
