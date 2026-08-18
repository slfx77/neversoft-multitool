using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

/// <summary>
///     Pins the <c>fnt</c> command's contract: two artifacts per font, relative directories
///     preserved so same-named fonts across builds cannot overwrite each other, and files that
///     merely share the <c>.fnt</c> extension reported as skipped rather than as errors.
/// </summary>
public sealed class FntCommandTests
{
    [Fact]
    public void ConvertsOneFontToAnAtlasAndAManifest()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "sample.fnt");
        File.WriteAllBytes(input, BuildFont());
        var output = Path.Combine(temp.Path, "out");

        var exitCode = FntCommand.Execute(input, output, charMap: null, atlasWidth: 512, verbose: true);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(output, "sample.fnt.png")));
        Assert.True(File.Exists(Path.Combine(output, "sample.fnt.json")));
    }

    [Fact]
    public void PreservesRelativeDirectoriesSoSameNamedFontsDoNotCollide()
    {
        using var temp = new TempDirectory();
        var buildA = Path.Combine(temp.Path, "buildA");
        var buildB = Path.Combine(temp.Path, "buildB");
        Directory.CreateDirectory(buildA);
        Directory.CreateDirectory(buildB);
        File.WriteAllBytes(Path.Combine(buildA, "score.fnt"), BuildFont());
        File.WriteAllBytes(Path.Combine(buildB, "score.fnt"), BuildFont());
        var output = Path.Combine(temp.Path, "out");

        var exitCode = FntCommand.Execute(temp.Path, output, charMap: null, atlasWidth: 512, verbose: false);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(output, "buildA", "score.fnt.png")));
        Assert.True(File.Exists(Path.Combine(output, "buildB", "score.fnt.png")));
        Assert.True(File.Exists(Path.Combine(output, "buildA", "score.fnt.json")));
        Assert.True(File.Exists(Path.Combine(output, "buildB", "score.fnt.json")));
    }

    [Fact]
    public void LabelsGlyphsOnlyWhenACharMapModeIsStated()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "sample.fnt");
        File.WriteAllBytes(input, BuildFont());
        var output = Path.Combine(temp.Path, "out");

        Assert.Equal(0, FntCommand.Execute(input, output, charMap: 1, atlasWidth: 512, verbose: false));

        var json = File.ReadAllText(Path.Combine(output, "sample.fnt.json"));
        Assert.Contains("\"characterMapStatus\": \"appliedFromCallerArgument\"", json, StringComparison.Ordinal);
        Assert.Contains("\"characterMapMode\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"character\": \"0\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesThatOnlyShareTheExtensionAreSkippedNotFailed()
    {
        // THAW and THPS3-PS2 both ship unrelated formats under .fnt, so pointing the command at
        // one of those trees must not read as a wall of conversion errors.
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "other.fnt"), "this is not a Neversoft font"u8.ToArray());
        var output = Path.Combine(temp.Path, "out");

        var exitCode = FntCommand.Execute(temp.Path, output, charMap: null, atlasWidth: 512, verbose: false);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void AValidFontStillConvertsWhenAnotherFileInTheBatchIsSkipped()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "[good].fnt"), BuildFont());
        File.WriteAllBytes(Path.Combine(temp.Path, "other.fnt"), "not a font"u8.ToArray());
        var output = Path.Combine(temp.Path, "out");

        var exitCode = FntCommand.Execute(temp.Path, output, charMap: null, atlasWidth: 512, verbose: true);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(output, "[good].fnt.png")));
    }

    [Fact]
    public void MissingInputFails()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");

        var exitCode = FntCommand.Execute(
            Path.Combine(temp.Path, "absent.fnt"), output, charMap: null, atlasWidth: 512, verbose: false);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnsupportedCharMapModeFails(int charMap)
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "sample.fnt");
        File.WriteAllBytes(input, BuildFont());
        var output = Path.Combine(temp.Path, "out");

        Assert.Equal(1, FntCommand.Execute(input, output, charMap, atlasWidth: 512, verbose: false));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeAtlasWidthFails(int atlasWidth)
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "sample.fnt");
        File.WriteAllBytes(input, BuildFont());
        var output = Path.Combine(temp.Path, "out");

        Assert.Equal(1, FntCommand.Execute(input, output, charMap: null, atlasWidth, verbose: false));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void EmptyDirectorySucceedsWithoutWritingAnything()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");

        Assert.Equal(0, FntCommand.Execute(temp.Path, output, charMap: null, atlasWidth: 512, verbose: false));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void CancellationPropagatesWithoutWritingAnything()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "sample.fnt"), BuildFont());
        var output = Path.Combine(temp.Path, "out");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FntCommand.Execute(temp.Path, output, charMap: null, atlasWidth: 512, verbose: false, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void OutputPathsStayInsideTheOutputRoot()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FntCommand.GetOutputTarget(Path.GetTempPath(), Path.Combine("C:", "elsewhere", "a.fnt"), "out"));
    }

    /// <summary>An eleven-glyph paletted font: the shape a numeric CharMap 1 font has.</summary>
    private static byte[] BuildFont()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        const int glyphCount = 11;
        writer.Write(glyphCount);
        for (var i = 0; i < glyphCount; i++)
        {
            writer.Write(2); // widthUnits -> 8 pixels
            writer.Write(3); // height
            writer.Write(3); // baseline
            writer.Write(9); // advance width
        }

        for (var i = 0; i < 15; i++)
            writer.Write((ushort)(0x8000 | (i * 2) | ((i * 2) << 5) | ((i * 2) << 10)));
        writer.Write((ushort)0x0000);

        for (var glyph = 0; glyph < glyphCount; glyph++)
        for (var i = 0; i < 12; i++)
            writer.Write((byte)0x21);

        writer.Flush();
        return stream.ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-fnt-{Guid.NewGuid():N}");
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
