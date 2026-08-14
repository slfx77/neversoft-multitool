using System.Text.Json;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxMeshDumpCommandTests
{
    [Fact]
    public void Execute_ValidBracketedPaths_WritesSnapshotAndSucceeds()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[good].psx");
        var output = Path.Combine(temp.Path, "[output]", "[dump].json");
        File.WriteAllBytes(input, CreateMinimalPsx());

        var result = PsxMeshDumpCommand.Execute(
            input,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.True(File.Exists(output));
        using var document = JsonDocument.Parse(File.ReadAllBytes(output));
        var root = document.RootElement;
        Assert.Equal("[good].psx", root.GetProperty("FileName").GetString());
        Assert.Equal(4, root.GetProperty("Version").GetInt32());
        Assert.Single(root.GetProperty("Objects").EnumerateArray());
        var mesh = Assert.Single(root.GetProperty("Meshes").EnumerateArray());
        Assert.Equal(0, mesh.GetProperty("VertexCount").GetInt32());
        Assert.Equal(0, mesh.GetProperty("FaceCount").GetInt32());
    }

    [Fact]
    public void Execute_OutputCanonicalAliasCannotOverwriteValidInput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "model.psx");
        var original = CreateMinimalPsx();
        File.WriteAllBytes(input, original);
        var outputAlias = Path.Combine(temp.Path, ".", Path.GetFileName(input));

        Assert.Equal(1, PsxMeshDumpCommand.Execute(
            input,
            outputAlias,
            TestContext.Current.CancellationToken));
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void Execute_InvalidAndMissingBracketedInputs_ReturnFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var invalid = Path.Combine(temp.Path, "[bad].psx");
        var missing = Path.Combine(temp.Path, "[missing].psx");
        var invalidOutput = Path.Combine(temp.Path, "invalid", "dump.json");
        var missingOutput = Path.Combine(temp.Path, "missing", "dump.json");
        File.WriteAllBytes(invalid, "NOPE"u8.ToArray());

        Assert.Equal(1, PsxMeshDumpCommand.Execute(
            invalid,
            invalidOutput,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, PsxMeshDumpCommand.Execute(
            missing,
            missingOutput,
            TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.GetDirectoryName(invalidOutput)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingOutput)));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].psx");
        var output = Path.Combine(temp.Path, "output", "dump.json");
        File.WriteAllBytes(input, CreateMinimalPsx());

        Assert.Throws<OperationCanceledException>(() => PsxMeshDumpCommand.Execute(
            input,
            output,
            new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    private static byte[] CreateMinimalPsx()
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
        writer.Write(68u);

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

        return stream.ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-mesh-dump-{Guid.NewGuid():N}");
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
