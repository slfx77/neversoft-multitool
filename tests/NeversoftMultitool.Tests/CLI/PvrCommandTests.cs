using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PvrCommandTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void SelectCandidatePaths_FiltersExtensionCaseInsensitivelyAndRemovesOnlyExactDuplicates()
    {
        var pvrPath = Path.Combine("input", "first.PvR");
        var caseDistinctPvrPath = Path.Combine("input", "FIRST.PVR");
        var unrelatedPath = Path.Combine("input", "notes.txt");

        var result = PvrCommand.SelectCandidatePaths(
            [pvrPath, pvrPath, caseDistinctPvrPath, unrelatedPath]);

        Assert.Equal([pvrPath, caseDistinctPvrPath], result);
    }

    [Fact]
    public void FindDuplicateOutputStems_UsesExactStemIdentity()
    {
        var lowerCasePath = Path.Combine("input", "clip.pvr");
        var sameStemPath = Path.Combine("input", "clip.PVR");
        var upperCasePath = Path.Combine("input", "CLIP.PvR");

        Assert.Equal(
            ["clip"],
            PvrCommand.FindDuplicateOutputStems([lowerCasePath, sameStemPath]));
        Assert.Empty(PvrCommand.FindDuplicateOutputStems([lowerCasePath, upperCasePath]));
    }

    [Fact]
    public void Execute_ValidBracketedPvr_VerboseWritesPngAndSucceeds()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[ok].pvr");
        var output = Path.Combine(temp.Path, "output");
        var pngPath = Path.Combine(output, "[ok].png");
        File.WriteAllBytes(input, BuildRgb565RectanglePvr());

        var result = PvrCommand.Execute(
            input,
            output,
            verbose: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(pngPath, Assert.Single(Directory.EnumerateFiles(output)));
        Assert.Equal(PngSignature, File.ReadAllBytes(pngPath)[..PngSignature.Length]);
    }

    [Fact]
    public void Execute_UnsupportedBracketedPvr_VerboseReturnsFailureWithoutPng()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[bad].pvr");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        var result = PvrCommand.Execute(
            input,
            output,
            verbose: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(output, "[bad].png")));
        Assert.Empty(Directory.EnumerateFiles(output, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].pvr");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, BuildRgb565RectanglePvr());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => PvrCommand.Execute(
            input,
            output,
            verbose: true,
            cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private static byte[] BuildRgb565RectanglePvr()
    {
        var data = new byte[24];
        "PVRT"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        data[8] = 1; // RGB565
        data[9] = 9; // rectangle
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 0x07E0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 0x001F);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 0xFFFF);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-pvr-{Guid.NewGuid():N}");
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
