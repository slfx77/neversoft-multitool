using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Cas;

namespace NeversoftMultitool.Tests.CLI;

public sealed class CasCommandTests
{
    private static readonly byte[] SyntheticPs2 =
        Convert.FromHexString("02000000000008000100000000000002A4000000");

    private static readonly byte[] SyntheticXbox =
        Convert.FromHexString("020000000000004001000000800000000E0002000D000C00");

    [Fact]
    public void Create_AdvertisesInspectionOnlyTypedCasContract()
    {
        var command = CasCommand.Create();

        Assert.Equal("cas", command.Name);
        Assert.Contains("PS2/Xbox", command.Description, StringComparison.Ordinal);
        Assert.Contains("does not alter geometry", command.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_MixedBracketedDirectory_PreservesSuccessesAndFullSourceNames()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var ps2Path = Path.Combine(input, "[good].cas.ps2");
        var xboxPath = Path.Combine(input, "[good].cas.xbx");
        var badPath = Path.Combine(input, "[bad].cas.ps2");
        File.WriteAllBytes(ps2Path, SyntheticPs2);
        File.WriteAllBytes(xboxPath, SyntheticXbox);
        File.WriteAllBytes(badPath, SyntheticPs2[..^1]);
        File.WriteAllBytes(Path.Combine(input, "ignored.cas.ngc"), SyntheticXbox);
        File.WriteAllBytes(Path.Combine(input, "ignored.cas"), SyntheticPs2);

        var badOutput = Path.Combine(output, "[bad].cas.ps2.json");
        Directory.CreateDirectory(output);
        const string sentinel = "do-not-replace";
        File.WriteAllText(badOutput, sentinel);

        var result = CasCommand.Execute(input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(sentinel, File.ReadAllText(badOutput));
        var ps2Output = Path.Combine(output, "[good].cas.ps2.json");
        var xboxOutput = Path.Combine(output, "[good].cas.xbx.json");
        Assert.True(File.Exists(ps2Output));
        Assert.True(File.Exists(xboxOutput));
        Assert.False(File.Exists(Path.Combine(output, "ignored.cas.ngc.json")));
        Assert.False(File.Exists(Path.Combine(output, "ignored.cas.json")));

        using var ps2Json = JsonDocument.Parse(File.ReadAllText(ps2Output));
        Assert.Equal("ps2", ps2Json.RootElement.GetProperty("platform").GetString());
        Assert.Equal("notApplied",
            ps2Json.RootElement.GetProperty("geometryApplicationStatus").GetString());
        using var xboxJson = JsonDocument.Parse(File.ReadAllText(xboxOutput));
        Assert.Equal("xbox", xboxJson.RootElement.GetProperty("platform").GetString());
        Assert.Equal([14, 12, 13], xboxJson.RootElement.GetProperty("entries")[0]
            .GetProperty("vertexIndices").EnumerateArray().Select(static value => value.GetInt32()));
    }

    [Fact]
    public void Execute_SingleValidTypedFile_ReturnsSuccess()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[single].cas.xbx");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, SyntheticXbox);

        Assert.Equal(0, CasCommand.Execute(input, output, verbose: true, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(output, "[single].cas.xbx.json")));
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
        File.WriteAllBytes(Path.Combine(unsupported, "only.cas.ngc"), SyntheticXbox);
        File.WriteAllBytes(Path.Combine(unsupported, "only.cas"), SyntheticPs2);

        Assert.Equal(1, CasCommand.Execute(missing, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, CasCommand.Execute(empty, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, CasCommand.Execute(unsupported, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, CasCommand.Execute(
            Path.Combine(unsupported, "only.cas.ngc"), output, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_EmptyDirectoryPreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        Assert.Throws<OperationCanceledException>(() =>
            CasCommand.Execute(
                input,
                output,
                verbose: true,
                new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void TryGetPlatform_AcceptsOnlyTypedPs2AndXboxSuffixes()
    {
        Assert.True(CasCommand.TryGetPlatform("thing.CAS.PS2", out var ps2));
        Assert.Equal(CasPolyRemovalPlatform.Ps2, ps2);
        Assert.True(CasCommand.TryGetPlatform("thing.cas.xbx", out var xbox));
        Assert.Equal(CasPolyRemovalPlatform.Xbox, xbox);
        Assert.False(CasCommand.TryGetPlatform("thing.cas", out _));
        Assert.False(CasCommand.TryGetPlatform("thing.cas.ngc", out _));
        Assert.False(CasCommand.TryGetPlatform("thing.cas.xbx.bin", out _));
    }

    [Fact]
    public void GetOutputPath_PreservesCompoundSuffixAndRejectsEscape()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var nested = Path.Combine(input, "nested");
        var output = Path.Combine(temp.Path, "output");
        var file = Path.Combine(nested, "same.cas.ps2");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(file, SyntheticPs2);

        Assert.Equal(Path.Combine(output, "nested", "same.cas.ps2.json"),
            CasCommand.GetOutputPath(input, file, output));

        var outside = Path.Combine(temp.Path, "outside.cas.ps2");
        File.WriteAllBytes(outside, SyntheticPs2);
        Assert.Throws<InvalidOperationException>(() => CasCommand.GetOutputPath(input, outside, output));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-cas-{Guid.NewGuid():N}");
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
