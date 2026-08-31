using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

public class QbFileTests(TestPaths paths)
{
    private const string Thps3Ps2Build = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    private static long CountOccurrences(string text, string value)
    {
        var count = 0L;
        for (var i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private string[] GetAllQbFiles()
    {
        if (!paths.HasSampleBuilds) return [];
        return Directory.EnumerateDirectories(paths.SampleBuildsDir!)
            .OrderBy(static build => build, StringComparer.Ordinal)
            .SelectMany(static build => Directory.EnumerateFiles(
                build, "*.qb", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private string? FindQbFile(string fileName)
    {
        if (!paths.HasSampleBuilds) return null;
        var file = Path.Combine(
            paths.SampleBuildsDir!, Thps3Ps2Build, "SKATE3", "Scripts", fileName);
        return File.Exists(file) ? file : null;
    }

    [CorpusFact]
    public void Parse_AllQbFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllQbFiles();
        Assert.SkipWhen(files.Length == 0, "No QB files found");

        var errors = new List<string>();
        var parsed = 0;
        var totalTokens = 0L;
        var totalScripts = 0;
        var totalGlobals = 0;

        // 4,746 through 2026-08-19; +356 loose THPS4-era .qb with the THPS4 PC
        // build on 2026-08-20; +819 when the THUG2 Remix PSP build joined on
        // 2026-08-24 — the PSP port's .qb are the classic THPS3-THUG2 token
        // stream and every one parses with the existing grammar; +427 more on
        // 2026-08-26 when PRE v4 support extracted the Remix datap PREs.
        Assert.Equal(6_348, files.Length);

        foreach (var file in files)
        {
            try
            {
                var qb = QbFile.Parse(file);
                Assert.NotNull(qb.Tokens);
                Assert.True(qb.Tokens.Count > 0, $"{Path.GetFileName(file)}: empty token list");
                parsed++;
                totalTokens += qb.Tokens.Count;
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
        Assert.Equal(files.Length, parsed);
        // Aggregates re-pinned 2026-08-24 when the THUG2 Remix PSP build's 819
        // .qb joined the corpus, and again 2026-08-26 for the 427 PRE-extracted
        // Remix .qb (+1,631,079 tokens, +5,677 scripts, +266,798 globals).
        Assert.Equal(22_992_212L, totalTokens);
        Assert.Equal(77_749, totalScripts);
        Assert.Equal(2_259_078, totalGlobals);
    }

    [Fact]
    public void Parse_ChecksumName_PopulatesLocalNames()
    {
        // airtricks.qb has CHECKSUM_NAME tokens for all trick names
        var file = FindQbFile("airtricks.qb");
        Assert.SkipWhen(file == null, "THPS3 airtricks.qb not found");

        var qb = ParseAirtricksFixture(file!);

        Assert.True(qb.LocalNames.Count > 0,
            "Expected CHECKSUM_NAME tokens to populate LocalNames");
    }

    [Fact]
    public void Parse_ScriptDefinition_CreatesScriptItem()
    {
        // alf_scripts.qb has top-level script definitions
        var file = FindQbFile("alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = ParseAlfScriptsFixture(file!);
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
        var file = FindQbFile("airtricks.qb");
        Assert.SkipWhen(file == null, "THPS3 airtricks.qb not found");

        var qb = ParseAirtricksFixture(file!);
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
        var file = FindQbFile("alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = ParseAlfScriptsFixture(file!);
        var source = QbDecompiler.Decompile(qb);

        Assert.NotEmpty(source);
        Assert.Contains("script ", source);
        Assert.Contains("endscript", source);
    }

    [Fact]
    public void Decompile_IfElseEndif_Present()
    {
        var file = FindQbFile("alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = ParseAlfScriptsFixture(file!);
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
        var file = FindQbFile("alf_scripts.qb");
        Assert.SkipWhen(file == null, "THPS3 alf_scripts.qb not found");

        var qb = ParseAlfScriptsFixture(file!);
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

    [CorpusFact]
    public void Decompile_AllQbFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllQbFiles();
        Assert.SkipWhen(files.Length == 0, "No QB files found");

        var errors = new List<string>();
        var decompiled = 0;
        var totalOutputChars = 0L;
        var escapedQuotes = 0L;

        // 4,746 through 2026-08-19; +356 loose THPS4-era .qb with the THPS4 PC
        // build on 2026-08-20; +819 THUG2 Remix PSP .qb on 2026-08-24; +427
        // PRE-extracted Remix .qb on 2026-08-26 — every one decompiles through
        // the existing THPS3-THUG2 path.
        Assert.Equal(6_348, files.Length);

        foreach (var file in files)
        {
            try
            {
                var qb = QbFile.Parse(file);
                var source = QbDecompiler.Decompile(qb);
                Assert.NotNull(source);
                decompiled++;
                totalOutputChars += source.Length;
                escapedQuotes += CountOccurrences(source, "\\'");
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed to decompile {errors.Count}/{files.Length} files:\n{string.Join("\n", errors)}");
        Assert.Equal(files.Length, decompiled);

        // Single-quoted local strings escape any quote they contain, one added
        // character each. The corpus holds 228 of them (183 through 2026-08-19,
        // +15 in the THPS4 PC build's loose .qb, +15 in the THUG2 Remix PSP
        // build, +15 in the Remix PRE-extracted copies), which is exactly the
        // difference from the pre-escaping total — so the pin below is derived
        // from that cause rather than copied off a failure.
        Assert.Equal(228L, escapedQuotes);
        Assert.Equal(139_819_449L + escapedQuotes, totalOutputChars);
    }

    [Fact]
    public void ResolveName_LocalName_ReturnsLocalFirst()
    {
        var file = FindQbFile("airtricks.qb");
        Assert.SkipWhen(file == null, "THPS3 airtricks.qb not found");

        var qb = ParseAirtricksFixture(file!);
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

    private QbFile ParseAirtricksFixture(string file)
    {
        AssertFixtureIdentity(file, "airtricks.qb", 25_694);
        var qb = QbFile.Parse(file);
        Assert.Equal(5_084, qb.Tokens.Count);
        Assert.Equal(450, qb.LocalNames.Count);
        Assert.Equal(1, qb.GlobalCount);
        return qb;
    }

    private QbFile ParseAlfScriptsFixture(string file)
    {
        AssertFixtureIdentity(file, "alf_scripts.qb", 105_331);
        var qb = QbFile.Parse(file);
        Assert.Equal(3_577, qb.Tokens.Count);
        Assert.Equal(3, qb.ScriptCount);
        return qb;
    }

    private void AssertFixtureIdentity(string actualPath, string fileName, long expectedSize)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(
            paths.SampleBuildsDir!, Thps3Ps2Build, "SKATE3", "Scripts", fileName));
        Assert.True(
            string.Equals(expectedPath, Path.GetFullPath(actualPath), StringComparison.OrdinalIgnoreCase),
            $"Expected fixture '{expectedPath}', got '{actualPath}'");
        Assert.Equal(expectedSize, new FileInfo(actualPath).Length);
    }
}
