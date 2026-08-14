using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class BlendModelExporterOutputTests
{
    [Fact]
    public void Export_HelperProducesNoOutput_ThrowsAndCleansStage()
    {
        using var temp = new TempDirectory();
        var fixture = CreateFixture(temp.Path);
        string? stagedPath = null;
        var exporter = new BlendModelExporter((_, _, _, path, _) =>
        {
            stagedPath = path;
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            exporter.Export(fixture.Document, fixture.Request));

        Assert.Equal(
            "Blender export helper completed successfully but did not produce a non-empty .blend file.",
            ex.Message);
        Assert.False(File.Exists(fixture.OutputPath));
        AssertStageWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Fact]
    public void Export_HelperProducesNoOutput_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        var fixture = CreateFixture(temp.Path);
        var original = new byte[] { 0x42, 0x4C, 0x45, 0x4E, 0x44 };
        File.WriteAllBytes(fixture.OutputPath, original);
        string? stagedPath = null;
        var exporter = new BlendModelExporter((_, _, _, path, _) =>
        {
            stagedPath = path;
        });

        Assert.Throws<InvalidOperationException>(() =>
            exporter.Export(fixture.Document, fixture.Request));

        Assert.Equal(original, File.ReadAllBytes(fixture.OutputPath));
        AssertStageWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Fact]
    public void Export_HelperProducesEmptyOutput_PreservesExistingDestinationAndCleansStage()
    {
        using var temp = new TempDirectory();
        var fixture = CreateFixture(temp.Path);
        var original = new byte[] { 0x42, 0x4C, 0x45, 0x4E, 0x44 };
        File.WriteAllBytes(fixture.OutputPath, original);
        string? stagedPath = null;
        var exporter = new BlendModelExporter((_, _, _, path, _) =>
        {
            stagedPath = path;
            File.WriteAllBytes(path, []);
        });

        Assert.Throws<InvalidOperationException>(() =>
            exporter.Export(fixture.Document, fixture.Request));

        Assert.Equal(original, File.ReadAllBytes(fixture.OutputPath));
        AssertStageWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Fact]
    public void Export_HelperProducesDirectory_PreservesExistingDestinationAndCleansStage()
    {
        using var temp = new TempDirectory();
        var fixture = CreateFixture(temp.Path);
        var original = new byte[] { 0x42, 0x4C, 0x45, 0x4E, 0x44 };
        File.WriteAllBytes(fixture.OutputPath, original);
        string? stagedPath = null;
        var exporter = new BlendModelExporter((_, _, _, path, _) =>
        {
            stagedPath = path;
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "unexpected.bin"), [0x01]);
        });

        Assert.Throws<InvalidOperationException>(() =>
            exporter.Export(fixture.Document, fixture.Request));

        Assert.Equal(original, File.ReadAllBytes(fixture.OutputPath));
        AssertStageWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Fact]
    public void Export_HelperProducesNonEmptyOutput_ReplacesDestinationAndReportsIt()
    {
        using var temp = new TempDirectory();
        var fixture = CreateFixture(temp.Path);
        File.WriteAllBytes(fixture.OutputPath, [0x4F, 0x4C, 0x44]);
        var replacement = new byte[] { 0x42, 0x4C, 0x45, 0x4E, 0x44, 0x45, 0x52 };
        string? stagedPath = null;
        var exporter = new BlendModelExporter((_, _, _, path, _) =>
        {
            stagedPath = path;
            File.WriteAllBytes(path, replacement);
        });

        var result = exporter.Export(fixture.Document, fixture.Request);

        Assert.Equal(fixture.OutputPath, Assert.Single(result.OutputPaths));
        Assert.Equal(replacement, File.ReadAllBytes(fixture.OutputPath));
        AssertStageWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    private static ExportFixture CreateFixture(string root)
    {
        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "BlenderExporter",
            "import_package.py");
        Assert.True(File.Exists(scriptPath), $"Expected copied Blender export script at {scriptPath}.");

        var helperPath = Path.Combine(root, "dummy-blender");
        File.WriteAllBytes(helperPath, [0x00]);
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "synthetic.blend");
        var document = new ModelDocument
        {
            Name = "synthetic",
            TriangleCount = 1
        };
        var request = new MeshExportRequest
        {
            OutputDirectory = outputDirectory,
            OutputStem = "synthetic",
            Format = MeshOutputFormat.Blend,
            BlenderHelperPath = helperPath
        };

        return new ExportFixture(document, request, outputDirectory, outputPath);
    }

    private static void AssertStageWasCleaned(string? stagedPath, string outputDirectory)
    {
        Assert.NotNull(stagedPath);
        Assert.Equal(outputDirectory, Path.GetDirectoryName(stagedPath));
        var leaf = Path.GetFileName(stagedPath);
        const string suffix = ".tmp.blend";
        Assert.StartsWith(".", leaf, StringComparison.Ordinal);
        Assert.EndsWith(suffix, leaf, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(leaf[1..^suffix.Length], "N", out _));
        Assert.False(File.Exists(stagedPath));
        Assert.Empty(
            Directory.EnumerateFileSystemEntries(outputDirectory)
                .Where(path => Path.GetFileName(path).EndsWith(".tmp.blend", StringComparison.Ordinal)));
    }

    private sealed record ExportFixture(
        ModelDocument Document,
        MeshExportRequest Request,
        string OutputDirectory,
        string OutputPath);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtBlendOutput_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
