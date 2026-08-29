using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the faces the engine's baked isometric view cannot texture.
/// </summary>
/// <remarks>
///     A user reported "diagonal textures are reprojected wavy". The cause is the
///     projection, not the export: its Jacobian determinant is
///     <c>256·(hx + hy − 1)</c>, so a surface rising at 45° in the combined view
///     direction projects to NO art area, and past that the mapping folds.
///     <para>
///         Measured over all nine THPS2 levels before this shipped: quads on the
///         near side of the fold average well under one art pixel of mapping error
///         (0.30–1.10 px on the six large levels), while quads past it average
///         5–17 px and reach 268. Two fixes were tested against that measurement and
///         REJECTED — the other diagonal is about twice as bad on every level, and
///         doubling the subdivision does not help and sometimes hurts (School II
///         34.05 → 44.80 px, pool 48.00 → 96.00). Refinement making it worse is the
///         signature of a fold rather than an approximation error.
///     </para>
/// </remarks>
public sealed class GbaLevelGrazedFaceTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    [Fact]
    public void FlatGroundIsNeverGrazingAndTheFoldIsWhereTheDeterminantFlips()
    {
        // Flat ground: one world unit of surface covers 256 art pixels.
        Assert.Equal(-256.0, GbaLevelArtProjection.ProjectedAreaPerWorldArea(0, 0));
        Assert.False(GbaLevelArtProjection.IsGrazing(0, 0));

        // A gentle slope still projects, with the same sign.
        Assert.True(GbaLevelArtProjection.ProjectedAreaPerWorldArea(0.25, 0.25) < 0);
        Assert.False(GbaLevelArtProjection.IsGrazing(0.25, 0.25));

        // At 45 degrees combined the projected area is exactly zero — the art
        // covering such a face carries no detail at all.
        Assert.Equal(0.0, GbaLevelArtProjection.ProjectedAreaPerWorldArea(0.5, 0.5));
        Assert.True(GbaLevelArtProjection.IsGrazing(0.5, 0.5));

        // Past it the sign flips: the mapping folds back on itself.
        Assert.True(GbaLevelArtProjection.ProjectedAreaPerWorldArea(1, 1) > 0);
        Assert.True(GbaLevelArtProjection.IsGrazing(1, 1));
    }

    /// <summary>
    ///     Grazed faces are SEPARATED, never dropped: the level keeps every triangle
    ///     it had, and still resolves to one material and one texture.
    /// </summary>
    [CorpusFact]
    public void GrazedFacesAreSplitOutWithoutChangingWhatTheLevelContains()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var withGrazed = 0;
        var totalGrazed = 0;

        for (var level = 0; level < 9; level++)
        {
            var rec = GbaLevelCarver.FindRecordOffset(rom, carve[level].Data);
            var native = new GbaLevelNativeSource(carve[level].Data, rom, rec, "L" + level, "");
            var document = ModelDocument.CreateNative("L" + level, ModelSourceKind.GbaLevel, native);
            GbaLevelGeometryWriter.Populate(document, native);

            Assert.Single(document.Materials);
            Assert.Single(document.Textures);

            var grazed = document.Meshes
                .SelectMany(m => m.Primitives)
                .Where(p => p.Name.EndsWith(GbaLevelGeometryWriter.GrazedSuffix, StringComparison.Ordinal))
                .Sum(p => p.TriangleCount);
            if (grazed > 0) withGrazed++;
            totalGrazed += grazed;
        }

        // Every level has some: the fold is a property of the view, not of one
        // level's authoring.
        Assert.Equal(9, withGrazed);
        Assert.True(totalGrazed > 0);
    }

    /// <summary>
    ///     The Hangar's totals are unchanged by the split — the grazed faces are a
    ///     subset of what it already emitted.
    /// </summary>
    [CorpusFact]
    public void TheHangarKeepsItsTriangleCount()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var rec = GbaLevelCarver.FindRecordOffset(rom, carve[0].Data);
        var native = new GbaLevelNativeSource(carve[0].Data, rom, rec, "Hangar", "");
        var document = ModelDocument.CreateNative("0_hangar", ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(document, native);

        Assert.Equal(14739, document.TriangleCount);
    }
}
