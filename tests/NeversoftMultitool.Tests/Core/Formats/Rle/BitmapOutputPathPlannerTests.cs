using NeversoftMultitool.Core.Formats.Rle;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public sealed class BitmapOutputPathPlannerTests
{
    [Fact]
    public void Plan_FileSystemCollisionsMirrorFoldersWhileUniqueStemStaysFlat()
    {
        var root = Path.Combine("input", "bitmaps");
        string[] sources =
        [
            Path.Combine(root, "left", "shared.rle"),
            Path.Combine(root, "right", "shared.bmr"),
            Path.Combine(root, "nested", "unique.tga")
        ];

        var plans = BitmapOutputPathPlanner.Plan(sources, root);

        Assert.Equal(sources, plans.Select(static plan => plan.Source));
        Assert.Equal(
            [
                Path.Combine("left", "shared.png"),
                Path.Combine("right", "shared.png"),
                "unique.png"
            ],
            plans.Select(static plan => plan.RelativePngPath));
    }

    [Fact]
    public void Plan_ArchiveCollisionsPreserveArchiveAndEntryDirectories()
    {
        var root = Path.Combine("input", "archives");
        var archive = Path.Combine(root, "bundle.wad");
        string[] sources =
        [
            $"{archive}::shared.rle",
            $"{archive}::right/shared.bmr",
            $"{archive}::unique.tga"
        ];

        var plans = BitmapOutputPathPlanner.Plan(sources, root);

        Assert.Equal(
            [
                Path.Combine("bundle.wad", "shared.png"),
                Path.Combine("bundle.wad", "right", "shared.png"),
                "unique.png"
            ],
            plans.Select(static plan => plan.RelativePngPath));
    }
}
