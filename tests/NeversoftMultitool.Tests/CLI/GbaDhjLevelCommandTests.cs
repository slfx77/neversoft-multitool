using System.CommandLine;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GbaDhjLevelCommandTests(TestPaths paths)
{
    [CorpusFact]
    public void PublicCommandExportsSelectedVisualAndCollisionAndValidatesIndex()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
            "Tony Hawk's Downhill Jam (USA).gba");
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");

        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-dhj-level-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, GbaDhjLevelCommand.Create()
                .Parse([romPath!, "--output", output, "--index", "0"])
                .Invoke());
            var visual = Path.Combine(output, "course_00.glb");
            var collision = Path.Combine(output, "course_00_collision.glb");
            Assert.True(File.Exists(visual));
            Assert.True(new FileInfo(visual).Length > 1_000_000);
            Assert.True(File.Exists(collision));
            Assert.True(new FileInfo(collision).Length > 100_000);

            Assert.Equal(1, GbaDhjLevelCommand.Create()
                .Parse([romPath!, "--output", output, "--index", "11"])
                .Invoke());

            // The established cross-game entry point must detect the BXS
            // engine and emit the full DHJ corpus through the same exporter.
            Assert.Equal(0, GbaLevelCommand.Create()
                .Parse([romPath!, "--output", output])
                .Invoke());
            var glbs = Directory.GetFiles(output, "*.glb");
            Assert.Equal(11, glbs.Count(static path =>
                !Path.GetFileNameWithoutExtension(path).EndsWith(
                    "_collision", StringComparison.Ordinal)));
            Assert.Equal(11, glbs.Count(static path =>
                Path.GetFileNameWithoutExtension(path).EndsWith(
                    "_collision", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }
}
