using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skeleton;

/// <summary>
///     THAW-generation skeletons: little-endian .ske (PS2/PC) and big-endian
///     .ske.ngc (GC) are field-for-field endian mirrors sharing one parser.
///     Rosetta fixtures are extracted from pristine PAK archives at test time.
/// </summary>
public class ThawSkeletonFileTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string ThawPcBuild = "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";

    private static string ExtractPak(string pakPath, string tag)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NsMultitool_Test_{tag}_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);
        return tempDir;
    }

    /// <summary>Extracted skeleton file paths in pak entry-table order.</summary>
    private static string[] OrderedSkeletonFiles(string pakPath, string extractDir, string suffix)
    {
        var byName = Directory.GetFiles(extractDir, "*" + suffix, SearchOption.AllDirectories)
            .ToDictionary(static file => Path.GetFileName(file)!, static file => file);
        return PakArchive.GetFileList(pakPath)
            .Where(e => e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(e => byName[Path.GetFileName(e.Name)!])
            .ToArray();
    }

    [CorpusFact]
    public void Parse_Bh11Main_Ps2AndGc_AgreeOnHierarchyAndPose()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2Pak = paths.FindSampleFile(ThawPs2Build, "bh_11_main.pak.ps2");
        var gcPak = paths.FindSampleFile(ThawGcBuild, "bh_11_main.apk.ngc");
        Assert.SkipWhen(ps2Pak is null || gcPak is null, "bh_11_main archives not found");

        var ps2Dir = ExtractPak(ps2Pak!, "SkePs2");
        var gcDir = ExtractPak(gcPak!, "SkeGc");
        try
        {
            // Pair by pak ENTRY ORDER (extracted filenames sort differently:
            // PS2 = offset-named, GC = QbKey-named).
            var ps2Files = OrderedSkeletonFiles(ps2Pak!, ps2Dir, ".ske");
            var gcFiles = OrderedSkeletonFiles(gcPak!, gcDir, ".ske.ngc");
            Assert.Equal(ps2Files.Length, gcFiles.Length);
            Assert.True(ps2Files.Length >= 5, $"expected the cutscene cast, got {ps2Files.Length}");

            for (var i = 0; i < ps2Files.Length; i++)
            {
                var ps2 = SkeletonFile.Parse(File.ReadAllBytes(ps2Files[i]));
                var gc = SkeletonFile.Parse(File.ReadAllBytes(gcFiles[i]));

                Assert.Equal(ps2.Bones.Length, gc.Bones.Length);
                for (var b = 0; b < ps2.Bones.Length; b++)
                {
                    Assert.Equal(ps2.Bones[b].NameChecksum, gc.Bones[b].NameChecksum);
                    Assert.Equal(ps2.Bones[b].ParentIndex, gc.Bones[b].ParentIndex);

                    var dt = (ps2.Bones[b].LocalTranslation - gc.Bones[b].LocalTranslation).Length();
                    Assert.True(dt < 1e-4f, $"file {i} bone {b}: translation differs by {dt}");

                    // Same rotation up to quaternion sign.
                    var dot = MathF.Abs(Quaternion.Dot(ps2.Bones[b].LocalRotation, gc.Bones[b].LocalRotation));
                    Assert.True(dot > 1f - 1e-4f, $"file {i} bone {b}: rotation dot {dot}");
                }
            }
        }
        finally
        {
            Directory.Delete(ps2Dir, true);
            Directory.Delete(gcDir, true);
        }
    }

    [CorpusFact]
    public void Parse_AllThawSkeletons_FullCorpusSweep()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var failures = new List<string>();
        var total = 0;
        foreach (var (build, pattern, minimum) in new[]
                 {
                     (ThawPs2Build, "*.ske", 300),
                     (ThawGcBuild, "*.ske.ngc", 300),
                     (ThawPcBuild, "*.ske", 250)
                 })
        {
            var buildDir = Path.Combine(paths.SampleBuildsDir!, build);
            if (!Directory.Exists(buildDir))
                continue;

            var files = Directory.GetFiles(buildDir, pattern, SearchOption.AllDirectories);
            Assert.True(files.Length >= minimum,
                $"{build}: expected at least {minimum} extracted skeletons, found {files.Length}");

            foreach (var file in files)
            {
                total++;
                try
                {
                    var skeleton = SkeletonFile.Parse(File.ReadAllBytes(file));
                    Assert.InRange(skeleton.Bones.Length, 1, 256);

                    // Parents must precede their children (verified corpus-wide),
                    // which also rules out cycles.
                    for (var b = 0; b < skeleton.Bones.Length; b++)
                        if (skeleton.Bones[b].ParentIndex >= b)
                            throw new InvalidDataException(
                                $"bone {b} parented forward to {skeleton.Bones[b].ParentIndex}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)} ({build}): {ex.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{total} failed:\n" + string.Join("\n", failures.Take(10)));
        Assert.True(total >= 900, $"expected the full three-platform corpus, swept {total}");
    }
}
