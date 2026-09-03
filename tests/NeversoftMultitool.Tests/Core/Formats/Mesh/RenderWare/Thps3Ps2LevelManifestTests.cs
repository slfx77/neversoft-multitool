using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Tests.Helpers;
using QbChecksum = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class Thps3Ps2LevelManifestTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    [Fact]
    public void TryParse_SelectsSinglePlayerArmAndOnlyApplicableBackground()
    {
        var qb = BuildManifest(
            [
                Master("Airport", "Load_Ap"),
                Master("Canada", "Load_Can"),
                Master("Debug", "Load_Debug", "debug_level")
            ],
            [
                Script("Load_Ap",
                    If(),
                    Geometry("Ap", ""),
                    Background(151, 180, 214),
                    Else(),
                    Geometry("Ap", "Ap_Sky"),
                    EndIf()),
                Script("Load_Can",
                    If(),
                    Geometry("Can", ""),
                    Else(),
                    Geometry("Can", "Can_Sky"),
                    EndIf(),
                    Background(167, 175, 197)),
                Script("Load_Debug", Geometry("Debug", "Debug_Sky"))
            ]);

        Assert.True(Thps3Ps2LevelManifest.TryParse(qb, out var entries));
        Assert.Equal(2, entries.Count);

        var airport = Assert.Single(entries, static entry => entry.DisplayName == "Airport");
        Assert.Equal("Levels/Ap/Ap.bsp", airport.LevelAssetPath);
        Assert.Equal("Levels/Ap_Sky/Ap_Sky.bsp", airport.SkyAssetPath);
        Assert.Null(airport.BackgroundColor); // The only colour is in the multiplayer arm.

        var canada = Assert.Single(entries, static entry => entry.DisplayName == "Canada");
        Assert.Equal(0xA7AFC5u, canada.BackgroundColor);
        Assert.Equal("Can", canada.PreSet);
    }

    [Fact]
    public void TryParse_RejectsTwoSinglePlayerSkyCandidates()
    {
        var qb = BuildManifest(
            [Master("Level", "Load_Level")],
            [Script("Load_Level", Geometry("One", "Sky_A"), Geometry("One", "Sky_B"))]);

        Assert.False(Thps3Ps2LevelManifest.TryParse(qb, out _));
    }

    [Fact]
    public void TryParse_SelectsMultiplayerPredicateElseArmRatherThanGuessingFromSky()
    {
        var qb = BuildManifest(
            [Master("Level", "Load_Level")],
            [Script("Load_Level",
                If("InNetGame"),
                Geometry("One", "Multiplayer_Sky"),
                Else(),
                Geometry("One", ""),
                EndIf())]);

        Assert.True(Thps3Ps2LevelManifest.TryParse(qb, out var entries));
        Assert.Null(Assert.Single(entries).SkyAssetPath);
    }

    [Fact]
    public void TryParse_RejectsGeometryBehindUnrelatedConditional()
    {
        var qb = BuildManifest(
            [Master("Level", "Load_Level")],
            [Script("Load_Level",
                If("IsCareerMode"),
                Geometry("One", "Sky_A"),
                Else(),
                Geometry("One", "Sky_B"),
                EndIf())]);

        Assert.False(Thps3Ps2LevelManifest.TryParse(qb, out _));
    }

    [Fact]
    public void TryParse_RejectsElseIfAsAFalseSinglePlayerArm()
    {
        var qb = BuildManifest(
            [Master("Level", "Load_Level")],
            [Script("Load_Level",
                If(),
                Geometry("One", "Multiplayer_Sky"),
                ElseIf("IsCareerMode"),
                Geometry("One", "Career_Sky"),
                EndIf())]);

        Assert.False(Thps3Ps2LevelManifest.TryParse(qb, out _));
    }

    [CorpusFact]
    public void CorpusManifest_PinsAllThirteenSinglePlayerLevelSkyAndBackdropSelections()
    {
        var manifest = FindShippingManifest();
        Assert.SkipWhen(manifest == null, "THPS3 PS2 SKATE3/Scripts/levels.qb is not available");

        Assert.True(Thps3Ps2LevelManifest.TryParse(QbFile.Parse(manifest!), out var entries));
        Assert.Equal(13, entries.Count);

        var expected = new Dictionary<string, (string Main, string? Sky, uint? Background)>
            (StringComparer.OrdinalIgnoreCase)
        {
            ["Foundry"] = ("Foun", null, 0x000000),
            ["Canada"] = ("Can", "Can_Sky", 0xA7AFC5),
            ["Rio"] = ("Rio", "Rio_Sky", 0x646EC8),
            ["Suburbia"] = ("Sub", "Sub_Sky", 0x505050),
            ["Airport"] = ("Ap", "Ap_Sky", null),
            ["Skater Island"] = ("SI", "SI_Sky", 0x3787C8),
            ["Los Angeles"] = ("La", "La_Sky", 0x000000),
            ["Tokyo"] = ("Tok", "Tok_Sky", 0x000000),
            ["Cruise Ship"] = ("Shp", "Shp_Sky", 0x0064FF),
            ["Warehouse"] = ("Ware", null, null),
            ["Burnside"] = ("Burn", "Burn_Sky", 0x808080),
            ["Roswell"] = ("Ros", "Ros_Sky", 0x000000),
            ["Tutorials"] = ("Tut", "Sk3Ed_Bch_Sky", 0x808080)
        };

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase),
            entries.Select(static entry => entry.DisplayName).Order(StringComparer.OrdinalIgnoreCase));
        foreach (var entry in entries)
        {
            var value = expected[entry.DisplayName];
            Assert.Equal($"Levels/{value.Main}/{value.Main}.bsp", entry.LevelAssetPath, ignoreCase: true);
            Assert.Equal(value.Sky == null ? null : $"Levels/{value.Sky}/{value.Sky}.bsp",
                entry.SkyAssetPath, ignoreCase: true);
            Assert.Equal(value.Background, entry.BackgroundColor);
        }

        Assert.Equal(11, entries.Count(static entry => entry.SkyAssetPath != null));
        Assert.Equal(11, entries.Count(static entry => entry.BackgroundColor.HasValue));
    }

    [CorpusFact]
    public void CorpusRuntimeTree_ResolvesExactlyTheThirteenAuthoredMains()
    {
        var manifest = FindShippingManifest();
        Assert.SkipWhen(manifest == null, "THPS3 PS2 SKATE3/Scripts/levels.qb is not available");
        var skate3 = Directory.GetParent(Path.GetDirectoryName(manifest!)!)!.FullName;
        var pre = Path.Combine(skate3, "pre");

        var bspFiles = Directory.EnumerateFiles(pre, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var resolved = bspFiles
            .Select(path => (Path: path, Success: Thps3Ps2LevelManifest.TryResolve(path, out var value), Value: value))
            .Where(static item => item.Success)
            .ToArray();

        Assert.Equal(13, resolved.Length);
        Assert.Equal(11, resolved.Count(static item => item.Value!.SkyBspPath != null));
        Assert.All(resolved, static item =>
        {
            Assert.NotNull(item.Value);
            Assert.Equal(Path.GetFullPath(item.Path), Path.GetFullPath(item.Value!.LevelBspPath), ignoreCase: true);
            if (item.Value.SkyBspPath != null)
                Assert.True(File.Exists(item.Value.SkyBspPath));
        });

        var tutorials = Assert.Single(resolved,
            static item => item.Value!.ManifestEntry.DisplayName == "Tutorials");
        Assert.EndsWith(Path.Combine("Levels", "Sk3Ed_Bch_Sky", "Sk3Ed_Bch_Sky.bsp"),
            tutorials.Value!.SkyBspPath!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(resolved,
            static item => item.Path.Contains("Sk3Ed_BchBSP", StringComparison.OrdinalIgnoreCase));
    }

    [CorpusFact]
    public void CorpusRuntimeTree_RejectsAmbiguousAuthoredSkyWithinTheSameBuild()
    {
        var manifest = FindShippingManifest();
        Assert.SkipWhen(manifest == null, "THPS3 PS2 SKATE3/Scripts/levels.qb is not available");
        var sourceSkate3 = Directory.GetParent(Path.GetDirectoryName(manifest!)!)!.FullName;
        var sourcePre = Path.Combine(sourceSkate3, "pre");
        var sourceMain = FindExactRuntimeAsset(sourcePre, "Can", "Can.bsp");
        var sourceSky = FindExactRuntimeAsset(sourcePre, "Can_Sky", "Can_Sky.bsp");

        using var temp = new TempDirectory();
        var skate3 = Path.Combine(temp.Path, "SKATE3");
        var copiedManifest = Path.Combine(skate3, "Scripts", "levels.qb");
        var copiedMain = Path.Combine(skate3, "pre", "Main", "Levels", "Can", "Can.bsp");
        var firstSky = Path.Combine(
            skate3, "pre", "SkyA", "Levels", "Can_Sky", "Can_Sky.bsp");
        var secondSky = Path.Combine(
            skate3, "pre", "SkyB", "Levels", "Can_Sky", "Can_Sky.bsp");
        CopyFile(manifest!, copiedManifest);
        CopyFile(sourceMain, copiedMain);
        CopyFile(sourceSky, firstSky);
        CopyFile(sourceSky, secondSky);

        Assert.False(Thps3Ps2LevelManifest.TryResolve(copiedMain, out _));
    }

    private string? FindShippingManifest()
    {
        var candidates = paths.FindSampleFiles(Build, "levels.qb")
            .Where(path => path.Replace('\\', '/').EndsWith("/SKATE3/Scripts/levels.qb",
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string FindExactRuntimeAsset(string preRoot, string directory, string fileName) =>
        Directory.EnumerateFiles(preRoot, fileName, SearchOption.AllDirectories)
            .Single(path => path.Replace('\\', '/').EndsWith(
                $"/Levels/{directory}/{fileName}", StringComparison.OrdinalIgnoreCase));

    private static void CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("nmt-thps3-manifest-").FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }

    private static QbFile BuildManifest(
        IReadOnlyList<IReadOnlyList<QbToken>> masterEntries,
        IReadOnlyList<IReadOnlyList<QbToken>> scripts)
    {
        var tokens = new List<QbToken>
        {
            Name("master_level_list"), Token(QbTokenType.Equals), Token(QbTokenType.StartArray)
        };
        foreach (var entry in masterEntries) tokens.AddRange(entry);
        tokens.Add(Token(QbTokenType.EndArray));
        tokens.Add(Line());
        foreach (var script in scripts) tokens.AddRange(script);
        tokens.Add(Token(QbTokenType.EndOfFile));

        var names = tokens
            .Where(static token => token.Type is QbTokenType.Name or QbTokenType.Enum)
            .Select(static token => token.NameChecksum)
            .Distinct()
            .ToDictionary(static checksum => checksum,
                static checksum => QbChecksum.TryResolve(checksum) ?? $"key_{checksum:X8}");
        foreach (var known in new[] { "Load_Ap", "Load_Can", "Load_Debug", "Load_Level" })
            names[QbChecksum.HashLower(known)] = known;

        return new QbFile { FileName = "levels.qb", Tokens = tokens, LocalNames = names };
    }

    private static IReadOnlyList<QbToken> Master(string displayName, string script, string? flag = null)
    {
        var tokens = new List<QbToken>
        {
            Token(QbTokenType.StartStruct),
            Name("level_name"), Token(QbTokenType.Equals), String(displayName),
            Name("load_script"), Token(QbTokenType.Equals), Name(script)
        };
        if (flag != null) tokens.Add(Name(flag));
        tokens.Add(Token(QbTokenType.EndStruct));
        return tokens;
    }

    private static IReadOnlyList<QbToken> Script(string name, params IReadOnlyList<QbToken>[] statements)
    {
        var tokens = new List<QbToken> { Token(QbTokenType.KeywordScript), Name(name), Line() };
        foreach (var statement in statements)
        {
            tokens.AddRange(statement);
            tokens.Add(Line());
        }
        tokens.Add(Token(QbTokenType.KeywordEndScript));
        return tokens;
    }

    private static IReadOnlyList<QbToken> Geometry(string main, string sky) =>
    [
        Name("loadlevelgeometry"),
        Name("level"), Token(QbTokenType.Equals), String($"Levels\\{main}\\{main}.bsp"),
        Name("Sky"), Token(QbTokenType.Equals), String(sky.Length == 0 ? "" : $"Levels\\{sky}\\{sky}.bsp"),
        Name("Pre_set"), Token(QbTokenType.Equals), String(main)
    ];

    private static IReadOnlyList<QbToken> Background(int r, int g, int b) =>
    [
        Name("SetBackgroundColor"), Token(QbTokenType.StartStruct),
        Name("r"), Token(QbTokenType.Equals), Integer(r),
        Name("g"), Token(QbTokenType.Equals), Integer(g),
        Name("b"), Token(QbTokenType.Equals), Integer(b),
        Token(QbTokenType.EndStruct)
    ];

    private static IReadOnlyList<QbToken> If(string predicate = "InMultiPlayerGame") =>
        [Token(QbTokenType.KeywordIf), Name(predicate)];

    private static IReadOnlyList<QbToken> Else() => [Token(QbTokenType.KeywordElse)];
    private static IReadOnlyList<QbToken> ElseIf(string predicate) =>
        [Token(QbTokenType.KeywordElseIf), Name(predicate)];
    private static IReadOnlyList<QbToken> EndIf() => [Token(QbTokenType.KeywordEndIf)];
    private static QbToken Name(string value) =>
        new() { Type = QbTokenType.Name, NameChecksum = QbChecksum.HashLower(value) };
    private static QbToken String(string value) => new() { Type = QbTokenType.String, StringValue = value };
    private static QbToken Integer(int value) => new() { Type = QbTokenType.Integer, IntValue = value };
    private static QbToken Line() => new() { Type = QbTokenType.EndOfLineNumber, IntValue = 1 };
    private static QbToken Token(QbTokenType type) => new() { Type = type };
}
