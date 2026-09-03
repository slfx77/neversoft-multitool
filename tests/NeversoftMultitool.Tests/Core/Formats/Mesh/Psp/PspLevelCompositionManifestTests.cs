using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psp;
using NeversoftMultitool.Core.Formats.Qb;
using QbChecksum = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psp;

public sealed class PspLevelCompositionManifestTests(TestPaths paths)
{
    private const string RemixBuild =
        "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";
    private const string Project8FinalBuild =
        "Tony Hawk's Project 8 (2006-10-14, PSP - Final)";
    private const string Project8Rev1Build =
        "Tony Hawk's Project 8 (2007-2-16, PSP - Rev1)";

    [Fact]
    public void Parser_RejectsDuplicateOwnershipMemberAndWrongLoaderOrder()
    {
        var duplicate = SyntheticManifest(PspLevelCompositionManifest.Game.Thug2Remix);
        var structureEnd = duplicate.Tokens.FindLastIndex(
            static token => token.Type == QbTokenType.EndStruct);
        duplicate.Tokens.InsertRange(structureEnd,
        [
            Name("level"), Token(QbTokenType.Equals), String("other")
        ]);

        Assert.False(PspLevelCompositionManifest.TryParse(
            duplicate,
            PspLevelCompositionManifest.Game.Thug2Remix,
            out _));

        var wrongOrder = SyntheticManifest(
            PspLevelCompositionManifest.Game.Thug2Remix,
            putSkyAfterMain: true);
        Assert.False(PspLevelCompositionManifest.TryParse(
            wrongOrder,
            PspLevelCompositionManifest.Game.Thug2Remix,
            out _));
    }

    [Fact]
    public void Parser_RejectsMismatchedStructureNameAndTruncatedTokenEnvelope()
    {
        var mismatch = SyntheticManifest(PspLevelCompositionManifest.Game.Project8);
        var structureNameValue = mismatch.Tokens.FindIndex(token =>
            token.Type == QbTokenType.Name
            && token.NameChecksum == QbChecksum.HashLower("structure_name"));
        mismatch.Tokens[structureNameValue + 2].NameChecksum = QbChecksum.HashLower("Level_Other");
        Assert.False(PspLevelCompositionManifest.TryParse(
            mismatch,
            PspLevelCompositionManifest.Game.Project8,
            out _));

        var truncated = SyntheticManifest(PspLevelCompositionManifest.Game.Project8);
        truncated.Tokens.RemoveAt(truncated.Tokens.Count - 1);
        Assert.False(PspLevelCompositionManifest.TryParse(
            truncated,
            PspLevelCompositionManifest.Game.Project8,
            out _));
    }

    [Fact]
    public void Parser_AcceptsExactRuntimeContractsAndExplicitNoSky()
    {
        foreach (var game in Enum.GetValues<PspLevelCompositionManifest.Game>())
        {
            var manifest = SyntheticManifest(game, includeSky: false);
            Assert.True(PspLevelCompositionManifest.TryParse(manifest, game, out var entries));
            var entry = Assert.Single(entries);
            Assert.Equal("level_test", entry.StructureName, ignoreCase: true);
            Assert.Equal("test", entry.LevelName);
            Assert.Null(entry.SkyName);
            Assert.False(entry.IsEditorAlternative);
        }
    }

    [Fact]
    public void TextureRegistration_CanKeepSceneNamespacesIndependent()
    {
        var document = new ModelDocument
        {
            Name = "namespaces",
            SourceKind = ModelSourceKind.XbxScene
        };
        var png = new byte[] { 1, 2, 3, 4 };
        var first = ModelDocumentGeometryAdapter.AddTexture(
            document,
            "sky__shared",
            png,
            0x12345678,
            distinguishChecksumVariantsByContent: true,
            distinguishChecksumVariantsByName: true);
        var repeated = ModelDocumentGeometryAdapter.AddTexture(
            document,
            "sky__shared",
            png,
            0x12345678,
            distinguishChecksumVariantsByContent: true,
            distinguishChecksumVariantsByName: true);
        var second = ModelDocumentGeometryAdapter.AddTexture(
            document,
            "level__shared",
            png,
            0x12345678,
            distinguishChecksumVariantsByContent: true,
            distinguishChecksumVariantsByName: true);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
        Assert.Equal(2, document.Textures.Count);
    }

