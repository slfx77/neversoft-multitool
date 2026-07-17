using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class ArchiveRwDffCopyDeduplicatorTests(TestPaths paths)
{
    private static readonly int[] ExpectedRootIndex = [0];

    [Fact]
    public void PedProMuska_Skate3Wad_SelectsOnlyRootExactCopy()
    {
        const string buildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";
        var wadPath = paths.FindSampleFile(buildName, "SKATE3.WAD");
        Assert.SkipWhen(wadPath == null, "THPS3 SKATE3.WAD not found in sample builds");

        var root = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(root);
        using var rootFileSystem = root.FileSystem;

        var rootEntry = root.FindEntry("PedPro_Muska.skn");
        Assert.NotNull(rootEntry);
        var rootSource = new ArchiveAssetSource(root, rootEntry);
        var rootMesh = rootSource.ReadBytes();
        var rootTexture = rootSource.TryReadCompanion("PedPro_Muska.tex");
        Assert.NotNull(rootTexture);

        var candidates = new List<ArchiveRwDffCopyCandidate>
        {
            new(rootEntry.Name, 0, ArchiveRwDffCopyDeduplicator.Fingerprint(rootMesh, rootTexture))
        };

        foreach (var preName in new[] { "Foo.pre", "Rio.pre", "SI.pre", "Tok.pre" })
        {
            var preEntry = root.FindEntry(preName);
            Assert.NotNull(preEntry);
            var nested = root.TryOpenNested(preEntry);
            Assert.NotNull(nested);
            using var nestedFileSystem = nested.FileSystem;

            var nestedEntry = nested.FindEntry("PedPro_Muska.skn");
            Assert.NotNull(nestedEntry);
            var nestedSource = new ArchiveAssetSource(nested, nestedEntry);
            var nestedMesh = nestedSource.ReadBytes();
            var nestedTexture = nestedSource.TryReadCompanion("PedPro_Muska.tex");
            Assert.NotNull(nestedTexture);

            Assert.Equal(rootMesh, nestedMesh);
            Assert.Equal(rootTexture, nestedTexture);
            candidates.Add(new ArchiveRwDffCopyCandidate(
                nestedEntry.Name,
                nested.FileSystem.NestingDepth,
                ArchiveRwDffCopyDeduplicator.Fingerprint(nestedMesh, nestedTexture)));
        }

        var textureDictionary = RwTxdFile.Parse(rootTexture);
        Assert.True(textureDictionary.Success, textureDictionary.ErrorMessage);
        Assert.Equal(8, textureDictionary.Textures.Count);
        Assert.All(textureDictionary.Textures, texture =>
        {
            Assert.Equal(32, texture.Width);
            Assert.Equal(32, texture.Height);
        });

        var keptIndices = ArchiveRwDffCopyDeduplicator.SelectIndicesToKeep(candidates);
        Assert.Equal(ExpectedRootIndex, keptIndices);
    }
}
