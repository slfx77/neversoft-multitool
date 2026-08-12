using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxArchiveTextureResolutionTests(TestPaths paths)
{
    private const string ApocalypseBuild = "Apocalypse (1998-11-17, PSX - Final)";
    private const string Thps1Build = "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";

    [Fact]
    public void CompanionLibraryStems_ApocalypseRegion_IncludesFamilyLibrary()
    {
        var candidates = PsxTextureProviderFactory.GetCompanionLibraryStems("city_1");
        var legacyCandidates = PsxTextureProviderFactory.GetCompanionLibraryStems("death_1");

        Assert.Contains("city_lib", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("deathlib", legacyCandidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompanionLibraryStems_ApocalypseInterior_UsesCumulativeRegionLibraries()
    {
        var candidates = PsxTextureProviderFactory.GetCompanionLibraryStems("int_3").ToList();

        Assert.True(candidates.IndexOf("int3_lib") < candidates.IndexOf("int2_lib"));
        Assert.True(candidates.IndexOf("int2_lib") < candidates.IndexOf("int_lib"));
    }

    [CorpusTheory]
    [InlineData("city_1")]
    [InlineData("int_3")]
    [InlineData("death_1")]
    public void ApocalypseRegion_FromWad_ResolvesEveryUsedTextureFromFamilyLibraries(string regionStem)
    {
        var wadPath = paths.FindSampleFile(ApocalypseBuild, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Apocalypse CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry(regionStem + ".psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry);
        var parsed = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(parsed);
        var usedTextureHashes = parsed.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(static face => face.IsTextured && face.TextureHash != 0)
            .Select(static face => face.TextureHash)
            .ToHashSet();
        Assert.NotEmpty(usedTextureHashes);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = regionStem,
            SourceKind = ModelSourceKind.Psx
        });
        var embeddedTextureHashes = document.Textures
            .Where(static texture => texture.NativeChecksum.HasValue)
            .Select(static texture => texture.NativeChecksum!.Value)
            .ToHashSet();

        Assert.All(usedTextureHashes, hash => Assert.Contains(hash, embeddedTextureHashes));
    }

    [CorpusFact]
    public void Thps1Skware_FromWad_PreservesUntexturedAdditivePlanes()
    {
        var wadPath = paths.FindSampleFile(Thps1Build, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "THPS1 CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry("skware.psx");
        Assert.NotNull(entry);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new ArchiveAssetSource(backend, entry),
            FileName = entry.Name,
            OutputStem = "skware",
            SourceKind = ModelSourceKind.Psx
        });

        var materialIndex = document.Materials.FindIndex(static material =>
            material.Name == "untextured__st1");
        Assert.True(materialIndex >= 0, "Expected an untextured ABR1 material");
        var material = document.Materials[materialIndex];
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.Null(material.TextureIndex);

        var additiveVertices = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Where(primitive => primitive.MaterialIndex == materialIndex)
            .SelectMany(static primitive => primitive.Vertices)
            .ToArray();
        Assert.NotEmpty(additiveVertices);
        Assert.Contains(additiveVertices, static vertex => vertex.Color.W < 1f);
    }

    [CorpusTheory]
    [InlineData(0x52158AD3u, 73)] // truck: wt*.bmp authored mask
    [InlineData(0x59BD3DD8u, 72)] // wheel: w_*.bmp authored mask
    public void Thps1Hawk_FromWad_AppliesAuthoredEquipmentCutout(
        uint textureHash,
        int expectedTransparentPixels)
    {
        var wadPath = paths.FindSampleFile(Thps1Build, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "THPS1 CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry("hawk.psx");
        Assert.NotNull(entry);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new ArchiveAssetSource(backend, entry),
            FileName = entry.Name,
            OutputStem = "hawk",
            SourceKind = ModelSourceKind.Psx
        });

        var textureIndex = document.Textures.FindIndex(texture =>
            texture.NativeChecksum == textureHash);
        Assert.True(textureIndex >= 0, $"Expected texture 0x{textureHash:X8}");
        Assert.Contains(document.Materials, material =>
            material.TextureIndex == textureIndex && material.AlphaMode == ModelAlphaMode.Mask);

        var pngBytes = document.Textures[textureIndex].PngBytes;
        Assert.NotNull(pngBytes);
        using var image = Image.Load<Rgba32>(pngBytes);
        var transparentPixels = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    if (row[x].A == 0)
                        transparentPixels++;
            }
        });

        Assert.Equal(expectedTransparentPixels, transparentPixels);
    }
}
