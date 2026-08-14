using System.Buffers.Binary;
using System.Text.Json;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class TrgCommandTests
{
    [Fact]
    public void Execute_MixedBracketedDirectory_WritesValidSiblingAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        var goodPath = Path.Combine(input, "[good].trg");
        var badPath = Path.Combine(input, "[bad].trg");
        var goodJsonPath = Path.Combine(output, "[good].json");
        var badJsonPath = Path.Combine(output, "[bad].json");
        Directory.CreateDirectory(input);

        var good = BuildTerminatorTrg();
        var bad = (byte[])good.Clone();
        bad[0] ^= 1;
        File.WriteAllBytes(goodPath, good);
        File.WriteAllBytes(badPath, bad);

        var result = TrgCommand.Execute(input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(goodJsonPath, Assert.Single(Directory.EnumerateFiles(output, "*.json")));
        Assert.False(File.Exists(badJsonPath));

        using var json = JsonDocument.Parse(File.ReadAllText(goodJsonPath));
        var root = json.RootElement;
        Assert.Equal("[good].trg", root.GetProperty("fileName").GetString());
        Assert.Equal(2, root.GetProperty("versionMajor").GetInt32());
        Assert.Equal(1, root.GetProperty("versionMinor").GetInt32());
        Assert.Equal(1, root.GetProperty("nodeCount").GetInt32());
        var node = Assert.Single(root.GetProperty("nodes").EnumerateArray());
        Assert.Equal(255, node.GetProperty("typeId").GetInt32());
        Assert.Equal("TERMINATOR", node.GetProperty("type").GetString());
    }

    [Fact]
    public void Execute_MissingBracketedPathFailsAndEmptyDirectorySucceeds()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, TrgCommand.Execute(
            missing, missingOutput, verbose: true, CancellationToken.None));
        Assert.Equal(0, TrgCommand.Execute(
            empty, emptyOutput, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_NestedSameDirectoryPlatformVariants_GetDistinctOwnedOutputs()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var nestedInput = Path.Combine(input, "scripts");
        var output = Path.Combine(temp.Path, "output");
        var nestedOutput = Path.Combine(output, "scripts");
        Directory.CreateDirectory(nestedInput);
        File.WriteAllBytes(Path.Combine(nestedInput, "level.trg.n64"), BuildTerminatorTrg());
        File.WriteAllBytes(Path.Combine(nestedInput, "level.trg.ps2"), BuildTerminatorTrg());

        var result = TrgCommand.Execute(
            input, output, verbose: true, CancellationToken.None);

        Assert.Equal(0, result);
        var naturalPath = Path.Combine(nestedOutput, "level.trg.json");
        var suffixedPath = Path.Combine(nestedOutput, "level.trg_2.json");
        Assert.Equal(
            [naturalPath, suffixedPath],
            Directory.EnumerateFiles(output, "*.json", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray());

        using var naturalJson = JsonDocument.Parse(File.ReadAllText(naturalPath));
        Assert.Equal(
            "level.trg.n64",
            naturalJson.RootElement.GetProperty("fileName").GetString());
        using var suffixedJson = JsonDocument.Parse(File.ReadAllText(suffixedPath));
        Assert.Equal(
            "level.trg.ps2",
            suffixedJson.RootElement.GetProperty("fileName").GetString());
    }

    private static byte[] BuildTerminatorTrg()
    {
        var data = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x4752545F);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x00010002);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 255);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-trg-{Guid.NewGuid():N}");
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
