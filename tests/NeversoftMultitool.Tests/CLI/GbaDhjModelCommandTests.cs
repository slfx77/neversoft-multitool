using NeversoftMultitool.CLI;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GbaDhjModelCommandTests(TestPaths paths)
{
    [CorpusFact]
    public void ExportsOneSelectedPoseAndRejectsOutOfRangeSelections()
    {
        var romPath = RomPath();
        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-dhj-model-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 79, frameIndex: 0,
                verbose: false));
            var glb = Path.Combine(output, "rider_19.glb");
            Assert.True(File.Exists(glb));
            Assert.True(new FileInfo(glb).Length > 1_024);

            // The default route stays a single pose: no animation, no morphing.
            var exported = ModelRoot.Load(glb);
            Assert.Empty(exported.LogicalAnimations);
            Assert.All(exported.LogicalMeshes.SelectMany(static mesh => mesh.Primitives),
                static primitive => Assert.Equal(0, primitive.MorphTargetsCount));

            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 24, clipIndex: 79, frameIndex: 0,
                verbose: false));
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 94, frameIndex: 0,
                verbose: false));
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 79, frameIndex: 12,
                verbose: false));
        }
        finally
        {
            Delete(output);
        }
    }

    [CorpusFact]
    public void AnimateExportsTheWholeClipAsMorphTargets()
    {
        var romPath = RomPath();
        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-dhj-anim-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 79, frameIndex: 0,
                animate: true, verbose: true));

            var glb = Path.Combine(output, "rider_19.glb");
            Assert.True(File.Exists(glb));
            var exported = ModelRoot.Load(glb);
            Assert.Equal("anim_79", Assert.Single(exported.LogicalAnimations).Name);
            Assert.Empty(exported.LogicalSkins);
            Assert.All(exported.LogicalMeshes.SelectMany(static mesh => mesh.Primitives),
                static primitive => Assert.Equal(11, primitive.MorphTargetsCount));
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>
    ///     Clip 93 is the directory's last and has no following offset, so its
    ///     length comes solely from the u32 prefix in front of it (25 records).
    ///     It used to be refused as unbounded; it now exports like any other clip.
    /// </summary>
    [CorpusFact]
    public void AnimateExportsTheFinalClipToo()
    {
        var romPath = RomPath();
        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-dhj-final-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 93, frameIndex: 0,
                animate: true, verbose: false));

            var exported = ModelRoot.Load(Path.Combine(output, "rider_19.glb"));
            var animation = Assert.Single(exported.LogicalAnimations);
            Assert.Equal("anim_93", animation.Name);

            // A single pose from the same clip works too, including its last
            // record; the one past the end is still refused.
            Assert.Equal(0, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 93, frameIndex: 24,
                verbose: false));
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 93, frameIndex: 25,
                verbose: false));
        }
        finally
        {
            Delete(output);
        }
    }

    [CorpusFact]
    public void AnimateFailsClosedOnAnOutOfRangeClipAndOnAnExplicitFrame()
    {
        var romPath = RomPath();
        var output = Path.Combine(Path.GetTempPath(), $"nmt-gba-dhj-anim-fail-{Guid.NewGuid():N}");
        try
        {
            // 94 clips, so 94 is past the end.
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 94, frameIndex: 0,
                animate: true, verbose: false));

            // --frame names one pose; an animated clip is always based on frame 0.
            Assert.Equal(1, GbaDhjModelCommand.Execute(
                romPath, output, selectedIndex: 19, clipIndex: 79, frameIndex: 3,
                animate: true, verbose: false));

            Assert.False(File.Exists(Path.Combine(output, "rider_19.glb")));
        }
        finally
        {
            Delete(output);
        }
    }

    private string RomPath()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
            "Tony Hawk's Downhill Jam (USA).gba");
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        return romPath;
    }

    private static void Delete(string output)
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }
}
