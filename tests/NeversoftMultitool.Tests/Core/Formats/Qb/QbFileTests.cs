using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

public class QbFileTests(TestPaths paths)
{
    private string[] GetAllQbFiles()
    {
        if (!paths.HasSampleBuilds) return [];
        return Directory.GetDirectories(paths.SampleBuildsDir!)
            .SelectMany(build =>
            {
                var qbDir = Path.Combine(build, "QB");
                return Directory.Exists(qbDir)
                    ? Directory.GetFiles(qbDir, "*.qb", SearchOption.AllDirectories)
                    : [];
            })
            .ToArray();
    }

    private string? FindQbFile(string buildPattern, string fileName)
    {
        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains(buildPattern, StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;
        var qbDir = Path.Combine(buildDir, "QB");
        if (!Directory.Exists(qbDir)) return null;
        return Directory.GetFiles(qbDir, "*.qb", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AllQbFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllQbFiles();
        Assert.SkipWhen(files.Length == 0, "No QB files found");

        var errors = new List<string>();
        var parsed = 0;
        var totalScripts = 0;
        var totalGlobals = 0;

        foreach (var file in files)
        {
            try
            {
                var qb = QbFile.Parse(file);
                Assert.NotNull(qb.Tokens);
                Assert.True(qb.Tokens.Count > 0, $"{Path.GetFileName(file)}: empty token list");
                parsed++;
                totalScripts += qb.ScriptCount;
                totalGlobals += qb.GlobalCount;
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
    public void Parse_ChecksumName_PopulatesLocalNames()
    {
        // airtricks.qb has CHECKSUM_NAME tokens for all trick names
        var file = FindQbFile("Pro Skater 3", "airtricks.qb");
        Assert.SkipWhen(file == null, "THPS3 airtricks.qb not found");

        var qb = QbFile.Parse(file!);

        Assert.True(qb.LocalNames.Count > 0,
            "Expected CHECKSUM_NAME tokens to populate LocalNames");
    }

    [Fact]
    public void Parse_ScriptDefinition_CreatesScriptItem()
    {
        // alf_scripts.qb has top-level script definitions
        var file = FindQbFile("Pro Skater 3", "alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = QbFile.Parse(file!);
        var scripts = qb.Items.Where(i => i.Kind == QbItemKind.Script).ToList();

        Assert.True(scripts.Count > 0, "Expected at least one script item");
        // All scripts should have a name checksum
        foreach (var s in scripts)
        {
            Assert.True(s.NameChecksum != 0, "Script should have a name checksum");
            Assert.True(s.StartTokenIndex < s.EndTokenIndex, "Script should span tokens");
        }
    }

    [Fact]
    public void Parse_GlobalAssignment_CreatesGlobalItem()
    {
        var file = FindQbFile("Pro Skater 3", "airtricks.qb");
        Assert.SkipWhen(file == null, "THPS3 airtricks.qb not found");

        var qb = QbFile.Parse(file!);
        var globals = qb.Items.Where(i => i.Kind == QbItemKind.Global).ToList();

        Assert.True(globals.Count > 0, "Expected at least one global item");
        foreach (var g in globals)
        {
            Assert.True(g.NameChecksum != 0, "Global should have a name checksum");
        }
    }

    [Fact]
    public void Decompile_SimpleScript_ProducesReadableOutput()
    {
        var file = FindQbFile("Pro Skater 3", "alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = QbFile.Parse(file!);
        var source = QbDecompiler.Decompile(qb);

        Assert.NotEmpty(source);
        Assert.Contains("script ", source);
        Assert.Contains("endscript", source);
    }

    [Fact]
    public void Decompile_IfElseEndif_Present()
    {
        var file = FindQbFile("Pro Skater 3", "alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = QbFile.Parse(file!);
        var source = QbDecompiler.Decompile(qb);

        // Should contain if/endif blocks
        Assert.Contains("if ", source);
        Assert.Contains("endif", source);

        // Every top-level script item should decompile to a script/endscript pair
        foreach (var item in qb.Items.Where(i => i.Kind == QbItemKind.Script))
        {
            var itemSource = QbDecompiler.DecompileItem(qb, item);
            Assert.Contains("script ", itemSource);
            Assert.Contains("endscript", itemSource);
        }
    }

    [Fact]
    public void DecompileItem_SingleScript_OnlyContainsOneScript()
    {
        var file = FindQbFile("Pro Skater 3", "alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = QbFile.Parse(file!);
        var scriptItem = qb.Items.FirstOrDefault(i => i.Kind == QbItemKind.Script);
        Assert.SkipWhen(scriptItem == null, "No script items found");

        var source = QbDecompiler.DecompileItem(qb, scriptItem!);

        Assert.NotEmpty(source);
        Assert.Contains("script ", source);
        Assert.Contains("endscript", source);
        // Single item decompilation should produce exactly one script block
        var scriptCount = source.Split("script ").Length - 1;
        Assert.Equal(1, scriptCount);
    }

    [Fact]
    public void Decompile_AllQbFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllQbFiles();
        Assert.SkipWhen(files.Length == 0, "No QB files found");

        var errors = new List<string>();
        var decompiled = 0;

        foreach (var file in files)
        {
            try
            {
                var qb = QbFile.Parse(file);
                var source = QbDecompiler.Decompile(qb);
                Assert.NotNull(source);
                decompiled++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed to decompile {errors.Count}/{files.Length} files:\n{string.Join("\n", errors)}");
        Assert.True(decompiled > 0, "No files were decompiled");
    }

    [Fact]
    public void ResolveName_LocalName_ReturnsLocalFirst()
    {
        var file = FindQbFile("Pro Skater 3", "airtricks.qb");
        Assert.SkipWhen(file == null, "THPS3 airtricks.qb not found");

        var qb = QbFile.Parse(file!);
        Assert.SkipWhen(qb.LocalNames.Count == 0, "No local names in file");

        // Local names should take priority
        var (checksum, expectedName) = qb.LocalNames.First();
        var resolved = qb.ResolveName(checksum);
        Assert.Equal(expectedName, resolved);
    }

    [Fact]
    public void ResolveName_UnknownChecksum_ReturnsHexFallback()
    {
        var qb = QbFile.Parse([], "test.qb");
        var resolved = qb.ResolveName(0xDEADBEEF);
        Assert.Equal("#\"0xDEADBEEF\"", resolved);
    }
}
