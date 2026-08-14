using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimDumpCommandTests
{
    [Fact]
    public void Execute_MissingAndInvalidBracketedInputs_ReturnFailure()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing].psx");
        var invalid = Path.Combine(temp.Path, "[bad].psx");
        File.WriteAllBytes(invalid, "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(missing));
        Assert.Equal(1, Execute(invalid));
    }

    [Fact]
    public void Execute_PreCancelled_Propagates()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "cancelled.psx");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Execute(input, cancellation.Token));
    }

    [Fact]
    public void Walker_BracketedPshBoneName_RendersLiterally()
    {
        var psh = PshFile.Parse(Encoding.UTF8.GetBytes(
            "#define TESTPART_[bone] 0\n"));
        Assert.NotNull(psh);
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 1);

        var result = PsxAnimDumpWalker.TryWalkHierarchy(
            data,
            startOffset: 0,
            psh,
            verbose: true);

        Assert.NotNull(result);
        Assert.Equal(1, result.NumStreams);
    }

    [Fact]
    public void Execute_OutOfRangeAnimationIndex_ReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "one-clip.psx");
        File.WriteAllBytes(input, BuildMinimalDirectMatrixPsx());

        Assert.Equal(0, Execute(input, animIndex: 0));
        Assert.Equal(1, Execute(input, animIndex: 1));
    }

    private static int Execute(
        string input,
        CancellationToken? cancellationToken = null,
        int animIndex = 0)
    {
        return PsxAnimDumpCommand.Execute(
            input,
            hexBytes: 256,
            animIndex,
            boneIndex: 0,
            rankBoneIndex: null,
            rankTop: 12,
            verbose: true,
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static byte[] BuildMinimalDirectMatrixPsx()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0x00020004u);
        writer.Write(56u);
        writer.Write(1u);

        writer.Write(0u);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0u);
        writer.Write(0u);

        writer.Write(1u);
        writer.Write(124u);

        writer.Write(0x52454948u); // HIER
        writer.Write(4u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        writer.Write(PsxMeshFile.HierChunkV1Tag);
        writer.Write(0x24u);
        writer.Write(1u);
        writer.Write(0x0Cu);
        writer.Write((ushort)1);
        writer.Write((ushort)0);

        Span<short> matrix =
        [
            4096, 0, 0,
            0, 4096, 0,
            0, 0, 4096
        ];
        foreach (var value in matrix)
            writer.Write(value);
        writer.Write((short)36);
        writer.Write((short)0);
        writer.Write((short)0);

        writer.Write(uint.MaxValue);
        writer.Write(0x12345678u);
        writer.Write(0u);

        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(new byte[12]);
        writer.Write(short.MaxValue);
        writer.Write(ushort.MaxValue);

        // The command requires a post-mesh region before entering its
        // hierarchy diagnostic layers.
        writer.Write(new byte[16]);
        return stream.ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-anim-dump-{Guid.NewGuid():N}");
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
