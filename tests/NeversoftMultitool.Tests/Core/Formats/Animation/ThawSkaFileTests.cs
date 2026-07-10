using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     THAW v0x28 SKA animations: little-endian .ska (PS2/PC) and big-endian
///     .ska.ngc (GC) share one parser; compressed key blobs are raw LE bytes on
///     every platform, so paired files must decode to identical keys.
/// </summary>
public class ThawSkaFileTests(TestPaths paths)
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

    private static string[] OrderedFiles(string pakPath, string extractDir, string suffix)
    {
        var byName = Directory.GetFiles(extractDir, "*" + suffix, SearchOption.AllDirectories)
            .ToDictionary(Path.GetFileName!, f => f);
        return PakArchive.GetFileList(pakPath)
            .Where(e => e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(e => byName[Path.GetFileName(e.Name)])
            .ToArray();
    }

    private SkaCompressTable? LoadCompressTable(string build)
    {
        var buildDir = Path.Combine(paths.SampleBuildsDir!, build);
        var q = Directory.GetFiles(buildDir, "standardkey?.bin", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith("standardkeyq", StringComparison.OrdinalIgnoreCase));
        var t = Directory.GetFiles(buildDir, "standardkey?.bin", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith("standardkeyt", StringComparison.OrdinalIgnoreCase));
        return q != null && t != null ? SkaCompressTable.TryLoad(q, t) : null;
    }

    [Fact]
    public void Parse_Bh11Cam0_Ps2AndGc_DecodeToIdenticalKeys()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2Pak = paths.FindSampleFile(ThawPs2Build, "bh_11_cam0.pak.ps2");
        var gcPak = paths.FindSampleFile(ThawGcBuild, "bh_11_cam0.apk.ngc");
        Assert.SkipWhen(ps2Pak is null || gcPak is null, "bh_11_cam0 archives not found");

        var table = LoadCompressTable(ThawPs2Build);
        Assert.SkipWhen(table is null, "standardkey tables not found");

        var ps2Dir = ExtractPak(ps2Pak!, "SkaPs2");
        var gcDir = ExtractPak(gcPak!, "SkaGc");
        try
        {
            var ps2Files = OrderedFiles(ps2Pak!, ps2Dir, ".ska");
            var gcFiles = OrderedFiles(gcPak!, gcDir, ".ska.ngc");
            Assert.Equal(ps2Files.Length, gcFiles.Length);
            Assert.True(ps2Files.Length >= 20, $"expected a full camera cut set, got {ps2Files.Length}");

            var compared = 0;
            for (var i = 0; i < ps2Files.Length; i++)
            {
                var ps2Bytes = File.ReadAllBytes(ps2Files[i]);
                var gcBytes = File.ReadAllBytes(gcFiles[i]);
                if (ps2Bytes.Length != gcBytes.Length)
                    continue; // a handful of cuts differ across platforms

                var ps2 = SkaFile.Parse(ps2Bytes, table);
                var gc = SkaFile.Parse(gcBytes, table);

                Assert.Equal(ps2.Duration, gc.Duration);
                Assert.Equal(ps2.BoneTracks.Length, gc.BoneTracks.Length);
                for (var b = 0; b < ps2.BoneTracks.Length; b++)
                {
                    Assert.Equal(ps2.BoneTracks[b].RotationKeys, gc.BoneTracks[b].RotationKeys);
                    Assert.Equal(ps2.BoneTracks[b].TranslationKeys, gc.BoneTracks[b].TranslationKeys);
                }

                compared++;
            }

            Assert.True(compared >= 20, $"only {compared} same-size pairs compared");
        }
        finally
        {
            Directory.Delete(ps2Dir, true);
            Directory.Delete(gcDir, true);
        }
    }

    [Fact]
    public void Parse_AllThawSka_FullCorpusSweep()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var failures = new List<string>();
        var total = 0;
        foreach (var (build, pattern, minimum) in new[]
                 {
                     (ThawPs2Build, "*.ska", 6000),
                     (ThawGcBuild, "*.ska.ngc", 8000),
                     (ThawPcBuild, "*.ska", 6000),
                 })
        {
            var buildDir = Path.Combine(paths.SampleBuildsDir!, build);
            if (!Directory.Exists(buildDir))
                continue;

            var table = LoadCompressTable(build);
            Assert.True(table is not null, $"{build}: standardkey tables not found");

            var files = Directory.GetFiles(buildDir, pattern, SearchOption.AllDirectories);
            Assert.True(files.Length >= minimum,
                $"{build}: expected at least {minimum} extracted anims, found {files.Length}");

            foreach (var file in files)
            {
                total++;
                try
                {
                    var anim = SkaFile.Parse(File.ReadAllBytes(file), table);
                    Assert.Equal(0x28u, anim.Version);
                    var limit = anim.Duration * 60f + 1.5f;
                    foreach (var track in anim.BoneTracks)
                    {
                        for (var k = 0; k < track.RotationKeys.Length; k++)
                        {
                            var t = track.RotationKeys[k].Time * 60f;
                            if (t > limit || (k > 0 && t < track.RotationKeys[k - 1].Time * 60f))
                                throw new InvalidDataException($"rot key {k} time {t} invalid (limit {limit})");
                        }

                        for (var k = 0; k < track.TranslationKeys.Length; k++)
                        {
                            var t = track.TranslationKeys[k].Time * 60f;
                            if (t > limit || (k > 0 && t < track.TranslationKeys[k - 1].Time * 60f))
                                throw new InvalidDataException($"trans key {k} time {t} invalid (limit {limit})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)} ({build}): {ex.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{total} failed:\n" + string.Join("\n", failures.Take(10)));
        Assert.True(total >= 20000, $"expected the full three-platform corpus, swept {total}");
    }
}
