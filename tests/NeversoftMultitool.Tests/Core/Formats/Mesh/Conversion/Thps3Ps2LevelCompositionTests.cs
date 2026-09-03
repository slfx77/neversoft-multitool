using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class Thps3Ps2LevelCompositionTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    [Fact]
    public void Writer_AppendsMaterialWindowsAndTagsOnlySkyPrimitives()
    {
        var sky = World("sky_texture");
        var level = World("level_texture");
        var requested = new List<string>();
        MeshNamedTextureResolver resolver = name =>
        {
            requested.Add(name);
            return null;
        };
        var document = new ModelDocument { Name = "level", SourceKind = ModelSourceKind.RenderWareBsp };
        document.Materials.Add(new RenderMaterial { Name = "sky" });
        document.Materials.Add(new RenderMaterial { Name = "level" });

        RwBspGeometryWriter.PopulateRwBsp(
            document, sky, resolver, 0, "sky__", new PsxSkyRenderMetadata(), "sky__");
        RwBspGeometryWriter.PopulateRwBsp(
            document, level, resolver, 1, textureNamePrefix: "level__");

        Assert.Equal(2, document.TriangleCount);
        Assert.Equal(2, document.Meshes.Count);
        Assert.Equal(0, Assert.Single(document.Meshes[0].Primitives).MaterialIndex);
        Assert.Equal(1, Assert.Single(document.Meshes[1].Primitives).MaterialIndex);
        Assert.StartsWith("sky__", document.Meshes[0].Name, StringComparison.Ordinal);
        Assert.False(document.Meshes[1].Name.StartsWith("sky__", StringComparison.Ordinal));
        Assert.Single(document.Meshes[0].Primitives[0].NativeMetadata.OfType<PsxSkyRenderMetadata>());
        Assert.Empty(document.Meshes[1].Primitives[0].NativeMetadata.OfType<PsxSkyRenderMetadata>());
        Assert.Equal(["sky_texture", "level_texture"], requested);
    }

    [Fact]
    public void Writer_EmitsAuthoredUntexturedSkyButStillOmitsDevMaterials()
    {
        var sky = World(null);
        var dev = World("wire");
        var document = new ModelDocument { Name = "sky", SourceKind = ModelSourceKind.RenderWareBsp };
        document.Materials.Add(new RenderMaterial { Name = "untextured" });
        document.Materials.Add(new RenderMaterial { Name = "wire" });

        RwBspGeometryWriter.PopulateRwBsp(
            document,
            sky,
            textureProvider: null,
            materialStartIndex: 0,
            namePrefix: "sky__",
            primitiveMetadata: new PsxSkyRenderMetadata(),
            includeUntexturedMaterials: true);
        RwBspGeometryWriter.PopulateRwBsp(
            document,
            dev,
            textureProvider: null,
            materialStartIndex: 1,
            namePrefix: "dev__",
            includeUntexturedMaterials: true);

        Assert.Single(document.Meshes.SelectMany(static mesh => mesh.Primitives));
        Assert.Single(document.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxSkyRenderMetadata>());
    }

    [CorpusFact]
    public void Corpus_AllThirteenAuthoredMainLevelsComposeWithExactSkyPolicy()
    {
        var manifest = FindShippingManifest();
        Assert.SkipWhen(manifest == null, "THPS3 PS2 build is not available");
        Assert.True(Thps3Ps2LevelManifest.TryParse(
            NeversoftMultitool.Core.Formats.Qb.QbFile.Parse(manifest!), out var entries));
        var skate3 = Directory.GetParent(Path.GetDirectoryName(manifest!)!)!.FullName;
        var pre = Path.Combine(skate3, "pre");
        var allBsp = Directory.EnumerateFiles(pre, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var mains = allBsp
            .Where(path => Thps3Ps2LevelManifest.TryResolve(path, out _))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(13, mains.Length);

        var parser = new MeshModelParser();
        var composedSkies = 0;
        var authoredBackdrops = 0;
        foreach (var main in mains)
        {
            var source = new FileSystemAssetSource(main);
            var document = parser.Parse(new MeshImportRequest
            {
                Source = source,
                FileName = source.EntryName,
                OutputStem = Path.GetFileNameWithoutExtension(main),
                SourceKind = ModelSourceKind.RenderWareBsp
            });

            var composition = Assert.Single(
                document.NativeMetadata.OfType<Thps3Ps2LevelCompositionMetadata>());
            var expected = Assert.Single(entries,
                entry => entry.LevelAssetPath.Equals(composition.LevelAssetPath,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(expected.SkyAssetPath, composition.SkyAssetPath, ignoreCase: true);
            Assert.NotEmpty(document.Meshes);
            Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives), primitive =>
                Assert.InRange(primitive.MaterialIndex, 0, document.Materials.Count - 1));

            var skyPrimitives = document.Meshes
                .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
                .SelectMany(static mesh => mesh.Primitives)
                .ToArray();
            if (expected.SkyAssetPath == null)
            {
                Assert.Empty(skyPrimitives);
            }
            else
            {
                Assert.True(skyPrimitives.Length > 0,
                    $"{expected.DisplayName} authored {expected.SkyAssetPath}, but emitted no sky primitives");
                Assert.All(skyPrimitives, primitive =>
                    Assert.Single(primitive.NativeMetadata.OfType<PsxSkyRenderMetadata>()));
                composedSkies++;
            }

            var backdrops = document.NativeMetadata.OfType<PsxSkyBackdropMetadata>().ToArray();
            if (expected.BackgroundColor.HasValue)
            {
                Assert.Equal(expected.BackgroundColor.Value, Assert.Single(backdrops).SkyColor);
                authoredBackdrops++;
            }
            else
            {
                Assert.Empty(backdrops);
            }
        }

        Assert.Equal(11, composedSkies);
        Assert.Equal(11, authoredBackdrops);
    }

    [CorpusFact]
    public void Corpus_TutorialsExceptionalSkyExportsAComposedGlb()
    {
        var manifest = FindShippingManifest();
        Assert.SkipWhen(manifest == null, "THPS3 PS2 build is not available");
        var skate3 = Directory.GetParent(Path.GetDirectoryName(manifest!)!)!.FullName;
        var tut = Directory.EnumerateFiles(Path.Combine(skate3, "pre"), "Tut.bsp",
                SearchOption.AllDirectories)
            .Single(path => path.Replace('\\', '/').EndsWith("/Levels/Tut/Tut.bsp",
                StringComparison.OrdinalIgnoreCase));
        var source = new FileSystemAssetSource(tut);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Tut",
            SourceKind = ModelSourceKind.RenderWareBsp
        });

        var composition = Assert.Single(
            document.NativeMetadata.OfType<Thps3Ps2LevelCompositionMetadata>());
        Assert.Equal("Levels/Sk3Ed_Bch_Sky/Sk3Ed_Bch_Sky.bsp",
            composition.SkyAssetPath, ignoreCase: true);
        Assert.Contains(document.Meshes,
            static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal));

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(document.TriangleCount, triangles);
        Assert.True(glb.Length > 1_000_000);
    }

    [CorpusFact]
    public void Corpus_MalformedAuthoredSkyFailsOpenToStandaloneMain()
    {
        var manifest = FindShippingManifest();
        Assert.SkipWhen(manifest == null, "THPS3 PS2 build is not available");
        var sourceSkate3 = Directory.GetParent(Path.GetDirectoryName(manifest!)!)!.FullName;
        var sourcePre = Path.Combine(sourceSkate3, "pre");
        var sourceMain = Directory.EnumerateFiles(sourcePre, "Can.bsp", SearchOption.AllDirectories)
            .Single(path => path.Replace('\\', '/').EndsWith("/Levels/Can/Can.bsp",
                StringComparison.OrdinalIgnoreCase));

        using var temp = new TempDirectory();
        var skate3 = Path.Combine(temp.Path, "SKATE3");
        var copiedManifest = Path.Combine(skate3, "Scripts", "levels.qb");
        var copiedMain = Path.Combine(skate3, "pre", "Can", "Levels", "Can", "Can.bsp");
        var malformedSky = Path.Combine(
            skate3, "pre", "CanSky", "Levels", "Can_Sky", "Can_Sky.bsp");
        CopyFile(manifest!, copiedManifest);
        CopyFile(sourceMain, copiedMain);
        Directory.CreateDirectory(Path.GetDirectoryName(malformedSky)!);
        File.WriteAllBytes(malformedSky, [0x0B, 0x00, 0x00, 0x00]);

        Assert.True(Thps3Ps2LevelManifest.TryResolve(copiedMain, out _));
        var source = new FileSystemAssetSource(copiedMain);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Can",
            SourceKind = ModelSourceKind.RenderWareBsp
        });

        Assert.NotEmpty(document.Meshes);
        Assert.DoesNotContain(document.Meshes,
            static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal));
        Assert.Empty(document.NativeMetadata.OfType<Thps3Ps2LevelCompositionMetadata>());
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

    private static void CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("nmt-thps3-bsp-").FullName;
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

    private static RwBspWorld World(string? texture)
    {
        return new RwBspWorld
        {
            FormatFlags = 0,
            TotalTriangles = 1,
            TotalVertices = 3,
            Materials =
            [
                new RwMaterial
                {
                    TextureName = texture,
                    MaskName = null,
                    R = 255,
                    G = 255,
                    B = 255,
                    A = 255
                }
            ],
            Sections =
            [
                new RwBspSection
                {
                    MatListWindowBase = 0,
                    Vertices = [
                        System.Numerics.Vector3.Zero,
                        System.Numerics.Vector3.UnitX,
                        System.Numerics.Vector3.UnitY
                    ],
                    UVs = [
                        System.Numerics.Vector2.Zero,
                        System.Numerics.Vector2.UnitX,
                        System.Numerics.Vector2.UnitY
                    ],
                    Normals = null,
                    Colors = null,
                    Triangles = [new RwTriangle(0, 1, 2, 0)]
                }
            ]
        };
    }
}