    [CorpusFact]
    public void Corpus_RuntimeManifestsResolveOnlyExactAuthoredMainSubsets()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var expectations = new[]
        {
            (RemixBuild, PspLevelCompositionManifest.Game.Thug2Remix, 80, 42, 40, 21, 5),
            (Project8FinalBuild, PspLevelCompositionManifest.Game.Project8, 294, 40, 36, 20, 0),
            (Project8Rev1Build, PspLevelCompositionManifest.Game.Project8, 294, 40, 36, 20, 0)
        };

        foreach (var (build, game, corpusCount, resolvedCount, skyCount, ownerCount, editorCount)
                 in expectations)
        {
            var files = FindLevelFiles(build);
            Assert.Equal(corpusCount, files.Length);
            var resolved = files
                .Select(file => PspLevelCompositionManifest.TryResolve(file, out var composition)
                    ? composition
                    : null)
                .WhereNotNull()
                .ToArray();
            Assert.Equal(resolvedCount, resolved.Length);
            Assert.Equal(skyCount, resolved.Count(static item => item.SkyScenePath != null));
            Assert.Equal(resolvedCount / 2,
                resolved.Count(static item => item.IsNetworkVariant));
            Assert.Equal(ownerCount,
                resolved.Select(static item => item.ManifestEntry.StructureName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(resolved, item => Assert.Equal(game, item.Game));
            Assert.All(resolved, static item => Assert.Null(item.OuterShellScenePath));

            var manifestPath = game == PspLevelCompositionManifest.Game.Thug2Remix
                ? Path.Combine(BuildDatap(build), "scripts", "game", "levels.qb")
                : Path.Combine(BuildDatap(build), "pak", "qb.pak", "scripts", "game", "levels.qb.psp");
            var data = File.ReadAllBytes(manifestPath);
            var qb = game == PspLevelCompositionManifest.Game.Thug2Remix
                ? QbFile.ParseLegacyFastBranches(data, Path.GetFileName(manifestPath))
                : QbFile.Parse(data, Path.GetFileName(manifestPath));
            Assert.True(PspLevelCompositionManifest.TryParse(qb, game, out var entries));
            Assert.Equal(editorCount, entries.Count(static entry => entry.IsEditorAlternative));
        }
    }

    [CorpusFact]
    public void Corpus_ComposedRemixAndProject8ScenesExportRealTexturedGlbs()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var examples = new[]
        {
            (Build: RemixBuild, Relative: Path.Combine("levels", "tr", "tr.psp_level"),
                Game: "thug2_remix"),
            (Build: Project8FinalBuild,
                Relative: Path.Combine("worlds", "worldzones", "z_dj", "z_dj.psp_level"),
                Game: "project_8")
        };

        foreach (var example in examples)
        {
            var path = Path.Combine(BuildDatap(example.Build), example.Relative);
            var source = new FileSystemAssetSource(path);
            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = source,
                FileName = source.EntryName,
                OutputStem = Path.GetFileNameWithoutExtension(path),
                SourceKind = ModelSourceKind.XbxScene
            });

            var composition = Assert.Single(document.NativeMetadata
                .OfType<PspLevelCompositionMetadata>());
            Assert.Equal(example.Game, composition.Game);
            Assert.NotNull(composition.SkySceneName);
            Assert.True(document.TriangleCount > 0);
            Assert.Contains(document.Meshes, static mesh =>
                mesh.Name.StartsWith("sky__", StringComparison.Ordinal));
            Assert.Contains(document.Meshes, static mesh =>
                mesh.Name.StartsWith("level__", StringComparison.Ordinal));
            Assert.All(document.Meshes
                    .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
                    .SelectMany(static mesh => mesh.Primitives),
                primitive => Assert.Single(primitive.NativeMetadata.OfType<PsxSkyRenderMetadata>()));
            Assert.All(document.Materials, static material =>
                Assert.True(material.Name.StartsWith("sky__", StringComparison.Ordinal)
                            || material.Name.StartsWith("level__", StringComparison.Ordinal)));
            Assert.All(document.Textures, static texture =>
                Assert.True(texture.Name.StartsWith("sky__", StringComparison.Ordinal)
                            || texture.Name.StartsWith("level__", StringComparison.Ordinal)));

