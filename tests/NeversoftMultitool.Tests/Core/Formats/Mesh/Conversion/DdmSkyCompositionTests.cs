using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using NeversoftMultitool.Core.QbKey;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     THPS2X retains the PS1 TRG/layout ownership graph next to its replacement
///     Xbox DDM geometry. These tests pin the exact BackgroundCreate join that
///     keeps the sky camera-locked instead of exporting it at dead bank
///     coordinates.
/// </summary>
public sealed class DdmSkyCompositionTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)";

    [Fact]
    public void Classify_ExactRegistrationsCarryLayerOrderAnchorAndBackdrop()
    {
        var farName = "far_sky";
        var nearName = "near_sky";
        var farHash = QbKey.Hash(farName);
        var nearHash = QbKey.Hash(nearName);
        var bank = new DdmFile
        {
            Objects = [CreateTriangle(farName), CreateTriangle(nearName), CreateTriangle("ground")]
        };
        var trg = new TrgFile
        {
            Nodes =
            [
                new TrgNode
                {
                    Position = new TrgPosition
                    {
                        RawX = 4096,
                        RawY = 8192,
                        RawZ = -4096
                    },
                    Commands =
                    [
                        Background(farHash),
                        Background(nearHash),
                        new TrgCommand { Opcode = 0xCA, Args = [(ushort)0x12, (ushort)0x3456] }
                    ]
                }
            ]
        };

        var result = Assert.IsType<DdmSkyClassifier.Result>(DdmSkyClassifier.Classify(bank, trg));

        Assert.Equal([0, 1], result.ObjectIndices.Order());
        Assert.Equal(0, result.LayerOrder[0]);
        Assert.Equal(1, result.LayerOrder[1]);
        Assert.Equal(0x123456u, result.SkyColor);
        Assert.Equal(new Vector3(-4096f, -8192f, -4096f), result.AnchorTransform.Translation);
    }

    [Fact]
    public void Classify_AmbiguousDdmHashDeclinesTheWholeSky()
    {
        const string duplicateName = "same_sky";
        var bank = new DdmFile
        {
            Objects = [CreateTriangle(duplicateName), CreateTriangle(duplicateName)]
        };
        var trg = new TrgFile
        {
            Nodes =
            [
                new TrgNode
                {
                    Commands = [Background(QbKey.Hash(duplicateName))]
                }
            ]
        };

        Assert.Null(DdmSkyClassifier.Classify(bank, trg));
    }

    [Fact]
    public void PopulatePlacedLevel_RepeatedSkyPlacementEmitsOneTaggedCameraLockedNode()
    {
        const string skyName = "authored_sky";
        const string groundName = "ground";
        var skyHash = QbKey.Hash(skyName);
        var groundHash = QbKey.Hash(groundName);
        var bank = new DdmFile
        {
            Objects = [CreateTriangle(skyName), CreateTriangle(groundName)]
        };
        var layout = new PsxLayoutFile
        {
            MeshNameHashes = [skyHash, groundHash],
            Objects =
            [
                new PsxLayoutObject { MeshIndex = 0, RawX = 40_960 },
                new PsxLayoutObject { MeshIndex = 0, RawX = 81_920 },
                new PsxLayoutObject { MeshIndex = 1, RawX = 12_288 }
            ]
        };
        var trg = new TrgFile
        {
            Nodes =
            [
                new TrgNode
                {
                    Position = new TrgPosition { RawX = 4096, RawY = -8192, RawZ = 12_288 },
                    Commands = [Background(skyHash)]
                }
            ]
        };
        var sky = Assert.IsType<DdmSkyClassifier.Result>(DdmSkyClassifier.Classify(bank, trg));
        var document = new ModelDocument
        {
            Name = "ddm_sky",
            SourceKind = ModelSourceKind.DdmPlacedLevel
        };

        DdmGeometryWriter.PopulateDdmPlacedLevel(
            document,
            new DdmFile { Objects = [] },
            null,
            bank,
            layout,
            null,
            objectSky: sky);

        var skyNode = Assert.Single(document.Nodes, static node =>
            node.Name.StartsWith("sky__", StringComparison.Ordinal));
        Assert.Equal(new Vector3(-4096f, 8192f, 12_288f), skyNode.Transform.Translation);
        Assert.Single(document.Nodes, static node => node.Name.Contains(groundName));

        var skyMesh = document.Meshes[Assert.IsType<int>(skyNode.MeshIndex)];
        Assert.StartsWith("sky__", skyMesh.Name, StringComparison.Ordinal);
        var metadata = Assert.Single(skyMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxSkyRenderMetadata>());
        Assert.Equal(0, metadata.LayerIndex);
    }

    [CorpusFact]
    public void CompleteThps2xLevelFamily_EveryBackgroundRegistrationResolvesExactly()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample/Builds is not available");
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, Build);
        Assert.SkipWhen(!Directory.Exists(buildRoot), "THPS2X final build is not available");

        var allFiles = Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(Path.GetFullPath, StringComparer.OrdinalIgnoreCase);
        var ddmFiles = allFiles.Values
            .Where(static path => path.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var levelCount = 0;
        var levelsWithSky = 0;
        var registrationCommands = 0;
        var uniqueSkyObjects = 0;

        foreach (var levelDdmPath in ddmFiles)
        {
            var directory = Path.GetDirectoryName(levelDdmPath)!;
            var stem = Path.GetFileNameWithoutExtension(levelDdmPath);
            if (!allFiles.TryGetValue(Path.GetFullPath(Path.Combine(directory, stem + "_t.trg")),
                    out var trgPath)
                || !allFiles.TryGetValue(Path.GetFullPath(Path.Combine(directory, stem + "_o.ddm")),
                    out var objectDdmPath))
            {
                continue;
            }

            levelCount++;
            var trg = TrgFile.Parse(trgPath);
            var registrations = PsxSkyDomeClassifier.CollectBackgroundRegistrations(
                trg, out _);
            registrationCommands += trg.Nodes.Sum(static node =>
                node.Commands?.Count(static command => command.Opcode == 0xAB) ?? 0);

            var bank = DdmFile.Parse(objectDdmPath);
            var sky = DdmSkyClassifier.Classify(bank, trg);
            if (registrations.Count == 0)
            {
                Assert.Null(sky);
                continue;
            }

            levelsWithSky++;
            var resolved = Assert.IsType<DdmSkyClassifier.Result>(sky);
            Assert.Equal(registrations.Count, resolved.ObjectIndices.Count);
            uniqueSkyObjects += resolved.ObjectIndices.Count;
            Assert.All(resolved.ObjectIndices, objectIndex =>
            {
                var name = bank.Objects[objectIndex].Name;
                Assert.True(
                    name.Contains("sky", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("background", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("bak", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("neb", StringComparison.OrdinalIgnoreCase),
                    $"TRG background resolved to unexpected object {stem}/{name}");
            });
        }

        Assert.Equal(24, levelCount);
        Assert.Equal(20, levelsWithSky);
        Assert.Equal(500, registrationCommands);
        Assert.Equal(25, uniqueSkyObjects);
    }

    [CorpusFact]
    public void Skhvn_EndToEndExportTagsAllSixOrderedSkyLayers()
    {
        var path = paths.FindSampleFile(Build, "skhvn.DDM");
        Assert.SkipWhen(path == null, "THPS2X skhvn.DDM is not available");

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path!),
            FileName = Path.GetFileName(path),
            OutputStem = "skhvn",
            SourceKind = ModelSourceKind.Ddm,
            HasPlacedPsxCompanion = true
        });

        var skyMeshes = document.Meshes
            .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
            .ToArray();
        // One authored layer has two draw-order passes, so six registered
        // objects legitimately become seven render meshes/nodes.
        Assert.Equal(7, skyMeshes.Length);
        Assert.Equal(
            [0, 1, 2, 3, 4, 5],
            skyMeshes.SelectMany(static mesh => mesh.Primitives)
                .SelectMany(static primitive => primitive.NativeMetadata)
                .OfType<PsxSkyRenderMetadata>()
                .Select(static metadata => metadata.LayerIndex)
                .Distinct()
                .Order());
        Assert.Equal(7, document.Nodes.Count(static node =>
            node.Name.StartsWith("sky__", StringComparison.Ordinal)));
        Assert.Single(document.NativeMetadata.OfType<PsxSkyBackdropMetadata>());

        var trg = TrgFile.Parse(Path.Combine(Path.GetDirectoryName(path!)!, "skhvn_t.trg"));
        var anchor = trg.Nodes.First(static node =>
            node.Commands?.Any(static command => command.Opcode == 0xAB) == true).Position!;
        var expectedAnchor = new Vector3(-anchor.RawX, -anchor.RawY, anchor.RawZ);
        Assert.All(document.Nodes.Where(static node =>
            node.Name.StartsWith("sky__", StringComparison.Ordinal)),
            node => Assert.Equal(expectedAnchor, node.Transform.Translation));
    }

    private static TrgCommand Background(uint checksum) => new()
    {
        Opcode = 0xAB,
        Args = ["0x" + checksum.ToString("X8", CultureInfo.InvariantCulture)]
    };

    private static DdmObject CreateTriangle(string name)
    {
        return new DdmObject
        {
            Name = name,
            Checksum = QbKey.Hash(name),
            Materials =
            [
                new DdmMaterial
                {
                    Name = "material",
                    TextureName = "No_Texture_Map",
                    DiffuseR = 255,
                    DiffuseG = 255,
                    DiffuseB = 255,
                    DiffuseA = 255
                }
            ],
            Vertices =
            [
                new DdmVertex(0, 0, 0, 0, 0, 1, 255, 255, 255, 255, 0, 0),
                new DdmVertex(1, 0, 0, 0, 0, 1, 255, 255, 255, 255, 1, 0),
                new DdmVertex(0, 1, 0, 0, 0, 1, 255, 255, 255, 255, 0, 1)
            ],
            Indices = [0, 1, 2],
            Splits = [new DdmSplit(0, 0, 3)]
        };
    }
}
