using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Tests.Helpers;
using QbChecksum = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public sealed class Thps4PcLevelManifestTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";

    [Fact]
    public void TryParse_ExtractsAuthoredLevelSkyAndShell()
    {
        var qb = BuildManifest(
            ("Level_Motox", "motox", "hof_Sky", null),
            ("Level_Sk4Ed", "sk4ed", "sk4ed_Sky", "sk4ed_shell"));

        Assert.True(Thps4PcLevelManifest.TryParse(qb, out var entries));
        Assert.Equal(2, entries.Count);
        Assert.Equal("hof_Sky", entries["motox"].SkyName);
        Assert.Null(entries["motox"].OuterShellName);
        Assert.Equal("sk4ed_Sky", entries["sk4ed"].SkyName);
        Assert.Equal("sk4ed_shell", entries["sk4ed"].OuterShellName);
    }

    [Fact]
    public void TryParse_DuplicateLevelOwnershipFailsClosed()
    {
        var qb = BuildManifest(
            ("Level_A", "same", "sky_a", null),
            ("Level_B", "same", "sky_b", null));

        Assert.False(Thps4PcLevelManifest.TryParse(qb, out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryParse_DuplicateSkyMemberFailsClosed()
    {
        var tokens = Struct(
            Field("level", "main"),
            Field("sky", "one"),
            Field("sky", "two"));
        var qb = BuildGlobals(("Level_Main", tokens));

        Assert.False(Thps4PcLevelManifest.TryParse(qb, out _));
    }

    [CorpusFact]
    public void CorpusLevelsQb_PinsAllThirteenRuntimeSceneJoins()
    {
        var levelsQb = paths.FindSampleFile(Build, "Levels.qb");
        Assert.SkipWhen(levelsQb == null, "THPS4 PC Levels.qb is not available");

        var qb = QbFile.Parse(levelsQb!);
        Assert.True(Thps4PcLevelManifest.TryParse(qb, out var entries));
        Assert.Equal(13, entries.Count);

        var expected = new Dictionary<string, (string Sky, string? Shell)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["alc"] = ("alc_Sky", null),
            ["cnv"] = ("cnv_Sky", null),
            ["jnk"] = ("jnk_Sky", null),
            ["kon"] = ("Kon_Sky", null),
            ["lon"] = ("lon_Sky", null),
            ["sch"] = ("Sch_Sky", null),
            ["sf2"] = ("sf2_Sky", null),
            ["zoo"] = ("zoo_Sky", null),
            ["skateshop"] = ("skateshop_Sky", null),
            ["hof"] = ("Hof_Sky", null),
            ["motox"] = ("hof_Sky", null),
            ["sk4ed"] = ("sk4ed_Sky", "sk4ed_shell"),
            ["sk4ed2"] = ("sk4ed2_Sky", "sk4ed2_shell")
        };

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase),
            entries.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (level, companions) in expected)
        {
            Assert.Equal(companions.Sky, entries[level].SkyName, ignoreCase: true);
            Assert.Equal(companions.Shell, entries[level].OuterShellName, ignoreCase: true);
        }
    }

    [CorpusFact]
    public void CorpusMainScenes_ResolveOnlyThroughAuthoredManifest()
    {
        var levelsQb = paths.FindSampleFile(Build, "Levels.qb");
        Assert.SkipWhen(levelsQb == null, "THPS4 PC Levels.qb is not available");
        var dataDirectory = Directory.GetParent(Path.GetDirectoryName(levelsQb!)!)!.FullName;
        var levelsDirectory = Path.Combine(dataDirectory, "levels");

        var sceneFiles = Directory.EnumerateFiles(levelsDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path)
                .EndsWith("scn.dat", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var resolved = sceneFiles
            .Select(path => (Path: path,
                Success: Thps4PcLevelManifest.TryResolve(path, out var composition),
                Composition: composition))
            .Where(static item => item.Success)
            .ToArray();

        Assert.Equal(29, sceneFiles.Length);
        Assert.Equal(13, resolved.Length);
        Assert.All(resolved, static item =>
        {
            Assert.NotNull(item.Composition);
            Assert.NotNull(item.Composition!.SkyScenePath);
            Assert.True(File.Exists(item.Composition.SkyScenePath));
        });
        Assert.Equal(2, resolved.Count(static item => item.Composition!.OuterShellScenePath != null));

        var motox = Assert.Single(resolved, static item =>
            item.Composition!.ManifestEntry.LevelName.Equals("motox", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("hof_Sky", motox.Composition!.ManifestEntry.SkyName, ignoreCase: true);
        Assert.Contains("Hof_Sky", motox.Composition.SkyScenePath!, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(resolved, static item =>
            item.Path.Contains("Can_Sky", StringComparison.OrdinalIgnoreCase)
            || item.Path.Contains("Pink_Sky", StringComparison.OrdinalIgnoreCase));
    }

    private static QbFile BuildManifest(
        params (string Structure, string Level, string? Sky, string? Shell)[] entries)
    {
        return BuildGlobals(entries.Select(entry =>
        {
            var fields = new List<IReadOnlyList<QbToken>> { Field("level", entry.Level) };
            if (entry.Sky != null) fields.Add(Field("sky", entry.Sky));
            if (entry.Shell != null) fields.Add(Field("outer_shell", entry.Shell));
            return (entry.Structure, Struct(fields.ToArray()));
        }).ToArray());
    }

    private static QbFile BuildGlobals(
        params (string Name, IReadOnlyList<QbToken> Value)[] globals)
    {
        var tokens = new List<QbToken>();
        var items = new List<QbItem>();
        var localNames = new Dictionary<uint, string>();
        foreach (var (name, value) in globals)
        {
            var checksum = QbChecksum.HashLower(name);
            localNames[checksum] = name;
            var start = tokens.Count;
            tokens.Add(Name(checksum));
            tokens.Add(Token(QbTokenType.Equals));
            tokens.AddRange(value);
            tokens.Add(Token(QbTokenType.EndOfLine));
            items.Add(new QbItem
            {
                Kind = QbItemKind.Global,
                NameChecksum = checksum,
                Name = name,
                StartTokenIndex = start,
                EndTokenIndex = tokens.Count
            });
        }

        return new QbFile
        {
            FileName = "Levels.qb",
            Tokens = tokens,
            Items = items,
            LocalNames = localNames
        };
    }

    private static IReadOnlyList<QbToken> Struct(params IReadOnlyList<QbToken>[] fields)
    {
        var result = new List<QbToken> { Token(QbTokenType.StartStruct) };
        foreach (var field in fields) result.AddRange(field);
        result.Add(Token(QbTokenType.EndStruct));
        return result;
    }

    private static IReadOnlyList<QbToken> Field(string name, string value) =>
    [
        Name(QbChecksum.HashLower(name)),
        Token(QbTokenType.Equals),
        new QbToken { Type = QbTokenType.String, StringValue = value }
    ];

    private static QbToken Name(uint checksum) =>
        new() { Type = QbTokenType.Name, NameChecksum = checksum };

    private static QbToken Token(QbTokenType type) => new() { Type = type };
}
