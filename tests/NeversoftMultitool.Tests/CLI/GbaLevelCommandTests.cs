using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GbaLevelCommandTests(TestPaths paths)
{
    [CorpusFact]
    public void Thps3LevelWriterNamesVisibleArtAndCollisionSeparately()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)",
            "Tony Hawk's Pro Skater 3 (USA, Europe).gba");
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var level = GbaThps3LevelArt.FindLevels(rom)[0];
        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-thps3-level-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            Assert.True(GbaLevelCommand.WriteThps3LevelImages(rom, level, output, verbose: false));
            foreach (var name in (ReadOnlySpan<string>)[
                         "level_00.png",
                         "level_00_collision.png",
                         "level_00_palette.png"])
            {
                var path = Path.Combine(output, name);
                Assert.True(File.Exists(path), name);
                Assert.True(new FileInfo(path).Length > 100, name);
                Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(path)[..4]);
            }
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [CorpusFact]
    public void LaterLevelWriterNamesVisibleArtCollisionAndOcclusionSeparately()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's American Sk8land (2005-10-18, GBA - Final)",
            "Tony Hawk's American Sk8land (USA).gba");
        Assert.SkipWhen(romPath == null, "American Sk8land GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var level = GbaLaterLevelArt.FindLevels(rom)[0];
        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-level-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            Assert.True(GbaLevelCommand.WriteLaterLevelImages(rom, level, output, verbose: false));
            foreach (var name in (ReadOnlySpan<string>)[
                         "level_00.png",
                         "level_00_collision.png",
                         "level_00_occlusion.png",
                         "level_00_palette.png"])
            {
                var path = Path.Combine(output, name);
                Assert.True(File.Exists(path), name);
                Assert.True(new FileInfo(path).Length > 100, name);
                Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(path)[..4]);
            }
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }
}