            var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
            Assert.NotNull(glb);
            Assert.Equal(document.TriangleCount, triangles);
            Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(glb, 0, 4));
            Assert.True(glb.Length > 100_000);
        }
    }

    [CorpusFact]
    public void Corpus_ExplicitNoSkyAndBrokenOptionalCompanionRemainSafe()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var noSkyExamples = new[]
        {
            Path.Combine(BuildDatap(RemixBuild), "levels", "mainmenu", "mainmenu.psp_level"),
            Path.Combine(BuildDatap(Project8FinalBuild), "worlds", "worldzones", "z_training",
                "z_training.psp_level")
        };
        foreach (var path in noSkyExamples)
        {
            var document = Parse(path);
            var metadata = Assert.Single(document.NativeMetadata.OfType<PspLevelCompositionMetadata>());
            Assert.Null(metadata.SkySceneName);
            Assert.DoesNotContain(document.Meshes, static mesh =>
                mesh.Name.StartsWith("sky__", StringComparison.Ordinal));
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"nmt-psp-composition-{Guid.NewGuid():N}");
        try
        {
            var datap = Path.Combine(temporaryRoot, "datap");
            var scripts = Path.Combine(datap, "scripts", "game");
            var levelDirectory = Path.Combine(datap, "levels", "tr");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(levelDirectory);
            File.Copy(
                Path.Combine(BuildDatap(RemixBuild), "scripts", "game", "levels.qb"),
                Path.Combine(scripts, "levels.qb"));
            var copiedMain = Path.Combine(levelDirectory, "tr.psp_level");
            File.Copy(
                Path.Combine(BuildDatap(RemixBuild), "levels", "tr", "tr.psp_level"),
                copiedMain);

            Assert.False(PspLevelCompositionManifest.TryResolve(copiedMain, out _));
            var fallback = Parse(copiedMain);
            Assert.Empty(fallback.NativeMetadata.OfType<PspLevelCompositionMetadata>());
            Assert.DoesNotContain(fallback.Meshes, static mesh =>
                mesh.Name.StartsWith("sky__", StringComparison.Ordinal));

            File.WriteAllBytes(Path.Combine(scripts, "levels.qb"), [0x23, 0x00]);
            Assert.False(PspLevelCompositionManifest.TryResolve(copiedMain, out _));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private ModelDocument Parse(string path)
    {
        var source = new FileSystemAssetSource(path);
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = Path.GetFileNameWithoutExtension(path),
            SourceKind = ModelSourceKind.XbxScene
        });
    }

    private string[] FindLevelFiles(string build) => paths
        .FindSampleFiles(build, "*.psp_level")
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private string BuildDatap(string build) => Path.Combine(
        paths.SampleBuildsDir!, build, "PSP_GAME", "USRDIR", "datap");

    private static QbFile SyntheticManifest(
        PspLevelCompositionManifest.Game game,
        bool putSkyAfterMain = false,
        bool includeSky = true)
    {
        var tokens = new List<QbToken>();
        var items = new List<QbItem>();
        var names = new Dictionary<uint, string>();
        void Register(string name) => names[QbChecksum.HashLower(name)] = name;
        foreach (var name in new[]
                 {
                     "load_level", "Load_Test", "level_test", "loadscene", "scene", "sky", "level",
                     "outer_shell", "is_dictionary", "no_supersectors", "is_net", "park_editor",
                     "ispsp", "is_streaming_level", "structure_name", "load_script"
                 })
        {
            Register(name);
        }

        var loaderStart = tokens.Count;
        tokens.Add(Token(QbTokenType.KeywordScript));
        tokens.Add(Name("load_level"));
        if (game == PspLevelCompositionManifest.Game.Project8)
        {
            tokens.Add(Name("ispsp"));
            tokens.Add(Token(QbTokenType.Or));
            tokens.Add(Token(QbTokenType.Arg));
            tokens.Add(Name("is_streaming_level"));
            tokens.Add(Token(QbTokenType.Equals));
            tokens.Add(Integer(0));
            tokens.Add(Token(QbTokenType.EndOfLine));
            if (putSkyAfterMain)
                AddCall(tokens, "level", "is_net");
            AddCall(tokens, "sky");
            if (!putSkyAfterMain)
                AddCall(tokens, "level", "is_net");
            AddCall(tokens, "level");
        }
        else
        {
            if (putSkyAfterMain)
                AddCall(tokens, "level", "is_dictionary");
            AddCall(tokens, "sky");
            tokens.Add(Name("park_editor"));
            tokens.Add(Token(QbTokenType.EndOfLine));
            if (!putSkyAfterMain)
                AddCall(tokens, "level", "is_dictionary");
            AddCall(tokens, "outer_shell", "no_supersectors");
            AddCall(tokens, "level", "is_net");
            AddCall(tokens, "level");
        }

        tokens.Add(Token(QbTokenType.KeywordEndScript));
        items.Add(new QbItem
        {
            Kind = QbItemKind.Script,
            NameChecksum = QbChecksum.HashLower("load_level"),
            Name = "load_level",
            StartTokenIndex = loaderStart,
            EndTokenIndex = tokens.Count - 1
        });

        tokens.Add(Token(QbTokenType.KeywordScript));
        tokens.Add(Name("Load_Test"));
        tokens.Add(Name("load_level"));
        tokens.Add(Name("level_test"));
        tokens.Add(Token(QbTokenType.EndOfLine));
        tokens.Add(Token(QbTokenType.KeywordEndScript));

        tokens.Add(Name("level_test"));
        tokens.Add(Token(QbTokenType.Equals));
        tokens.Add(Token(QbTokenType.StartStruct));
        AddMember(tokens, "structure_name", Name("level_test"));
        AddMember(tokens, "load_script", Name("Load_Test"));
        AddMember(tokens, "level", String("test"));
        if (includeSky)
            AddMember(tokens, "sky", String("test_sky"));
        if (game == PspLevelCompositionManifest.Game.Project8)
            AddMember(tokens, "is_streaming_level", Integer(1));
        tokens.Add(Token(QbTokenType.EndStruct));
        tokens.Add(Token(QbTokenType.EndOfLine));
        tokens.Add(Token(QbTokenType.EndOfFile));

        return new QbFile
        {
            FileName = "synthetic.qb",
            Tokens = tokens,
            Items = items,
            LocalNames = names
        };
    }

    private static void AddCall(List<QbToken> tokens, string value, params string[] flags)
    {
        tokens.Add(Name("loadscene"));
        tokens.Add(Name("scene"));
        tokens.Add(Token(QbTokenType.Equals));
        tokens.Add(Token(QbTokenType.Arg));
        tokens.Add(Name(value));
        foreach (var flag in flags)
            tokens.Add(Name(flag));
        tokens.Add(Token(QbTokenType.EndOfLine));
    }

    private static void AddMember(List<QbToken> tokens, string name, QbToken value)
    {
        tokens.Add(Name(name));
        tokens.Add(Token(QbTokenType.Equals));
        tokens.Add(value);
        tokens.Add(Token(QbTokenType.EndOfLine));
    }

    private static QbToken Token(QbTokenType type) => new() { Type = type };
    private static QbToken Name(string name) => new()
    {
        Type = QbTokenType.Name,
        NameChecksum = QbChecksum.HashLower(name)
    };
    private static QbToken String(string value) => new()
    {
        Type = QbTokenType.String,
        StringValue = value
    };
    private static QbToken Integer(int value) => new()
    {
        Type = QbTokenType.Integer,
        IntValue = value
    };
}

internal static class PspCompositionTestEnumerableExtensions
{
    internal static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> values) where T : class =>
        values.Where(static value => value != null).Select(static value => value!);
}
