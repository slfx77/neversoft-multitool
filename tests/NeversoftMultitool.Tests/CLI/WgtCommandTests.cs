using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Wgt;

namespace NeversoftMultitool.Tests.CLI;

public sealed class WgtCommandTests
{
    private static readonly byte[] SyntheticV1 = Convert.FromHexString(
        "0100000002000000" +
        "0000803F00000000000000000000803E0000403F00000000" +
        "1D00FF1E1F00");

    private static readonly byte[] UnsupportedV2 = Convert.FromHexString("0200000000000000");

    [Fact]
    public void Create_AdvertisesInspectionOnlyCompiledV1Contract()
    {
        var command = WgtCommand.Create();

        Assert.Equal("wgt", command.Name);
        Assert.Contains("PS2/Xbox", command.Description, StringComparison.Ordinal);
        Assert.Contains("v1", command.Description, StringComparison.Ordinal);
        Assert.Contains("does not alter geometry", command.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_MixedBracketedDirectory_PreservesSuccessesAndFullSourceNames()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var ps2Path = Path.Combine(input, "[good].wgt.ps2");
        var xboxPath = Path.Combine(input, "[good].wgt.xbx");
        var badPath = Path.Combine(input, "[bad].wgt.ps2");
        File.WriteAllBytes(ps2Path, SyntheticV1);
        File.WriteAllBytes(xboxPath, SyntheticV1);
        File.WriteAllBytes(badPath, UnsupportedV2);
        File.WriteAllBytes(Path.Combine(input, "ignored.wgt.ngc"), SyntheticV1);
        File.WriteAllBytes(Path.Combine(input, "ignored.wgt"), SyntheticV1);

        var badOutput = Path.Combine(output, "[bad].wgt.ps2.json");
        Directory.CreateDirectory(output);
        const string sentinel = "do-not-replace";
        File.WriteAllText(badOutput, sentinel);

        var result = WgtCommand.Execute(input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(sentinel, File.ReadAllText(badOutput));
        var ps2Output = Path.Combine(output, "[good].wgt.ps2.json");
        var xboxOutput = Path.Combine(output, "[good].wgt.xbx.json");
        Assert.True(File.Exists(ps2Output));
        Assert.True(File.Exists(xboxOutput));
        Assert.False(File.Exists(Path.Combine(output, "ignored.wgt.ngc.json")));
        Assert.False(File.Exists(Path.Combine(output, "ignored.wgt.json")));

        using var ps2Json = JsonDocument.Parse(File.ReadAllText(ps2Output));
        Assert.Equal("ps2", ps2Json.RootElement.GetProperty("platform").GetString());
        Assert.Equal("notApplied",
            ps2Json.RootElement.GetProperty("geometryApplicationStatus").GetString());
        Assert.Equal([29, 0, -1], ps2Json.RootElement.GetProperty("vertices")[0]
            .GetProperty("boneIndices").EnumerateArray().Select(static value => value.GetInt32()));
        using var xboxJson = JsonDocument.Parse(File.ReadAllText(xboxOutput));
        Assert.Equal("xbox", xboxJson.RootElement.GetProperty("platform").GetString());
        Assert.Equal(2, xboxJson.RootElement.GetProperty("vertexCount").GetInt32());
    }

    [Fact]
    public void Execute_SingleValidTypedFile_ReturnsSuccess()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[single].wgt.xbx");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, SyntheticV1);

        Assert.Equal(0, WgtCommand.Execute(input, output, verbose: true, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(output, "[single].wgt.xbx.json")));
    }

    [Fact]
    public void Execute_MissingOrNoSupportedInput_ReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var unsupported = Path.Combine(temp.Path, "unsupported");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(unsupported);
        File.WriteAllBytes(Path.Combine(unsupported, "only.wgt.ngc"), SyntheticV1);
        File.WriteAllBytes(Path.Combine(unsupported, "only.wgt"), SyntheticV1);

        Assert.Equal(1, WgtCommand.Execute(missing, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, WgtCommand.Execute(empty, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, WgtCommand.Execute(unsupported, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, WgtCommand.Execute(
            Path.Combine(unsupported, "only.wgt.ngc"), output, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutWritingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "cancel.wgt.ps2");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, SyntheticV1);

        Assert.Throws<OperationCanceledException>(() =>
            WgtCommand.Execute(input, output, verbose: true, new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void TryGetPlatform_AcceptsOnlyTypedPs2AndXboxSuffixes()
    {
        Assert.True(WgtCommand.TryGetPlatform("thing.WGT.PS2", out var ps2));
        Assert.Equal(CutsceneWeightMapPlatform.Ps2, ps2);
        Assert.True(WgtCommand.TryGetPlatform("thing.wgt.xbx", out var xbox));
        Assert.Equal(CutsceneWeightMapPlatform.Xbox, xbox);
        Assert.False(WgtCommand.TryGetPlatform("thing.wgt", out _));
        Assert.False(WgtCommand.TryGetPlatform("thing.wgt.ngc", out _));
        Assert.False(WgtCommand.TryGetPlatform("thing.wgt.xbx.bin", out _));
    }

    [Fact]
    public void GetOutputPath_PreservesCompoundSuffixAndRejectsEscape()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var nested = Path.Combine(input, "nested");
        var output = Path.Combine(temp.Path, "output");
        var file = Path.Combine(nested, "same.wgt.ps2");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(file, SyntheticV1);

        Assert.Equal(Path.Combine(output, "nested", "same.wgt.ps2.json"),
            WgtCommand.GetOutputPath(input, file, output));

        var outside = Path.Combine(temp.Path, "outside.wgt.ps2");
        File.WriteAllBytes(outside, SyntheticV1);
        Assert.Throws<InvalidOperationException>(() => WgtCommand.GetOutputPath(input, outside, output));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-wgt-{Guid.NewGuid():N}");
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
