using System.Text.Json;
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
            .SelectMany(static build => Directory.EnumerateFiles(
                build, "*", SearchOption.AllDirectories))
            .Where(static file => Path.GetExtension(file)
                .Equals(".trg", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
    public void Parse_RejectsNodeCountLargerThanRemainingOffsetTable()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x4752545Fu);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write(1_000_000u);
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        var error = Assert.Throws<InvalidDataException>(() => TrgFile.Parse(reader));

        Assert.Contains("node table", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDecreasingNodeOffsets()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x4752545Fu);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write(2u);
            writer.Write(24u);
            writer.Write(20u);
            writer.Write(new byte[12]);
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        var error = Assert.Throws<InvalidDataException>(() => TrgFile.Parse(reader));

        Assert.Contains("decreases", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AllowsAdjacentNodeIdsToAliasTheSamePayload()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x4752545Fu);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write(2u);
            writer.Write(20u);
            writer.Write(20u);
            writer.Write((ushort)255); // shared TERMINATOR payload
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        var trg = TrgFile.Parse(reader);

        Assert.Equal(2, trg.NodeCount);
        Assert.Equal([255, 255], trg.Nodes.Select(static node => node.TypeId));
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x1001)]
    public void Parse_BaddyRetainsUnalignedRuntimeFlagsAndExactPlacementValues(
        int priority)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x4752545Fu);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write(1u);
            writer.Write(16u);

            writer.Write((ushort)TrgNodeMetadata.TypeBaddy);
            writer.Write((ushort)0x192);
            writer.Write((ushort)priority);
            writer.Write((ushort)1); // odd link count: flags begin unaligned
            writer.Write((ushort)77);
            writer.Write(new byte[] { 0, 2, 5, byte.MaxValue });
            writer.Write((ushort)0); // align position to four bytes
            writer.Write(-14_571);
            writer.Write(-5_954);
            writer.Write(-246);
            writer.Write((short)0);
            writer.Write((short)3_243);
            writer.Write((short)0);
            writer.Write((ushort)0x212F);
            writer.Write(0x12345678u);
            writer.Write((ushort)0x4100);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var trg = TrgFile.Parse(reader, "synthetic_t.trg");

        var node = Assert.Single(trg.Nodes);
        Assert.Equal([77], node.Links);
        Assert.Equal([0, 2, 5], node.BaddyFlags);
        Assert.Equal(-14_571, node.Position!.RawX);
        Assert.Equal(-5_954, node.Position.RawY);
        Assert.Equal(-246, node.Position.RawZ);
        Assert.Equal(3_243, node.Angles!.RawY);
        Assert.Equal("0x12345678", Assert.Single(
            node.Script!, static op => op.Opcode == "0x212F").Value);
    }

    [Fact]
    public void ParseCommandList_SetVisibilityByName_ConsumesSuffixRangeAndVisibility()
    {
        byte[] bytes =
        [
            0xBF, 0x00,
            (byte)'K', (byte)'e', (byte)'v', (byte)'i', (byte)'n', (byte)'_', 0x00, 0x00,
            0x00, 0x00, 0x05, 0x00, 0x00, 0x00,
            0xFF, 0xFF
        ];
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);

        var command = Assert.Single(TrgCommandList.ParseCommandList(reader, bytes.Length));

        Assert.Equal(0xBF, command.Opcode);
        Assert.Equal("SetVisibilityByName", command.Name);
        Assert.Equal(["Kevin_", (ushort)0, (ushort)5, (ushort)0], command.Args);
    }

    [Fact]
    public void ParseScript_SpatialIfConsumesBothSignedOperands()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0x4118);
            writer.Write((short)-321);
            writer.Write((short)654);
            writer.Write((ushort)0x212F);
            writer.Write(0x12345678u);
            writer.Write((ushort)0x4120);
            writer.Write((ushort)0x4100);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var ops = TrgCommandList.ParseScript(reader, checked((int)stream.Length));

        Assert.Equal(["0x4118", "0x212F", "0x4120", "0x4100"],
            ops.Select(static op => op.Opcode));
        var operands = Assert.IsType<object[]>(ops[0].Value);
        Assert.Equal((short)-321, Assert.IsType<short>(operands[0]));
        Assert.Equal((short)654, Assert.IsType<short>(operands[1]));
        Assert.Equal("0x12345678", ops[1].Value);
    }

    [Fact]
    public void Parse_SpiderManL8a4_ReadsDefaultKevinVisibilityRange()
    {
        var file = paths.FindSampleFile("Spider-Man (2000-9-1, PSX - Final)", "l8a4_t.trg");
        Assert.SkipWhen(file == null, "Spider-Man l8a4_t.trg not found");

        var trg = TrgFile.Parse(file!);
        var restart = Assert.Single(trg.Nodes, static node => node.Type == "RESTART");
        var command = Assert.Single(
            restart.Commands!,
            static item => item.Opcode == 0xBF
                           && item.Args is { Count: > 0 }
                           && Equals(item.Args[0], "Kevin_"));

        Assert.Equal(["Kevin_", (ushort)0, (ushort)5, (ushort)0], command.Args);
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

    [CorpusFact]
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
        var tempFile = Path.Combine(Path.GetTempPath(), $"trg_test_{Guid.NewGuid():N}.tmp");
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
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }
}
