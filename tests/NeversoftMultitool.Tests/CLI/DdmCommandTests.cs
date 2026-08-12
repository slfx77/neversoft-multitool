using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.CLI;

public sealed class DdmCommandTests
{
    [Fact]
    public void Execute_MissingAndEmptyDirectories_PreserveExitContracts()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, Execute(missing, missingOutput));
        Assert.Equal(0, Execute(empty, emptyOutput));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_MixedBracketedBatch_PreservesValidArtifactAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        var textures = Path.Combine(temp.Path, "[textures]");
        var ddx = Path.Combine(temp.Path, "[ddx]");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(textures);
        Directory.CreateDirectory(ddx);
        File.WriteAllBytes(Path.Combine(input, "[good].ddm"), CreateOneTriangleDdm());
        File.WriteAllBytes(Path.Combine(input, "[bad].ddm"), "NOPE"u8.ToArray());

        var result = Execute(input, output, textures, verbose: true, ddxPath: ddx);

        Assert.Equal(1, result);
        var goodOutput = Path.Combine(output, "[good].glb");
        AssertGlb(goodOutput);
        Assert.False(File.Exists(Path.Combine(output, "[bad].glb")));
        Assert.Equal(goodOutput, Assert.Single(Directory.EnumerateFiles(output)));
    }

    [Fact]
    public void Execute_EmptyAcceptedDdm_ReturnsFailureWithoutArtifact()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[empty].ddm"), CreateEmptyDdm());

        Assert.Equal(1, Execute(input, output, verbose: true));
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void Execute_ExplicitPsxFile_IsAcceptedAsACompanionPath()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        var psx = Path.Combine(temp.Path, "[layout].psx");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "level.ddm"), CreateOneTriangleDdm());
        File.WriteAllBytes(psx, "NOPE"u8.ToArray());

        Assert.Equal(0, Execute(input, output, psxPath: psx));
        AssertGlb(Path.Combine(output, "level.glb"));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "level.ddm"), CreateOneTriangleDdm());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Execute(input, output, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ExecuteAll_MissingEmptyAndPreCancelled_PreserveContracts()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        var cancelledOutput = Path.Combine(temp.Path, "cancelled-output");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, ExecuteAll(missing, missingOutput));
        Assert.Equal(0, ExecuteAll(empty, emptyOutput));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ExecuteAll(empty, cancelledOutput, cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(cancelledOutput));
    }

    [Fact]
    public void ExecuteAll_MixedLevels_PreservesValidArtifactAndAggregatesFailure()
    {
        using var temp = new TempDirectory();
        var parent = Path.Combine(temp.Path, "parent");
        var goodLevel = Path.Combine(parent, "[good-level]");
        var badLevel = Path.Combine(parent, "[bad-level]");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(goodLevel);
        Directory.CreateDirectory(badLevel);
        File.WriteAllBytes(Path.Combine(goodLevel, "good.ddm"), CreateOneTriangleDdm());
        File.WriteAllBytes(Path.Combine(badLevel, "bad.ddm"), "NOPE"u8.ToArray());

        Assert.Equal(1, ExecuteAll(parent, output, verbose: true));
        AssertGlb(Path.Combine(output, "[good-level]", "good.glb"));
        var badOutput = Path.Combine(output, "[bad-level]");
        Assert.True(Directory.Exists(badOutput));
        Assert.Empty(Directory.EnumerateFileSystemEntries(badOutput));
    }

    [Fact]
    public void ExecuteAll_ForwardsExplicitDdxDirectoryToEachLevel()
    {
        using var temp = new TempDirectory();
        var parent = Path.Combine(temp.Path, "parent");
        var firstLevel = Path.Combine(parent, "first");
        var secondLevel = Path.Combine(parent, "second");
        var output = Path.Combine(temp.Path, "output");
        var ddx = Path.Combine(temp.Path, "[ddx]");
        Directory.CreateDirectory(firstLevel);
        Directory.CreateDirectory(secondLevel);
        Directory.CreateDirectory(ddx);
        File.WriteAllBytes(Path.Combine(firstLevel, "first.ddm"), CreateOneTriangleDdm());
        File.WriteAllBytes(Path.Combine(secondLevel, "second.ddm"), CreateOneTriangleDdm());
        File.WriteAllBytes(Path.Combine(ddx, "first.ddx"), "NOPE"u8.ToArray());
        File.WriteAllBytes(Path.Combine(ddx, "second.ddx"), "NOPE"u8.ToArray());

        var result = DdmCommand.Create()
            .Parse([parent, "--all", "--output", output, "--ddx", ddx])
            .Invoke();

        Assert.Equal(1, result);
        foreach (var levelName in new[] { "first", "second" })
        {
            var levelOutput = Path.Combine(output, levelName);
            Assert.True(Directory.Exists(levelOutput));
            Assert.Empty(Directory.EnumerateFileSystemEntries(levelOutput));
        }
    }

    private static int Execute(
        string input,
        string output,
        string? textures = null,
        bool verbose = false,
        string? ddxPath = null,
        string? psxPath = null,
        CancellationToken? cancellationToken = null)
    {
        return DdmCommand.Execute(
            input,
            output,
            textures,
            verbose,
            ddxPath,
            psxPath,
            MeshOutputFormat.Glb,
            blenderHelperPath: null,
            cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static int ExecuteAll(
        string parent,
        string output,
        string? textures = null,
        bool verbose = false,
        string? ddxPath = null,
        string? psxPath = null,
        CancellationToken? cancellationToken = null)
    {
        return DdmCommand.ExecuteAll(
            parent,
            output,
            textures,
            verbose,
            ddxPath,
            psxPath,
            MeshOutputFormat.Glb,
            blenderHelperPath: null,
            cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static byte[] CreateEmptyDdm()
    {
        var data = new byte[12];
        data[0] = 1;
        return data;
    }

    private static byte[] CreateOneTriangleDdm()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(1u);
        writer.Write(416u);
        writer.Write(1u);
        writer.Write(20u);
        writer.Write(408u);

        writer.Write(0u);
        writer.Write(0x12345678u);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0u);
        writer.Write(0u);
        WriteFixedAscii(writer, "triangle", 64);
        for (var i = 0; i < 7; i++)
            writer.Write(0f);
        writer.Write(1u);
        writer.Write(3u);
        writer.Write(3u);
        writer.Write(1u);

        WriteFixedAscii(writer, "mat", 64);
        WriteFixedAscii(writer, "No_Texture_Map", 64);
        writer.Write(0u);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0u);

        WriteVertex(writer, 0f, 0f, 0f, 0f, 0f);
        WriteVertex(writer, 1f, 0f, 0f, 1f, 0f);
        WriteVertex(writer, 0f, 1f, 0f, 0f, 1f);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)3);
        writer.Flush();

        var data = stream.ToArray();
        Assert.Equal(428, data.Length);
        return data;
    }

    private static void WriteVertex(
        BinaryWriter writer,
        float x,
        float y,
        float z,
        float u,
        float v)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write(u);
        writer.Write(v);
    }

    private static void WriteFixedAscii(BinaryWriter writer, string value, int byteLength)
    {
        var bytes = new byte[byteLength];
        Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
        writer.Write(bytes);
    }

    private static void AssertGlb(string path)
    {
        Assert.True(File.Exists(path), $"Expected GLB at {path}");
        Assert.Equal("glTF"u8.ToArray(), File.ReadAllBytes(path)[..4]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-ddm-command-{Guid.NewGuid():N}");
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
