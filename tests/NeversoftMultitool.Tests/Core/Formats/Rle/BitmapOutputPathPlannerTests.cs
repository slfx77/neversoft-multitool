using NeversoftMultitool.Core.Formats.Rle;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public sealed class BitmapOutputPathPlannerTests
{
    [Theory]
    [InlineData(".rle", "_rle.png")]
    [InlineData(".BMR", "_BMR.png")]
    [InlineData(".zlb", "_zlb.png")]
    [InlineData(".bmp", "_bmp.png")]
    [InlineData(".tga", "_tga.png")]
    public void Plan_ExtensionOnlyLeafUsesSafeCasePreservingStem(
        string fileName,
        string expectedPath)
    {
        var plan = Assert.Single(BitmapOutputPathPlanner.Plan([fileName], inputRoot: null));

        Assert.Equal(fileName, plan.Source);
        Assert.Equal(expectedPath, plan.RelativePngPath);
    }

    [Fact]
    public void Plan_ExtensionOnlyArchiveCollisionsPreserveOwningDirectories()
    {
        var root = Path.Combine("input", "archives");
        var archive = Path.Combine(root, "bundle.wad");
        string[] sources =
        [
            $"{archive}::left/.rle",
            $"{archive}::right/.rle"
        ];

        var plans = BitmapOutputPathPlanner.Plan(sources, root);

        Assert.Equal(sources, plans.Select(static plan => plan.Source));
        Assert.Equal(
            [
                Path.Combine("bundle.wad", "left", "_rle.png"),
                Path.Combine("bundle.wad", "right", "_rle.png")
            ],
            plans.Select(static plan => plan.RelativePngPath));
    }

    [Theory]
    [InlineData("named.rle", "named.png")]
    [InlineData("named.with.dots.tga", "named.with.dots.png")]
    [InlineData("named.PNG", "named.png")]
    [InlineData(".hidden.bmp", ".hidden.png")]
    [InlineData(".img.n64", ".img.png")]
    public void Plan_NamedLeafKeepsExistingStem(string fileName, string expectedPath)
    {
        var plan = Assert.Single(BitmapOutputPathPlanner.Plan([fileName], inputRoot: null));

        Assert.Equal(expectedPath, plan.RelativePngPath);
    }

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
    public void Plan_SourcePngReservesItsNaturalOutputName()
    {
        var plans = BitmapOutputPathPlanner.Plan(
            ["same.jpg", "same.png", "same_converted.png"], inputRoot: null);

        Assert.Equal("same_converted_converted.png", plans[0].RelativePngPath);
        Assert.Equal("same.png", plans[1].RelativePngPath);
        Assert.Equal("same_converted.png", plans[2].RelativePngPath);
    }

    [Fact]
    public void Plan_SourcePngAlsoReservesItsNameAgainstTiffMipOutputs()
    {
        var plans = BitmapOutputPathPlanner.Plan(
            ["foo.tif", "foo_mip1.png", "foo_converted_mip1.png"], inputRoot: null);

        Assert.Equal("foo_converted_converted.png", plans[0].RelativePngPath);
        Assert.Equal("foo_mip1.png", plans[1].RelativePngPath);
        Assert.Equal("foo_converted_mip1.png", plans[2].RelativePngPath);
    }

    [Fact]
    public void Plan_TiffMipAliasesCannotCollideWithAnotherPrimaryOutput()
    {
        var plans = BitmapOutputPathPlanner.Plan(
            ["foo.tif", "foo_mip1.bmp"], inputRoot: null);

        Assert.Equal("foo.png", plans[0].RelativePngPath);
        Assert.Equal("foo_mip1_2.png", plans[1].RelativePngPath);
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
