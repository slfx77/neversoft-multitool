using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GlbGifCommandTests
{
    [Fact]
    public void Execute_MixedBracketedBatch_PreservesSuccessAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[good].glb"), BuildAnimatedEmptySceneGlb());
        File.WriteAllBytes(Path.Combine(input, "[bad].glb"), "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(input, output));

        var goodGif = Path.Combine(output, "[good].gif");
        AssertGifSet(output, goodGif);
        Assert.False(File.Exists(Path.Combine(output, "[bad].gif")));
    }

    [Fact]
    public void Execute_NestedDuplicateStems_MirrorRelativeDirectoriesBeforeAnimationSuffix()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        var left = Path.Combine(input, "left");
        var right = Path.Combine(input, "right", "nested");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllBytes(Path.Combine(left, "shared.glb"), BuildAnimatedEmptySceneGlb());
        File.WriteAllBytes(Path.Combine(right, "shared.glb"), BuildAnimatedEmptySceneGlb());

        var relativeInput = Path.GetRelativePath(Directory.GetCurrentDirectory(), input);
        Assert.Equal(0, Execute(relativeInput, output, animIndex: 0));

        AssertGifSet(
            output,
            Path.Combine(output, "left", "shared_anim0.gif"),
            Path.Combine(output, "right", "nested", "shared_anim0.gif"));
        Assert.False(File.Exists(Path.Combine(output, "shared_anim0.gif")));
    }

    [Fact]
    public void Execute_UniqueExplicitOutputStaysFlatAndDirectWrongSuffixStaysBesideSource()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var nested = Path.Combine(input, "nested");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "unique.glb"), BuildAnimatedEmptySceneGlb());

        Assert.Equal(0, Execute(input, output));
        AssertGifSet(output, Path.Combine(output, "unique.gif"));
        Assert.False(Directory.Exists(Path.Combine(output, "nested")));

        var direct = Path.Combine(temp.Path, "[direct].bin");
        File.WriteAllBytes(direct, BuildAnimatedEmptySceneGlb());
        Assert.Equal(0, Execute(direct, output: null));
        AssertGif(Path.Combine(temp.Path, "[direct].gif"));
    }

    [Fact]
    public void Execute_NonAnimatedGlbSkipsWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[still].glb");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        Assert.Equal(0, Execute(input, output));

        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(output, "[still].gif")));
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
        File.WriteAllBytes(
            Path.Combine(ignored, "[ignored].bin"),
            BuildAnimatedEmptySceneGlb());

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
        File.WriteAllBytes(input, BuildAnimatedEmptySceneGlb());

        Assert.Throws<OperationCanceledException>(() => Execute(
            input,
            output,
            animIndex: null,
            cancellationToken: new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RenderToFile_NonPositiveFps_ThrowsBeforeCreatingOutput(int fps)
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "animated.glb");
        var outputDirectory = Path.Combine(temp.Path, "output");
        var output = Path.Combine(outputDirectory, "animated.gif");
        File.WriteAllBytes(input, BuildAnimatedEmptySceneGlb());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            GlbGifRenderer.RenderToFile(input, output, longEdge: 8, fps: fps));

        Assert.Equal("fps", exception.ParamName);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(output));
    }

    private static int Execute(
        string input,
        string? output,
        int? animIndex = null)
    {
        return Execute(
            input,
            output,
            animIndex,
            TestContext.Current.CancellationToken);
    }

    private static int Execute(
        string input,
        string? output,
        int? animIndex,
        CancellationToken cancellationToken)
    {
        return GlbGifCommand.Execute(
            input,
            output,
            longEdge: 8,
            fps: 2,
            animIndex: animIndex,
            azimuth: -90f,
            elevation: 10f,
            verbose: true,
            cancellationToken: cancellationToken);
    }

    private static byte[] BuildAnimatedEmptySceneGlb()
    {
        const string json = """
                            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{}],"buffers":[{"byteLength":32}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":8},{"buffer":0,"byteOffset":8,"byteLength":24}],"accessors":[{"bufferView":0,"componentType":5126,"count":2,"type":"SCALAR","min":[0],"max":[1]},{"bufferView":1,"componentType":5126,"count":2,"type":"VEC3"}],"animations":[{"samplers":[{"input":0,"output":1,"interpolation":"LINEAR"}],"channels":[{"sampler":0,"target":{"node":0,"path":"translation"}}]}]}
                            """;
        var binary = new byte[32];
        BinaryPrimitives.WriteSingleLittleEndian(binary, 0f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(8), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(12), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(16), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(20), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(24), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(binary.AsSpan(28), 0f);
        return BuildGlb(json, binary);
    }

    private static byte[] BuildEmptySceneGlb()
    {
        return BuildGlb(
            "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{}]}",
            []);
    }

    private static byte[] BuildGlb(string json, byte[] binary)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var paddedJsonLength = (jsonBytes.Length + 3) & ~3;
        var binaryChunkLength = binary.Length == 0 ? 0 : 8 + binary.Length;
        var data = new byte[12 + 8 + paddedJsonLength + binaryChunkLength];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x46546C67); // glTF
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x4E4F534A); // JSON
        jsonBytes.CopyTo(data.AsSpan(20));
        data.AsSpan(20 + jsonBytes.Length, paddedJsonLength - jsonBytes.Length).Fill(0x20);

        if (binary.Length > 0)
        {
            var binaryChunkOffset = 20 + paddedJsonLength;
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(binaryChunkOffset),
                (uint)binary.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(binaryChunkOffset + 4),
                0x004E4942); // BIN
            binary.CopyTo(data.AsSpan(binaryChunkOffset + 8));
        }

        return data;
    }

    private static void AssertGifSet(string output, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal).ToArray(),
            Directory.EnumerateFiles(output, "*.gif", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(expected, AssertGif);
    }

    private static void AssertGif(string path)
    {
        Assert.True(File.Exists(path), $"Missing GIF: {path}");
        using var image = Image.Load<Rgba32>(path);
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
        Assert.Equal(2, image.Frames.Count);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-glb-gif-{Guid.NewGuid():N}");
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
