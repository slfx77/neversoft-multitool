using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxInlineCollisionGeometryWriterTests(
    TestPaths paths,
    ITestOutputHelper output)
{
    private const string ApocalypseBuild = "Apocalypse (1998-11-17, PSX - Final)";
    private const string SpiderManWindowsBuild = "Spider-Man (2001-9-17, PC - Final)";

    private static readonly string[] CorpusBuilds =
    [
        ApocalypseBuild,
        "Spider-Man (2000-2-4, PSX - Prototype)",
        "Spider-Man (2000-2-18, PSX - Prototype)",
        "Spider-Man (2000-4-29, PSX - Prototype)",
        "Spider-Man (2000-6-12, PSX - Prototype)",
        "Spider-Man (2000-9-1, PSX - Final)",
        "Spider-Man (2001-2-14, DC - Prototype)",
        "Spider-Man 2 - Enter Electro (2001-8-14, PSX - Prototype)",
        "Spider-Man 2 - Enter Electro (2001-8-15, PSX - Final)",
        "Spider-Man 2 - Enter Electro (2001-9-28, PSX - Rev1)",
        "Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)",
        "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)",
        "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)",
        "Tony Hawk's Pro Skater 2 (2000-5-9, PSX - Demo)",
        "Tony Hawk's Pro Skater 2 (2000-6-2, PSX - Prototype)",
        "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)",
        "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)",
        "Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)",
        "Tony Hawk's Pro Skater 4 (2002-9-28, PSX - Final)",
        SpiderManWindowsBuild
    ];

    /// <summary>
    ///     Exact Apocalypse environment regions registered by shipped
    ///     0x80 SpoolEnv commands. Bare death/war are separately 0x7E SpoolIn
    ///     actor supers and must not inherit collision identity merely because
    ///     the object-bank resolver selects them as attachment owners.
    /// </summary>
    private static readonly string[] ApocalypseCollisionSources =
    [
        "bst_1.psx",
        "city_1.psx", "city_2.psx", "city_3.psx", "city_4.psx", "city_5.psx",
        "city_6.psx", "city_7.psx", "city_8.psx", "city_8a.psx", "city_9.psx",
        "city_10.psx",
        "death_1.psx",
        "grav_1.psx", "grav_2.psx", "grav_3.psx", "grav_4.psx",
        "pest_1.psx",
        "prsn_1.psx", "prsn_2a.psx", "prsn_2b.psx", "prsn_2c.psx", "prsn_3.psx",
        "prsn_4.psx", "prsn_5.psx", "prsn_6.psx", "prsn_tnk.psx",
        "roof_1.psx", "roof_2.psx", "roof_3.psx", "roof_4.psx", "roof_5.psx",
        "roof_6.psx", "roof_7.psx",
        "sewr_1a.psx", "sewr_1b.psx", "sewr_2.psx", "sewr_3.psx", "sewr_4.psx",
        "sewr_5a.psx", "sewr_5b.psx", "sewr_6.psx", "sewr_7a.psx", "sewr_7b.psx",
        "sewr_crc.psx",
        "tube_1.psx", "tube_2.psx", "tube_2b.psx", "tube_3.psx", "tube_4.psx",
        "tube_5.psx", "tube_6.psx", "tube_7.psx", "tube_8.psx", "tube_9.psx",
        "tube_10.psx", "tube_11.psx", "tube_12.psx",
        "war_1.psx", "war_2.psx", "war_3.psx", "war_4.psx", "war_5.psx",
        "wh_1.psx", "wh_2.psx", "wh_3.psx",
        "int_1.psx", "int_2.psx", "int_3.psx"
    ];

    private static readonly IReadOnlyDictionary<string, BuildCoverage> ExpectedCoverage =
        new Dictionary<string, BuildCoverage>(StringComparer.Ordinal)
        {
            [CorpusBuilds[0]] = new(69, 11, 121_333, 6_309, 231_604, 195_710, 97, 15),
            [CorpusBuilds[1]] = new(1, 1, 2_663, 37, 5_056, 4_402, 4, 12),
            [CorpusBuilds[2]] = new(31, 31, 57_695, 4_333, 111_418, 95_694, 58, 22),
            [CorpusBuilds[3]] = new(57, 57, 81_813, 5_404, 157_274, 132_184, 31, 26),
            [CorpusBuilds[4]] = new(66, 66, 100_416, 6_411, 190_441, 159_383, 52, 26),
            [CorpusBuilds[5]] = new(66, 66, 103_179, 7_494, 195_884, 162_450, 111, 27),
            [CorpusBuilds[6]] = new(15, 15, 35_769, 3_452, 69_440, 57_713, 23, 22),
            [CorpusBuilds[7]] = new(44, 44, 71_461, 3_933, 135_138, 121_704, 62, 42),
            [CorpusBuilds[8]] = new(44, 44, 71_461, 3_933, 135_138, 121_704, 62, 42),
            [CorpusBuilds[9]] = new(44, 44, 71_184, 3_974, 134_757, 121_569, 60, 41),
            [CorpusBuilds[10]] = new(12, 8, 43_186, 2_797, 77_114, 58_842, 152, 20),
            [CorpusBuilds[11]] = new(20, 9, 63_821, 8_327, 121_718, 92_501, 8, 71),
            [CorpusBuilds[12]] = new(2, 1, 5_466, 1_195, 11_672, 9_062, 0, 23),
            [CorpusBuilds[13]] = new(40, 20, 125_607, 16_763, 244_559, 192_982, 64, 101),
            [CorpusBuilds[14]] = new(0, 0, 0, 0, 0, 0, 0, 0),
            [CorpusBuilds[15]] = new(42, 21, 129_422, 20_057, 254_036, 197_124, 63, 113),
            [CorpusBuilds[16]] = new(21, 11, 70_805, 11_817, 141_491, 102_347, 55, 95),
            [CorpusBuilds[17]] = new(19, 10, 95_754, 9_140, 169_165, 130_376, 706, 79),
            [CorpusBuilds[18]] = new(10, 9, 42_051, 3_532, 79_051, 62_794, 16, 61),
            [CorpusBuilds[19]] = new(67, 67, 103_375, 7_439, 197_013, 163_702, 75, 27)
        };

    [Fact]
    public void PopulateOverlay_UsesRenderBasisAndIncludesLoaderInvisibleFaces()
    {
        var level = CreateLevel();
        var document = new ModelDocument
        {
            Name = "level",
            SourceKind = ModelSourceKind.Psx
        };

        var added = PsxInlineCollisionGeometryWriter.PopulateOverlay(document, level);

        Assert.Equal(3, added);
        Assert.Equal(3, document.TriangleCount);
        var mesh = Assert.Single(document.Meshes);
        Assert.Equal(2, mesh.Primitives.Count);
        Assert.Contains(mesh.Primitives, primitive =>
            primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()
                .Single() == new PsxCollisionFlagsRenderMetadata(0x1234, false));
        Assert.Contains(mesh.Primitives, primitive =>
            primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()
                .Single() == new PsxCollisionFlagsRenderMetadata(0xBEEF, true));

        // The raw render word controls texture/blend/invisibility; the distinct
        // halfword after NormalIndex is the collision surface classification.
        Assert.Equal((ushort)0x0000, level.Meshes[0].Faces[0].Flags);
        Assert.Equal((ushort)0x1234, level.Meshes[0].Faces[0].CollisionFlags);
        Assert.Equal((ushort)0x0080, level.Meshes[0].InvisibleFaces[0].Flags);
        Assert.Equal((ushort)0xBEEF, level.Meshes[0].InvisibleFaces[0].CollisionFlags);

        var visible = Assert.Single(mesh.Primitives, primitive =>
            !primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()
                .Single().LoaderInvisible);
        Assert.Equal(
            [
                new Vector3(1f, 2f, -3f),
                new Vector3(1f, 1f, -3f),
                new Vector3(2f, 2f, -3f)
            ],
            visible.Vertices.Select(static vertex => vertex.Position));
    }

    [Fact]
    public void PopulateOverlay_UsesRawSpriteWordsAndOmitsRuntimeNonCollisionClass()
    {
        var collidableSprite = new PsxFace
        {
            Flags = 0x0000,
            CollisionFlags = 0x0100,
            Index0 = 0,
            Index1 = 1,
            Index2 = 2
        };
        var runtimeSkipped = new PsxFace
        {
            Flags = 0x0000,
            CollisionFlags = 0x0101,
            Index0 = 0,
            Index1 = 1,
            Index2 = 3
        };
        var level = new PsxMeshFile
        {
            Version = 0x06,
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes =
            [
                new PsxMesh
                {
                    Vertices =
                    [
                        new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                        new PsxVertex { X = 0f, Y = 8f, Z = 0f },
                        new PsxVertex
                        {
                            X = 0f,
                            Y = 8f,
                            Z = 2f,
                            RawX = 0,
                            RawY = 8,
                            RawZ = 2,
                            Type = 0x10
                        },
                        new PsxVertex { X = 4f, Y = 0f, Z = 0f }
                    ],
                    Normals = [],
                    Faces = [collidableSprite, runtimeSkipped],
                    FaceReadInfos =
                    [
                        CreateFaceReadInfo(0, collidableSprite),
                        CreateFaceReadInfo(1, runtimeSkipped)
                    ]
                }
            ],
            MeshNameHashes = [0],
            TextureHashes = [],
            ScaleDivisor = 1f,
            TranslationDivisor = 1f
        };
        var document = new ModelDocument { Name = "sprite_collision" };

        Assert.True(PsxInlineCollisionGeometryWriter.CanPopulate(level));
        Assert.Equal(1, PsxInlineCollisionGeometryWriter.PopulateOverlay(document, level));

        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.Equal(
            new PsxCollisionFlagsRenderMetadata(0x0100, false),
            Assert.Single(primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()));
        Assert.Equal(
            [
                Vector3.Zero,
                new Vector3(0f, -8f, -2f),
                new Vector3(0f, -8f, 0f)
            ],
            primitive.Vertices.Select(static vertex => vertex.Position));

        // The ordinary render writer resolves vertex 2 around the 0→1 axis
        // to (-3.2, 0, 0). Collision uses the untouched serialized words.
        Assert.DoesNotContain(
            new Vector3(-PsxSpriteVertexResolver.VerticalAxisWidthFactor * 2f, 0f, 0f),
            primitive.Vertices.Select(static vertex => vertex.Position));
        Assert.DoesNotContain(document.Meshes.SelectMany(static mesh => mesh.Primitives), primitive =>
            primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()
                .Any(static metadata => metadata.CollisionFlags == 0x0101));
    }

    [Theory]
    [InlineData(0x0100, true)]
    [InlineData(0x0101, false)]
    [InlineData(0x0102, true)]
    [InlineData(0x0103, true)]
    [InlineData(0x0005, false)]
    public void RuntimeCollisionFaceClass_UsesOnlyTheLowTwoBits(
        ushort collisionFlags,
        bool expected)
    {
        Assert.Equal(expected, PsxInlineCollisionGeometryWriter.ParticipatesInRuntimeCollision(
            new PsxFace { CollisionFlags = collisionFlags }));
    }

    [Fact]
    public void ExactCollisionLevelIdentity_RejectsStandalonePropsAndCharacterBanks()
    {
        var geometry = CreateLevel();

        // CanPopulate is intentionally a geometry gate, not a source-identity
        // classifier: a standalone rigid prop can have perfectly usable faces.
        Assert.True(PsxInlineCollisionGeometryWriter.CanPopulate(geometry));
        foreach (var fileName in new[]
                 {
                     "bench.psx", "items.psx", "skater.psx", "warehouse_o.psx"
                 })
        {
            var source = new CompanionAvailabilitySource();
            Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                source, fileName, out _));
            Assert.False(MeshCompanionResolver.HasSupportedLevelObjectCompanion(
                source, fileName));
        }

        // The general scene resolver still recognizes Spider-Man's *_g role
        // without a bank. Collision additionally requires an exact SpoolEnv
        // registration, or the same-stem VAB compatibility marker used by six
        // legacy PS1/DC packages.
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new CompanionAvailabilitySource(), "l1a1_g.psx", out _));
        Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            new CompanionAvailabilitySource(), "l1a1_g.psx", out _));
        var legacyConsoleSource = new CompanionBytesSource(
            "l1a1_g.psx",
            ("l1a1_t.trg", BuildV2SpoolEnvironmentTrg("different_g", versionMinor: 1)),
            ("l1a1.vab", []));
        Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            legacyConsoleSource, "l1a1_g.psx", out var levelStem));
        Assert.Equal("l1a1", levelStem);
        Assert.False(MeshCompanionResolver.HasSupportedLevelObjectCompanion(
            legacyConsoleSource, "l1a1_g.psx"));
    }

    [Fact]
    public void ArchiveCollisionIdentity_RequiresBankAndTriggerInSelectedDirectory()
    {
        var (wadPath, tempDirectory) = BuildWadOnDisk(
            ("levels/a/city_1.psx", new byte[] { 0x01 }),
            ("levels/b/city_obj.psx", new byte[] { 0x02 }),
            ("levels/b/city_t.trg", BuildV2SpoolEnvironmentTrg("city_1")),
            ("levels/a/roof_1.psx", new byte[] { 0x03 }),
            ("levels/a/roof_obj.psx", new byte[] { 0x04 }),
            ("levels/a/roof_t.trg", BuildV2SpoolEnvironmentTrg("roof_1")));
        ArchiveAssetBackend? backend = null;

        try
        {
            backend = ArchiveAssetBackend.TryOpen(wadPath);
            Assert.NotNull(backend);

            var remoteOnlyEntry = backend!.FindByPath("levels/a/city_1.psx");
            Assert.NotNull(remoteOnlyEntry);
            var remoteOnlySource = new ArchiveAssetSource(backend, remoteOnlyEntry!);

            // Preserve the general/object attachment contract: its unique
            // archive-wide fallback still sees the remote bank and TRG.
            Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
                remoteOnlySource, "city_1.psx", out _));
            Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                remoteOnlySource, "city_1.psx", out _));

            var localEntry = backend.FindByPath("levels/a/roof_1.psx");
            Assert.NotNull(localEntry);
            var localSource = new ArchiveAssetSource(backend, localEntry!);
            Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                localSource, "roof_1.psx", out var levelStem));
            Assert.Equal("roof", levelStem);
        }
        finally
        {
            backend?.FileSystem.Dispose();
            Directory.Delete(tempDirectory, true);
        }
    }

    [CorpusFact]
    public void ApocalypseSpoolEnvironmentIdentity_AdmitsOnlyRegisteredChunks()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample builds not available");
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, ApocalypseBuild);
        Assert.True(Directory.Exists(buildRoot), $"Missing corpus build: {ApocalypseBuild}");

        var accepted = Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file)
                .Equals(".psx", StringComparison.OrdinalIgnoreCase))
            .Where(file => MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                new FileSystemAssetSource(file), Path.GetFileName(file), out _))
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            ApocalypseCollisionSources.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase),
            accepted,
            StringComparer.OrdinalIgnoreCase);

        var city2 = paths.FindSampleFile(ApocalypseBuild, "city_2.psx");
        Assert.NotNull(city2);
        var citySource = new FileSystemAssetSource(city2!);
        Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            citySource, "city_2.psx", out var cityStem));
        Assert.Equal("city", cityStem);
        Assert.False(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            citySource, "city_2.psx", out _));

        // Filename shape plus matching *_obj/TRG siblings is insufficient:
        // roof_8 ships beside the other chunks but no SpoolEnv command names it.
        var roof8 = paths.FindSampleFile(ApocalypseBuild, "roof_8.psx");
        Assert.NotNull(roof8);
        Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            new FileSystemAssetSource(roof8!), "roof_8.psx", out _));
    }

    [CorpusFact]
    public void ApocalypseSpoolInActorSupers_AreRejectedAndCombinedWriterFailsWithoutMutation()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample builds not available");

        foreach (var fileName in new[] { "death.psx", "war.psx" })
        {
            var path = paths.FindSampleFile(ApocalypseBuild, fileName);
            Assert.NotNull(path);
            var source = new FileSystemAssetSource(path!);

            // These names still own the shared object-layer attachment under
            // the existing contract, but the TRG registers them as SpoolIn
            // actors rather than SpoolEnv level regions.
            Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
                source, fileName, out _));
            Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                source, fileName, out _));

            var actor = PsxMeshFile.Parse(path!);
            Assert.NotNull(actor);
            var usesCombinedAssembly = PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(actor!);
            Assert.Equal(fileName == "war.psx", usesCombinedAssembly);
            if (!usesCombinedAssembly)
                continue;

            Assert.False(PsxInlineCollisionGeometryWriter.CanPopulate(actor));

            var document = new ModelDocument { Name = Path.GetFileNameWithoutExtension(fileName) };
            Assert.Equal(0, PsxInlineCollisionGeometryWriter.PopulateOverlay(document, actor));
            Assert.Empty(document.Materials);
            Assert.Empty(document.Meshes);
            Assert.Empty(document.Nodes);
            Assert.Empty(document.NativeMetadata);
            Assert.Equal(0, document.TriangleCount);
        }
    }

    [CorpusFact]
    public void V20SpoolEnvironmentIdentity_AdmitsThpsPrototypeSubAll()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample builds not available");
        var build = CorpusBuilds[10];
        var subAll = paths.FindSampleFile(build, "sub_all.psx");
        var subObjectBank = paths.FindSampleFile(build, "sub_obj.psx");
        Assert.NotNull(subAll);
        Assert.NotNull(subObjectBank);

        var source = new FileSystemAssetSource(subAll!);
        Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            source, "sub_all.psx", out var levelStem));
        Assert.Equal("sub", levelStem);
        Assert.False(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            source, "sub_all.psx", out _));
        Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            new FileSystemAssetSource(subObjectBank!), "sub_obj.psx", out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void SpiderManGeometryRole_RequiresSupportedExactSpoolEnvironment(
        int versionMinor,
        bool expected)
    {
        var source = new CompanionBytesSource(
            "l1a1_g.psx",
            ("l1a1_t.trg", BuildV2SpoolEnvironmentTrg("l1a1_g", versionMinor)));

        Assert.Equal(expected, MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            source, source.EntryName, out var levelStem));
        Assert.Equal(expected ? "l1a1" : string.Empty, levelStem);
    }

    [Fact]
    public void SpiderManGeometryRole_MismatchedRegistrationNeedsLegacyVabMarker()
    {
        var trigger = BuildV2SpoolEnvironmentTrg("different_g", versionMinor: 1);
        var withoutVab = new CompanionBytesSource(
            "l1a1_g.psx",
            ("l1a1_t.trg", trigger));
        var withVab = new CompanionBytesSource(
            "l1a1_g.psx",
            ("l1a1_t.trg", trigger),
            ("l1a1.vab", []));

        Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            withoutVab, withoutVab.EntryName, out _));
        Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            withVab, withVab.EntryName, out var levelStem));
        Assert.Equal("l1a1", levelStem);

        var vabWithoutTrigger = new CompanionBytesSource(
            "l1a1_g.psx",
            ("l1a1.vab", []));
        Assert.False(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
            vabWithoutTrigger, vabWithoutTrigger.EntryName, out _));
    }

    [Fact]
    public void FileSystemIdentity_ResolvesUppercaseConsoleCompanions()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "nmt_psx_collision_case_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var zartPath = Path.Combine(tempDirectory, "ZART_G.PSX");
            File.WriteAllBytes(zartPath, []);
            File.WriteAllBytes(
                Path.Combine(tempDirectory, "ZART_T.TRG"),
                BuildV2SpoolEnvironmentTrg("different_G", versionMinor: 1));
            File.WriteAllBytes(Path.Combine(tempDirectory, "ZART.VAB"), []);

            var zart = new FileSystemAssetSource(zartPath);
            Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                zart, zart.EntryName, out var zartStem));
            Assert.Equal("ZART", zartStem);

            var skatePath = Path.Combine(tempDirectory, "SKB1.PSX");
            File.WriteAllBytes(skatePath, []);
            File.WriteAllBytes(Path.Combine(tempDirectory, "SKB1_O.PSX"), []);
            File.WriteAllBytes(Path.Combine(tempDirectory, "SKB1_T.TRG"), []);

            var skate = new FileSystemAssetSource(skatePath);
            Assert.True(MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                skate, skate.EntryName, out var skateStem));
            Assert.Equal("SKB1", skateStem);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [CorpusFact]
    public void SpiderManWindowsSpoolEnvironmentIdentity_RejectsOnlyUnregisteredZArt()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample builds not available");
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, SpiderManWindowsBuild);
        Assert.True(Directory.Exists(buildRoot), $"Missing corpus build: {SpiderManWindowsBuild}");

        var candidates = Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file)
                .Equals(".psx", StringComparison.OrdinalIgnoreCase))
            .Where(static file => Path.GetFileNameWithoutExtension(file)
                .EndsWith("_g", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var rejected = candidates
            .Where(file => !MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                new FileSystemAssetSource(file), Path.GetFileName(file), out _))
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(68, candidates.Length);
        Assert.Equal(["zArt_G.psx"], rejected, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PopulateOverlay_StrictPreflightRejectsIncompleteSourceWithoutMutation()
    {
        var level = CreateLevel();
        var mesh = level.Meshes[0];
        var validFace = new PsxFace
        {
            Flags = 0x0010,
            Index0 = 0,
            Index1 = 1,
            Index2 = 2
        };
        var unresolvedFace = new PsxFace
        {
            Flags = 0x0010,
            Index0 = 0,
            Index1 = 1,
            Index2 = 99
        };
        var mixedMesh = new PsxMesh
        {
            Vertices = mesh.Vertices,
            Normals = [],
            Faces = [validFace, unresolvedFace],
            FaceReadInfos =
            [
                CreateFaceReadInfo(0, validFace),
                CreateFaceReadInfo(1, unresolvedFace)
            ]
        };
        var mixedValidAndUnresolved = CopyLevel(level, meshes: [mixedMesh]);

        var outOfRangeObject = CopyLevel(
            level,
            objects: [new PsxMeshObject { MeshIndex = 1 }]);

        var incompleteMesh = new PsxMesh
        {
            Vertices = mesh.Vertices,
            Normals = mesh.Normals,
            Faces = mesh.Faces,
            InvisibleFaces = mesh.InvisibleFaces,
            FaceReadInfos = [CreateFaceReadInfo(0, mesh.Faces[0])]
        };
        var rejectedDeclaredFace = CopyLevel(level, meshes: [incompleteMesh]);

        foreach (var unusable in new[]
                 {
                     mixedValidAndUnresolved,
                     outOfRangeObject,
                     rejectedDeclaredFace
                 })
        {
            var document = new ModelDocument { Name = "level" };
            Assert.False(PsxInlineCollisionGeometryWriter.CanPopulate(unusable));
            Assert.Equal(0, PsxInlineCollisionGeometryWriter.PopulateOverlay(document, unusable));
            Assert.Empty(document.Materials);
            Assert.Empty(document.Meshes);
            Assert.Empty(document.Nodes);
            Assert.Empty(document.NativeMetadata);
            Assert.Equal(0, document.TriangleCount);
        }
    }

    [Fact]
    public void PopulateOverlay_DoesNotDereferenceRuntimeSkippedFaceIndices()
    {
        var level = CreateLevel();
        var mesh = level.Meshes[0];
        var validFace = mesh.Faces[0];
        var runtimeSkipped = new PsxFace
        {
            CollisionFlags = 0x0101,
            Index0 = 99,
            Index1 = 100,
            Index2 = 101
        };
        var mixedMesh = new PsxMesh
        {
            Vertices = mesh.Vertices,
            Normals = [],
            Faces = [validFace, runtimeSkipped],
            FaceReadInfos =
            [
                CreateFaceReadInfo(0, validFace),
                CreateFaceReadInfo(1, runtimeSkipped)
            ]
        };
        var mixed = CopyLevel(level, meshes: [mixedMesh]);
        var document = new ModelDocument { Name = "level" };

        Assert.True(PsxInlineCollisionGeometryWriter.CanPopulate(mixed));
        Assert.Equal(1, PsxInlineCollisionGeometryWriter.PopulateOverlay(document, mixed));
        Assert.Equal(1, document.TriangleCount);
        Assert.DoesNotContain(document.Meshes.SelectMany(static item => item.Primitives), primitive =>
            primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()
                .Any(static metadata => metadata.CollisionFlags == 0x0101));
    }

    [CorpusFact]
    public void ExactLevelIdentity_AllPsxLineageSourcesEmitInlineCollision()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample builds not available");

        var rows = new List<string>();
        var coverageMismatches = new List<string>();
        long totalFiles = 0;
        long totalFamilies = 0;
        long totalVisibleFaces = 0;
        long totalInvisibleFaces = 0;
        long totalDeclaredRejects = 0;
        long totalRawTriangles = 0;
        long totalEmittedTriangles = 0;
        long totalSpriteTriangles = 0;
        long totalUnresolvedTriangles = 0;
        long totalDegenerateTriangles = 0;
        long totalRuntimeSkippedTriangles = 0;
        long totalSuperModels = 0;
        long totalNonzeroCollisionFaces = 0;
        long totalDistinctFromRenderFaces = 0;
        var allCollisionFlags = new HashSet<ushort>();
        var allAuthoredCollisionFlags = new HashSet<ushort>();

        foreach (var build in CorpusBuilds)
        {
            var buildRoot = Path.Combine(paths.SampleBuildsDir!, build);
            Assert.True(Directory.Exists(buildRoot), $"Missing corpus build: {build}");

            var accepted = new List<(string Path, string LevelStem)>();
            foreach (var file in Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories)
                         .Where(static file => Path.GetExtension(file)
                             .Equals(".psx", StringComparison.OrdinalIgnoreCase)))
            {
                var source = new FileSystemAssetSource(file);
                if (MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                        source, Path.GetFileName(file), out var levelStem))
                {
                    accepted.Add((file, levelStem));
                }
            }

            var familyCount = accepted
                .Select(static item => item.LevelStem)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            long visibleFaces = 0;
            long invisibleFaces = 0;
            long declaredRejects = 0;
            long rawTriangles = 0;
            long emittedTriangles = 0;
            long spriteTriangles = 0;
            long unresolvedTriangles = 0;
            long degenerateTriangles = 0;
            long runtimeSkippedTriangles = 0;
            long nonzeroCollisionFaces = 0;
            long distinctFromRenderFaces = 0;
            var collisionFlags = new HashSet<ushort>();
            var superModels = new List<string>();

            foreach (var (file, _) in accepted)
            {
                var level = PsxMeshFile.Parse(file);
                Assert.NotNull(level);
                Assert.All(level!.Meshes, mesh => Assert.Equal(
                    mesh.FaceReadInfos.Count,
                    mesh.Faces.Count + mesh.InvisibleFaces.Count));
                Assert.All(level.Objects, obj => Assert.True(
                    obj.MeshIndex < level.Meshes.Count,
                    $"{file}: object mesh index {obj.MeshIndex} >= {level.Meshes.Count}"));
                Assert.False(PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(level!), file);
                if (level!.IsSuperModel)
                    superModels.Add(Path.GetFileName(file));

                declaredRejects += level.Meshes.Sum(static mesh =>
                    mesh.FaceReadInfos.Count - mesh.Faces.Count - mesh.InvisibleFaces.Count);
                foreach (var obj in level.Objects)
                {
                    if (obj.MeshIndex >= level.Meshes.Count)
                        continue;

                    var mesh = level.Meshes[obj.MeshIndex];
                    visibleFaces += mesh.Faces.Count;
                    invisibleFaces += mesh.InvisibleFaces.Count;
                    foreach (var face in mesh.Faces.Concat(mesh.InvisibleFaces))
                    {
                        if (face.CollisionFlags != 0)
                            nonzeroCollisionFaces++;
                        if (face.CollisionFlags != face.Flags)
                            distinctFromRenderFaces++;
                        allAuthoredCollisionFlags.Add(face.CollisionFlags);
                        if (PsxInlineCollisionGeometryWriter.ParticipatesInRuntimeCollision(face))
                        {
                            collisionFlags.Add(face.CollisionFlags);
                            allCollisionFlags.Add(face.CollisionFlags);
                        }
                    }
                }

                var triangleAudit = AuditTriangles(level);
                rawTriangles += triangleAudit.Raw;
                spriteTriangles += triangleAudit.Sprite;
                unresolvedTriangles += triangleAudit.Unresolved;
                degenerateTriangles += triangleAudit.Degenerate;
                runtimeSkippedTriangles += triangleAudit.RuntimeSkipped;

                if (!PsxInlineCollisionGeometryWriter.CanPopulate(level))
                    continue;

                var document = new ModelDocument
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    SourceKind = ModelSourceKind.Psx
                };
                var added = PsxInlineCollisionGeometryWriter.PopulateOverlay(document, level);
                Assert.True(added > 0, file);
                Assert.Equal(triangleAudit.Usable, added);
                Assert.Equal(added, document.TriangleCount);
                Assert.Single(document.Materials);
                Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives), primitive =>
                    Assert.Single(primitive.NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()));
                emittedTriangles += added;
            }

            rows.Add(
                $"{build}: files={accepted.Count}, families={familyCount}, " +
                $"faces={visibleFaces}+{invisibleFaces}, rejects={declaredRejects}, " +
                $"rawTriangles={rawTriangles}, emitted={emittedTriangles}, " +
                $"omitted={rawTriangles - emittedTriangles} " +
                $"(runtimeSkipped={runtimeSkippedTriangles}, unresolved={unresolvedTriangles}, " +
                $"degenerate={degenerateTriangles}), " +
                $"spriteTriangles={spriteTriangles}, " +
                $"collisionFlags={collisionFlags.Count}, nonzeroCollisionFaces={nonzeroCollisionFaces}, " +
                $"distinctFromRender={distinctFromRenderFaces}, " +
                $"supers=[{string.Join(',', superModels)}]");
            var actualCoverage = new BuildCoverage(
                accepted.Count,
                familyCount,
                visibleFaces,
                invisibleFaces,
                rawTriangles,
                emittedTriangles,
                degenerateTriangles,
                collisionFlags.Count);
            if (ExpectedCoverage[build] != actualCoverage)
            {
                coverageMismatches.Add(
                    $"{build}: expected {ExpectedCoverage[build]}; actual {actualCoverage}");
            }
            totalFiles += accepted.Count;
            totalFamilies += familyCount;
            totalVisibleFaces += visibleFaces;
            totalInvisibleFaces += invisibleFaces;
            totalDeclaredRejects += declaredRejects;
            totalRawTriangles += rawTriangles;
            totalEmittedTriangles += emittedTriangles;
            totalSpriteTriangles += spriteTriangles;
            totalUnresolvedTriangles += unresolvedTriangles;
            totalDegenerateTriangles += degenerateTriangles;
            totalRuntimeSkippedTriangles += runtimeSkippedTriangles;
            totalSuperModels += superModels.Count;
            totalNonzeroCollisionFaces += nonzeroCollisionFaces;
            totalDistinctFromRenderFaces += distinctFromRenderFaces;
        }

        rows.Add(
            $"TOTAL: files={totalFiles}, families={totalFamilies}, " +
            $"faces={totalVisibleFaces}+{totalInvisibleFaces}, rejects={totalDeclaredRejects}, " +
            $"rawTriangles={totalRawTriangles}, emitted={totalEmittedTriangles}, " +
            $"omitted={totalRawTriangles - totalEmittedTriangles}, " +
            $"runtimeSkipped={totalRuntimeSkippedTriangles}, " +
            $"unresolved={totalUnresolvedTriangles}, degenerate={totalDegenerateTriangles}, " +
            $"spriteTriangles={totalSpriteTriangles}, collisionFlags={allCollisionFlags.Count}, " +
            $"nonzeroCollisionFaces={totalNonzeroCollisionFaces}, " +
            $"distinctFromRender={totalDistinctFromRenderFaces}, supers={totalSuperModels}");
        foreach (var row in rows)
            output.WriteLine(row);
        Assert.True(
            coverageMismatches.Count == 0,
            string.Join(Environment.NewLine, coverageMismatches));

        Assert.Equal(670, totalFiles);
        Assert.Equal(535, totalFamilies);
        Assert.Equal(1_396_461, totalVisibleFaces);
        Assert.Equal(126_347, totalInvisibleFaces);
        Assert.Equal(0, totalDeclaredRejects);
        Assert.Equal(2_661_969, totalRawTriangles);
        Assert.Equal(2_182_243, totalEmittedTriangles);
        Assert.Equal(478_027, totalRuntimeSkippedTriangles);
        Assert.Equal(0, totalUnresolvedTriangles);
        Assert.Equal(1_699, totalDegenerateTriangles);
        Assert.Equal(13_952, totalSpriteTriangles);
        Assert.Equal(243, allAuthoredCollisionFlags.Count);
        Assert.Equal(177, allCollisionFlags.Count);
        Assert.Equal(1_279_583, totalNonzeroCollisionFaces);
        Assert.Equal(1_522_670, totalDistinctFromRenderFaces);
        Assert.Equal(0, totalSuperModels);
    }

    private static TriangleAudit AuditTriangles(PsxMeshFile level)
    {
        long raw = 0;
        long usable = 0;
        long unresolved = 0;
        long degenerate = 0;
        long runtimeSkipped = 0;
        long sprite = 0;
        foreach (var obj in level.Objects)
        {
            if (obj.MeshIndex >= level.Meshes.Count)
                continue;

            var mesh = level.Meshes[obj.MeshIndex];
            var offset = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(level, obj));
            foreach (var face in mesh.Faces.Concat(mesh.InvisibleFaces))
            {
                Audit(face, face.Index0, face.Index2, face.Index1);
                if (face.IsQuad)
                    Audit(face, face.Index1, face.Index2, face.Index3);
            }

            void Audit(PsxFace face, uint i0, uint i1, uint i2)
            {
                raw++;
                if (IsSprite(i0) || IsSprite(i1) || IsSprite(i2))
                    sprite++;
                if (!PsxInlineCollisionGeometryWriter.ParticipatesInRuntimeCollision(face))
                {
                    runtimeSkipped++;
                }
                else if (!TryPosition(i0, out var a)
                    || !TryPosition(i1, out var b)
                    || !TryPosition(i2, out var c))
                {
                    unresolved++;
                }
                else if (ModelDocumentGeometryAdapter.IsDegenerate(a, b, c))
                {
                    degenerate++;
                }
                else
                {
                    usable++;
                }
            }

            bool IsSprite(uint index) =>
                index < mesh.Vertices.Count && mesh.Vertices[(int)index].IsSpriteVertex;

            bool TryPosition(uint index, out Vector3 position)
            {
                position = default;
                if (index >= mesh.Vertices.Count)
                    return false;

                var vertex = mesh.Vertices[(int)index];
                position = PsxMeshSemantics.ToGltfPosition(
                    new Vector3(vertex.X, vertex.Y, vertex.Z));
                position += offset;
                return true;
            }
        }

        return new TriangleAudit(raw, usable, unresolved, degenerate, runtimeSkipped, sprite);
    }

    private readonly record struct TriangleAudit(
        long Raw,
        long Usable,
        long Unresolved,
        long Degenerate,
        long RuntimeSkipped,
        long Sprite);

    private readonly record struct BuildCoverage(
        int Files,
        int Families,
        long VisibleFaces,
        long InvisibleFaces,
        long RawTriangles,
        long EmittedTriangles,
        long DegenerateTriangles,
        int CollisionFlagValues);

    private static PsxMeshFile CreateLevel()
    {
        var visible = new PsxFace
        {
            Flags = 0x0000,
            CollisionFlags = 0x1234,
            Index0 = 0,
            Index1 = 1,
            Index2 = 2
        };
        var hiddenQuad = new PsxFace
        {
            Flags = 0x0080,
            CollisionFlags = 0xBEEF,
            IsQuad = true,
            Index0 = 0,
            Index1 = 1,
            Index2 = 2,
            Index3 = 3
        };
        var mesh = new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                new PsxVertex { X = 1f, Y = 0f, Z = 0f },
                new PsxVertex { X = 0f, Y = 1f, Z = 0f },
                new PsxVertex { X = 1f, Y = 1f, Z = 0f }
            ],
            Normals = [],
            Faces = [visible],
            InvisibleFaces = [hiddenQuad],
            FaceReadInfos =
            [
                CreateFaceReadInfo(0, visible),
                CreateFaceReadInfo(1, hiddenQuad)
            ]
        };
        return new PsxMeshFile
        {
            Version = 0x06,
            Objects =
            [
                new PsxMeshObject
                {
                    MeshIndex = 0,
                    RawX = 4096,
                    RawY = -8192,
                    RawZ = 12_288
                }
            ],
            Meshes = [mesh],
            MeshNameHashes = [0],
            TextureHashes = [],
            ScaleDivisor = 1f,
            TranslationDivisor = 1f
        };
    }

    private static PsxMeshFile CopyLevel(
        PsxMeshFile source,
        List<PsxMeshObject>? objects = null,
        List<PsxMesh>? meshes = null)
    {
        return new PsxMeshFile
        {
            Version = source.Version,
            FormatRevision = source.FormatRevision,
            Objects = objects ?? source.Objects,
            Meshes = meshes ?? source.Meshes,
            MeshNameHashes = source.MeshNameHashes,
            TextureHashes = source.TextureHashes,
            GouraudPalette = source.GouraudPalette,
            ColourPulses = source.ColourPulses,
            HasHierarchy = source.HasHierarchy,
            IsSuperModel = source.IsSuperModel,
            ScaleDivisor = source.ScaleDivisor,
            TranslationDivisor = source.TranslationDivisor,
            HasStitchedReferences = source.HasStitchedReferences
        };
    }

    private static PsxFaceReadInfo CreateFaceReadInfo(int rawFaceIndex, PsxFace face)
    {
        return new PsxFaceReadInfo
        {
            RawFaceIndex = rawFaceIndex,
            Offset = 0,
            Flags = face.Flags,
            Length = 20,
            BytesConsumed = 20,
            UnderreadBytes = 0,
            OverreadBytes = 0,
            IsLengthAligned = true,
            IsAccepted = true,
            AcceptedFaceIndex = rawFaceIndex
        };
    }

    private static byte[] BuildV2SpoolEnvironmentTrg(
        string environmentStem,
        int versionMinor = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x4752545Fu); // _TRG
        writer.Write((uint)(2 | versionMinor << 16));
        writer.Write(1u);          // one node
        writer.Write(16u);         // node offset
        writer.Write((ushort)4);   // AUTOEXEC
        writer.Write((ushort)0x80);// SpoolEnv
        writer.Write(Encoding.ASCII.GetBytes(environmentStem));
        writer.Write((byte)0);
        if ((stream.Position & 1) != 0)
            writer.Write((byte)0);
        writer.Write(ushort.MaxValue);
        return stream.ToArray();
    }

    private static (string WadPath, string TempDirectory) BuildWadOnDisk(
        params (string Name, byte[] Data)[] files)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "nmt_psx_collision_wad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        using var wad = new MemoryStream();
        using var hed = new MemoryStream();
        using var writer = new BinaryWriter(hed, Encoding.ASCII, leaveOpen: true);
        foreach (var (name, data) in files)
        {
            var offset = (uint)wad.Length;
            wad.Write(data);
            writer.Write(Encoding.ASCII.GetBytes(name + "\0"));
            writer.Write(new byte[(4 - hed.Length % 4) % 4]);
            writer.Write(offset);
            writer.Write((uint)data.Length);
        }

        writer.Write((byte)0xFF);
        var wadPath = Path.Combine(tempDirectory, "TEST.WAD");
        File.WriteAllBytes(wadPath, wad.ToArray());
        File.WriteAllBytes(Path.Combine(tempDirectory, "TEST.HED"), hed.ToArray());
        return (wadPath, tempDirectory);
    }

    private sealed class CompanionAvailabilitySource(params string[] companionNames) : AssetSource
    {
        public override string DisplayName => "synthetic";
        public override string EntryName => "synthetic.psx";

        public override byte[] ReadBytes() => [];

        public override bool CompanionExists(string nameWithExtension) =>
            companionNames.Contains(nameWithExtension, StringComparer.OrdinalIgnoreCase);

        public override byte[]? TryReadCompanion(string nameWithExtension) => null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }

    private sealed class CompanionBytesSource(
        string entryName,
        params (string Name, byte[] Bytes)[] companions) : AssetSource
    {
        public override string DisplayName => entryName;
        public override string EntryName => entryName;
        public override byte[] ReadBytes() => [];

        public override bool CompanionExists(string nameWithExtension) =>
            companions.Any(item => item.Name.Equals(
                nameWithExtension, StringComparison.OrdinalIgnoreCase));

        public override byte[]? TryReadCompanion(string nameWithExtension) =>
            companions.FirstOrDefault(item => item.Name.Equals(
                nameWithExtension, StringComparison.OrdinalIgnoreCase)).Bytes;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            foreach (var extension in extensions)
            {
                var bytes = TryReadCompanion(stem + extension);
                if (bytes != null)
                    return bytes;
            }

            return null;
        }
    }
}
