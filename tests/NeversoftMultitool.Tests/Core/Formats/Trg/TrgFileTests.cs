using NeversoftMultitool.Core.Formats.Trg;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Trg;

public class TrgFileTests(TestPaths paths)
{
    private string? FindTrgFile(string buildPattern, string fileName)
    {
        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains(buildPattern, StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;
        var trgDir = Path.Combine(buildDir, "TRG");
        if (!Directory.Exists(trgDir)) return null;
        return Directory.GetFiles(trgDir)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private string[] GetAllTrgFiles()
    {
        if (!paths.HasSampleBuilds) return [];
        return Directory.GetDirectories(paths.SampleBuildsDir!)
            .SelectMany(build =>
            {
                var trgDir = Path.Combine(build, "TRG");
                return Directory.Exists(trgDir)
                    ? Directory.GetFiles(trgDir, "*.trg", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(trgDir, "*.TRG", SearchOption.TopDirectoryOnly))
                    : [];
            })
            .Distinct()
            .ToArray();
    }

    [Fact]
    public void Parse_ApocalypseCityT_HasExpectedNodeCount()
    {
        var file = FindTrgFile("Apocalypse", "city_t.trg");
        Assert.SkipWhen(file == null, "Apocalypse city_t.trg not found");

        var trg = TrgFile.Parse(file!);

        Assert.Equal(2, trg.VersionMajor);
        Assert.Equal(0, trg.VersionMinor);
        Assert.True(trg.NodeCount > 100, $"Expected >100 nodes, got {trg.NodeCount}");
        Assert.Equal(trg.NodeCount, trg.Nodes.Count);
    }

    [Fact]
    public void Parse_ApocalypseDeathT_SmallFileParsesCorrectly()
    {
        var file = FindTrgFile("Apocalypse", "death_t.trg");
        Assert.SkipWhen(file == null, "Apocalypse death_t.trg not found");

        var trg = TrgFile.Parse(file!);

        Assert.Equal(2, trg.VersionMajor);
        Assert.Equal(0, trg.VersionMinor);
        Assert.True(trg.NodeCount > 0);
        // Last node should always be TERMINATOR
        Assert.Equal("TERMINATOR", trg.Nodes[^1].Type);
        Assert.Equal(255, trg.Nodes[^1].TypeId);
    }

    [Fact]
    public void Parse_SpiderManV21_ParsesCorrectVersion()
    {
        // Try any Spider-Man build
        var file = FindTrgFile("Spider-Man (2000-9-1", "l1a1_t.trg");
        file ??= FindTrgFile("Spider-Man (2000-2-18", "l1a1_t.trg");
        Assert.SkipWhen(file == null, "Spider-Man l1a1_t.trg not found");

        var trg = TrgFile.Parse(file!);

        Assert.Equal(2, trg.VersionMajor);
        Assert.Equal(1, trg.VersionMinor);
        Assert.True(trg.NodeCount > 0);
        Assert.Equal("TERMINATOR", trg.Nodes[^1].Type);
    }

    [Fact]
    public void Parse_RestartNodesHaveNamesAndPositions()
    {
        var file = FindTrgFile("Apocalypse", "city_t.trg");
        Assert.SkipWhen(file == null, "Apocalypse city_t.trg not found");

        var trg = TrgFile.Parse(file!);
        var restarts = trg.Nodes.Where(n => n.Type == "RESTART").ToList();

        Assert.NotEmpty(restarts);
        foreach (var r in restarts)
        {
            Assert.NotNull(r.Name);
            Assert.NotEmpty(r.Name);
            Assert.NotNull(r.Position);
        }
    }

    [Fact]
    public void Parse_RailPointsHavePositions()
    {
        // THPS builds have the most rail points
        string? file = null;
        foreach (var pattern in new[] { "Tony Hawk's Pro Skater (1999-9-29", "Tony Hawk's Pro Skater 2 (2000-9-19" })
        {
            if (!paths.HasSampleBuilds) break;
            var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
                .FirstOrDefault(d => Path.GetFileName(d).Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (buildDir == null) continue;
            var trgDir = Path.Combine(buildDir, "TRG");
            if (!Directory.Exists(trgDir)) continue;
            file = Directory.GetFiles(trgDir).FirstOrDefault(f =>
                Path.GetExtension(f).Equals(".trg", StringComparison.OrdinalIgnoreCase));
            if (file != null) break;
        }
        Assert.SkipWhen(file == null, "No THPS TRG files found");

        var trg = TrgFile.Parse(file!);
        var rails = trg.Nodes.Where(n => n.Type is "RAILDEF" or "RAILPOINT").ToList();

        Assert.NotEmpty(rails);
        foreach (var r in rails)
        {
            Assert.NotNull(r.Position);
        }
    }

    [Fact]
    public void Parse_AllTrgFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllTrgFiles();
        Assert.SkipWhen(files.Length == 0, "No TRG files found");

        var errors = new List<string>();
        var parsed = 0;

        foreach (var file in files)
        {
            try
            {
                var trg = TrgFile.Parse(file);
                Assert.True(trg.NodeCount > 0);
                Assert.Equal(trg.NodeCount, trg.Nodes.Count);
                // Every file should end with TERMINATOR
                Assert.Equal(255, trg.Nodes[^1].TypeId);
                parsed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed to parse {errors.Count}/{files.Length} files:\n{string.Join("\n", errors)}");
        Assert.True(parsed > 0, "No files were parsed");
    }

    [Fact]
    public void Parse_InvalidMagic_ThrowsInvalidDataException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);
            Assert.Throws<InvalidDataException>(() => TrgFile.Parse(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var file = FindTrgFile("Apocalypse", "death_t.trg");
        Assert.SkipWhen(file == null, "Apocalypse death_t.trg not found");

        var trg = TrgFile.Parse(file!);
        var json = trg.ToJson();

        Assert.NotEmpty(json);
        Assert.Contains("\"nodeCount\"", json);
        Assert.Contains("\"nodes\"", json);
        Assert.Contains("\"versionMajor\"", json);
        // Verify it's valid JSON by parsing it
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }
}
