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

    private static int Execute(string input, CancellationToken? cancellationToken = null)
    {
        return PsxAnimDumpCommand.Execute(
            input,
            hexBytes: 256,
            animIndex: 0,
            boneIndex: 0,
            rankBoneIndex: null,
            rankTop: 12,
            verbose: true,
            cancellationToken ?? TestContext.Current.CancellationToken);
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
