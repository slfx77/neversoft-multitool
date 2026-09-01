using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GbaDhjModelCommandTests(TestPaths paths)
{
    [CorpusFact]
    public void ExportsOneSelectedPoseAndRejectsOutOfRangeSelections()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
            "Tony Hawk's Downhill Jam (USA).gba");
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");

        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-dhj-model-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, GbaDhjModelCommand.Execute(
                romPath!, output, selectedIndex: 19, clipIndex: 79, frameIndex: 0,
                verbose: false));
            var glb = Path.Combine(output, "rider_19.glb");
            Assert.True(File.Exists(glb));
            Assert.True(new FileInfo(glb).Length > 1_024);

            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath!, output, selectedIndex: 24, clipIndex: 79, frameIndex: 0,
                verbose: false));
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath!, output, selectedIndex: 19, clipIndex: 94, frameIndex: 0,
                verbose: false));
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath!, output, selectedIndex: 19, clipIndex: 79, frameIndex: 12,
                verbose: false));
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }
}
