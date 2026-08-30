using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the out-of-bounds ring: the level's real boundary, off by default.
/// </summary>
/// <remarks>
///     A user reported the level boundaries render flat. They do, because the
///     engine's kill wall — cells whose sampled surface stands about 34 world units
///     up, where the playfield sits near zero — is omitted: drawn unconditionally it
///     entombs the level. It is now emitted behind a switch, default off, so the
///     export is unchanged unless it is asked for.
/// </remarks>
public sealed class GbaLevelBoundaryTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    private ModelDocument Build(byte[] rom, byte[] record, bool showBoundary)
    {
        var rec = GbaLevelCarver.FindRecordOffset(rom, record);
        var native = new GbaLevelNativeSource(record, rom, rec, "level", "");
        var document = ModelDocument.CreateNative("level", ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(
            document, native,
            showBoundary
                ? new Dictionary<string, bool> { [GbaLevelGeometryWriter.BoundaryGroupId] = true }
                : null);
        return document;
    }

    [CorpusFact]
    public void TheBoundaryIsOfferedButOffByDefault()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var off = Build(rom, carve[0].Data, false);

        // Default output is exactly what it was before the ring existed.
        Assert.Equal(14739, off.TriangleCount);

        var group = Assert.Single(off.VisibilityGroups);
        Assert.Equal(GbaLevelGeometryWriter.BoundaryGroupId, group.Id);
        Assert.False(group.DefaultEnabled);
        Assert.False(group.IsEnabled);

        // The Hangar's ring is the 182 cells the 2D renders also omit, so the two
        // paths agree on what the boundary is.
        Assert.Contains("182 cells", group.Label);
        var iso = GbaCollisionRenderer.Render(rom, GbaLevelCarver.FindRecordOffset(rom, carve[0].Data));
        Assert.NotNull(iso);
        Assert.Equal(182, iso.Value.OmittedCells);
    }

    [CorpusFact]
    public void SwitchingItOnAddsTheWallAndNothingElse()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var off = Build(rom, carve[0].Data, false);
        var on = Build(rom, carve[0].Data, true);

        Assert.True(on.TriangleCount > off.TriangleCount);
        Assert.True(Assert.Single(on.VisibilityGroups).IsEnabled);

        // Still one material and one texture: the ring is the same level art, not a
        // second surface with a second claim about how it looks.
        Assert.Single(on.Materials);
        Assert.Single(on.Textures);
    }

    [CorpusFact]
    public void EveryLevelHasABoundaryAndNoneOfThemDrawsItByDefault()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        for (var level = 0; level < 9; level++)
        {
            var off = Build(rom, carve[level].Data, false);
            var group = Assert.Single(off.VisibilityGroups);
            Assert.False(group.IsEnabled);
            Assert.True(Build(rom, carve[level].Data, true).TriangleCount > off.TriangleCount);
        }
    }
}
