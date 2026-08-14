using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimExportRunnerTests
{
    [Fact]
    public void Run_AnimationOnlyGlb_ReturnsSuccessForEmittedOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "character.psx");
        var output = Path.Combine(temp.Path, "output", "animated.glb");
        File.WriteAllBytes(input, BuildMinimalDirectMatrixPsx());

        var result = Run(input, output);

        Assert.Equal(0, result);
        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
        var model = ModelRoot.Load(output);
        Assert.Single(model.LogicalAnimations);
    }

    [Fact]
    public void Run_MissingBracketedInput_ReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[missing].psx");
        var output = Path.Combine(temp.Path, "output", "[animated].glb");

        Assert.Equal(1, Run(input, output));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Fact]
    public void Run_PreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].psx");
        var output = Path.Combine(temp.Path, "output", "[animated].glb");
        var original = "NOPE"u8.ToArray();
        File.WriteAllBytes(input, original);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Run(input, output, cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    private static int Run(
        string input,
        string output,
        CancellationToken? cancellationToken = null)
    {
        return PsxAnimExportRunner.Run(
            input,
            output,
            animSourcePath: null,
            animIndex: -1,
            animName: null,
            opts: new PsxAnimationOptions(),
            format: MeshOutputFormat.Glb,
            blenderHelper: null,
            flatSkeleton: false,
            flatBoneFilter: null,
            verbose: false,
            cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static byte[] BuildMinimalDirectMatrixPsx()
    {
        var data = new byte[0x7C];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x00), 0x04);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x02), 0x02);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), 1);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x38), 0x52454948); // HIER
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x40), 0);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), PsxMeshFile.HierChunkV1Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x48), 0x24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x4C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x50), 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x54), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x56), 0);

        Span<short> matrix =
        [
            4096, 0, 0,
            0, 4096, 0,
            0, 0, 4096
        ];
        for (var i = 0; i < matrix.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x58 + i * 2), matrix[i]);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x6A), 36);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x70), uint.MaxValue);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-anim-export-{Guid.NewGuid():N}");
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
