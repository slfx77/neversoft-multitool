using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class Thps4PcLevelCompositionTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";

    [Fact]
    public void Writer_AppendsMaterialsAndTagsOnlyTheAuthoredSkyPart()
    {
        var sky = Scene(0x10000001, 0x20000001);
        var level = Scene(0x10000002, 0x20000002);
        var document = new ModelDocument
        {
            Name = "level",
            SourceKind = ModelSourceKind.XbxScene
        };
        document.Materials.Add(new RenderMaterial { Name = "sky" });
        document.Materials.Add(new RenderMaterial { Name = "level" });

        XbxGeometryWriter.PopulateXbxScene(
            document,
            sky,
            textureProvider: null,
            materialStartIndex: 0,
            namePrefix: "sky__",
            primitiveMetadata: new PsxSkyRenderMetadata(LayerIndex: 0));
        XbxGeometryWriter.PopulateXbxScene(
            document,
            level,
            textureProvider: null,
            materialStartIndex: 1);

        Assert.Equal(2, document.TriangleCount);
        Assert.Equal(2, document.Meshes.Count);
        Assert.StartsWith("sky__", document.Meshes[0].Name, StringComparison.Ordinal);
        Assert.False(document.Meshes[1].Name.StartsWith("sky__", StringComparison.Ordinal));
        Assert.Equal(0, Assert.Single(document.Meshes[0].Primitives).MaterialIndex);
        Assert.Equal(1, Assert.Single(document.Meshes[1].Primitives).MaterialIndex);
        Assert.Single(document.Meshes[0].Primitives[0].NativeMetadata
            .OfType<PsxSkyRenderMetadata>());
        Assert.Empty(document.Meshes[1].Primitives[0].NativeMetadata
            .OfType<PsxSkyRenderMetadata>());
    }

    [CorpusFact]
    public void Corpus_AllThirteenAuthoredLevelsComposeSkyAndOptionalShell()
    {
        var levelsQb = paths.FindSampleFile(Build, "Levels.qb");
        Assert.SkipWhen(levelsQb == null, "THPS4 PC Levels.qb is not available");
        var dataDirectory = Directory.GetParent(Path.GetDirectoryName(levelsQb!)!)!.FullName;
        var levelsDirectory = Path.Combine(dataDirectory, "levels");
        var mainScenes = Directory.EnumerateFiles(levelsDirectory, "*scn.dat", SearchOption.AllDirectories)
            .Where(path => Thps4PcLevelManifest.TryResolve(path, out _))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(13, mainScenes.Length);

        long triangles = 0;
        long meshes = 0;
        long materials = 0;
        long textures = 0;
        var shellCount = 0;
        var parser = new MeshModelParser();
        foreach (var mainPath in mainScenes)
        {
            var source = new FileSystemAssetSource(mainPath);
            var document = parser.Parse(new MeshImportRequest
            {
                Source = source,
                FileName = source.EntryName,
                OutputStem = Path.GetFileName(mainPath)[..^Thps4PcDatSceneFile.SceneSuffix.Length],
                SourceKind = ModelSourceKind.XbxScene,
                IncludeCollisionOverlay = true
            });

            var composition = Assert.Single(
                document.NativeMetadata.OfType<Thps4PcLevelCompositionMetadata>());
            Assert.Equal(Path.GetFileName(mainPath), composition.LevelSceneName, ignoreCase: true);
            Assert.EndsWith(Thps4PcDatSceneFile.SceneSuffix, composition.SkySceneName,
                StringComparison.OrdinalIgnoreCase);

            var skyMeshes = document.Meshes
                .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(skyMeshes);
            Assert.All(skyMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
                Assert.Single(primitive.NativeMetadata.OfType<PsxSkyRenderMetadata>()));

            var shellMeshes = document.Meshes
                .Where(static mesh => mesh.Name.StartsWith("shell__", StringComparison.Ordinal))
                .ToArray();
            if (composition.OuterShellSceneName == null)
            {
                Assert.Empty(shellMeshes);
            }
            else
            {
                Assert.NotEmpty(shellMeshes);
                shellCount++;
            }

            Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives), primitive =>
                Assert.InRange(primitive.MaterialIndex, 0, document.Materials.Count - 1));
            var collision = Assert.Single(
                document.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
            Assert.EndsWith("col.dat", collision.CompanionName, StringComparison.OrdinalIgnoreCase);
            Assert.True(collision.TriangleCount > 0);
            Assert.NotEmpty(document.Textures);
            triangles += document.TriangleCount;
            meshes += document.Meshes.Count;
            materials += document.Materials.Count;
            textures += document.Textures.Count;
        }

        Assert.Equal(2, shellCount);
        Assert.True(triangles > 0);
        Assert.True(meshes > 0);
        Assert.True(materials > 0);
        Assert.True(textures > 0);
    }

    [CorpusFact]
    public void Corpus_MotoxUsesHofSkyAndComposedLevelExportsGlb()
    {
        var motox = paths.FindSampleFile(Build, "Motoxscn.dat");
        Assert.SkipWhen(motox == null, "THPS4 PC Motox scene is not available");
        var source = new FileSystemAssetSource(motox!);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Motox",
            SourceKind = ModelSourceKind.XbxScene,
            IncludeCollisionOverlay = true
        });

        var composition = Assert.Single(
            document.NativeMetadata.OfType<Thps4PcLevelCompositionMetadata>());
        Assert.Equal("Hof_Skyscn.dat", composition.SkySceneName, ignoreCase: true);
        Assert.Single(document.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());

        var (glb, triangleCount) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(document.TriangleCount, triangleCount);
        Assert.True(glb.Length > 1_000_000);
    }

    private static ParsedXbxScene Scene(uint materialChecksum, uint sectorChecksum)
    {
        var vertices = new[]
        {
            Vertex(Vector3.Zero),
            Vertex(Vector3.UnitX),
            Vertex(Vector3.UnitY)
        };
        return new ParsedXbxScene
        {
            Materials =
            [
                new XbxMaterial
                {
                    Checksum = materialChecksum,
                    NameChecksum = materialChecksum,
                    NumPasses = 1,
                    Passes = [new XbxPass()]
                }
            ],
            Sectors =
            [
                new XbxSector
                {
                    Checksum = sectorChecksum,
                    BoneIndex = -1,
                    Meshes =
                    [
                        new XbxMesh
                        {
                            MaterialChecksum = materialChecksum,
                            Vertices = vertices,
                            FaceIndices = [0, 1, 2],
                            IsPreTriangulated = true
                        }
                    ]
                }
            ],
            Links = []
        };
    }

    private static XbxVertex Vertex(Vector3 position) => new()
    {
        Position = position,
        Normal = Vector3.UnitZ,
        Color = Vector4.One,
        HasNormal = true,
        HasColor = true
    };
}
