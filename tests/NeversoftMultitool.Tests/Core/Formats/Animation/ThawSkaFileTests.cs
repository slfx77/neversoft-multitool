using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Archives;

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

    private const string StorySelectPs2 =
        "DATAP/worlds/worldzones/z_storyselect/z_storyselect.pak/00000310.ska";
    private const string StorySelectGc =
        "worlds/worldzones/z_storyselect/z_storyselect.apk/Skater_camera.ska.ngc";
    private const string RocketPs2 =
        "DATAP/cutscenes/HO_LevelEvent_Rocket/ps2/ho_levelevent_rocket_main/ho_levelevent_rocket_main.pak/00001310.ska";
    private const string RocketGc =
        "cutscenes/HO_LevelEvent_Rocket/ngc/ho_levelevent_rocket_main/ho_levelevent_rocket_main.apk/CAM_0.ska.ngc";
    private const string Ho3Ps2 = "DATAP/cutscenes/HO_3/ps2/ho_3_main/ho_3_main.pak/000007E0.ska";
    private const string Ho3Gc = "cutscenes/HO_3/ngc/ho_3_main/ho_3_main.apk/CAM_0.ska.ngc";

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
            .ToDictionary(static file => Path.GetFileName(file)!, static file => file);
        return PakArchive.GetFileList(pakPath)
            .Where(e => e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(e => byName[Path.GetFileName(e.Name)!])
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
    public void Parse_StorySelectCustomFov_Ps2AndGcAreEndianMirrors()
    {
        var (ps2, gc, ps2Bytes, gcBytes) = ParseFixturePair(StorySelectPs2, StorySelectGc);

        Assert.Equal(140, ps2Bytes.Length);
        Assert.Equal(ps2Bytes.Length, gcBytes.Length);
        AssertCustomKeysEqual(ps2, gc);
        Assert.Equal(66.666664f, ps2.Duration);
        Assert.Equal(2, ps2.CustomKeys.Length);
        Assert.All(ps2.CustomKeys, static key => Assert.Equal(1u, key.Type));
        Assert.Equal(0u, ps2.CustomKeys[0].Timestamp);
        Assert.Equal(3998u, ps2.CustomKeys[1].Timestamp);
        Assert.Equal(0.17951635f, ps2.CustomKeys[0].Fov);
        Assert.Equal(0.17951635f, ps2.CustomKeys[1].Fov);
    }

    [Fact]
    public void Parse_RocketCustomScripts_Ps2AndGcAreEndianMirrors()
    {
        var (ps2, gc, ps2Bytes, gcBytes) = ParseFixturePair(RocketPs2, RocketGc);

        Assert.Equal(604, ps2Bytes.Length);
        Assert.Equal(ps2Bytes.Length, gcBytes.Length);
        AssertCustomKeysEqual(ps2, gc);
        Assert.Equal(31, ps2.CustomKeys.Length);
        Assert.Contains(ps2.CustomKeys, static key => key.Type == 4);
        Assert.Equal(0u, ps2.CustomKeys[0].Timestamp);
        Assert.Equal(4u, ps2.CustomKeys[0].Type);
        Assert.Equal(0xAB328A00u, ps2.CustomKeys[0].ScriptQbKey);
        Assert.Equal(2241u, ps2.CustomKeys[^1].Timestamp);
    }

    [Fact]
    public void Parse_Ho3FovRichCustomKeys_Ps2AndGcAreEndianMirrors()
    {
        var (ps2, gc, ps2Bytes, gcBytes) = ParseFixturePair(Ho3Ps2, Ho3Gc);

        Assert.Equal(1372, ps2Bytes.Length);
        Assert.Equal(ps2Bytes.Length, gcBytes.Length);
        AssertCustomKeysEqual(ps2, gc);
        Assert.Equal(71, ps2.CustomKeys.Length);
        Assert.Contains(ps2.CustomKeys, static key => key.Type == 1);
        Assert.Contains(ps2.CustomKeys, static key => key.Type == 4);
        Assert.Equal(0.60241574f, ps2.CustomKeys[0].Fov);
        Assert.Equal(4081u, ps2.CustomKeys[^1].Timestamp);
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

    [CorpusFact]
    public void Parse_AllThawSka_FullCorpusSweep()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var failures = new List<string>();
        var total = 0;
        var customFileCount = 0;
        var customFilesByBuild = new Dictionary<string, int>
        {
            [ThawPs2Build] = 0,
            [ThawGcBuild] = 0,
            [ThawPcBuild] = 0
        };
        var firstFovCount = 0;
        var firstScriptCount = 0;
        var minCustomCount = int.MaxValue;
        var maxCustomCount = 0;
        var customFlags = new HashSet<uint>();
        // Minimums calibrated against the 2026-07-16 regeneration (header-relative
        // pak reads): PS2 6,616 / GC 7,354 / PC 6,455 extracted anims. The old GC
        // floor of 8,000 was measured on a stale pre-pak-fix tree whose misparsed
        // entry tables emitted extra bogus files.
        foreach (var (build, pattern, minimum) in new[]
                 {
                     (ThawPs2Build, "*.ska", 6000),
                     (ThawGcBuild, "*.ska.ngc", 7000),
                     (ThawPcBuild, "*.ska", 6000)
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
                    var data = File.ReadAllBytes(file);
                    var anim = SkaFile.Parse(data, table);
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

                    if (anim.CustomKeys.Length > 0)
                    {
                        Assert.True(SkaThawParser.IsThawSka(data, out var bigEndian));
                        var r = new EndianSpanReader(data, bigEndian);
                        var headerCount = r.U16(0x12);
                        Assert.Equal(headerCount, anim.CustomKeys.Length);
                        Assert.True((anim.Flags & SkaFile.FlagPlatform) != 0,
                            $"custom events unexpectedly used non-platform flags 0x{anim.Flags:X8}");

                        var customStart = GetCustomKeyStart(data, bigEndian);
                        Assert.Equal(data.Length, customStart + headerCount * 16);
                        Assert.All(anim.CustomKeys, static key =>
                        {
                            Assert.Contains(key.Type, new uint[] { 1, 4 });
                            Assert.Equal(16u, key.Size);
                            Assert.Equal(4, key.Payload.Length);
                        });

                        for (var k = 1; k < anim.CustomKeys.Length; k++)
                        {
                            Assert.True(
                                anim.CustomKeys[k].Timestamp >=
                                anim.CustomKeys[k - 1].Timestamp,
                                $"custom timestamps are not serialized in timeline order at key {k}");
                        }

                        customFileCount++;
                        customFilesByBuild[build]++;
                        if (anim.CustomKeys[0].Type == 1) firstFovCount++;
                        if (anim.CustomKeys[0].Type == 4) firstScriptCount++;
                        minCustomCount = Math.Min(minCustomCount, anim.CustomKeys.Length);
                        maxCustomCount = Math.Max(maxCustomCount, anim.CustomKeys.Length);
                        customFlags.Add(anim.Flags);
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
        Assert.Equal(100, customFileCount);
        Assert.Equal(36, customFilesByBuild[ThawPs2Build]);
        Assert.Equal(35, customFilesByBuild[ThawGcBuild]);
        Assert.Equal(29, customFilesByBuild[ThawPcBuild]);
        Assert.Equal(35, firstFovCount);
        Assert.Equal(65, firstScriptCount);
        Assert.Equal(2, minCustomCount);
        Assert.Equal(121, maxCustomCount);
        Assert.Equal(new uint[] { 0x1E010100, 0x1E111000 }, customFlags.Order().ToArray());
    }

    private (SkaAnimation Ps2, SkaAnimation Gc, byte[] Ps2Bytes, byte[] GcBytes) ParseFixturePair(
        string ps2RelativePath, string gcRelativePath)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2Path = Path.Combine(paths.SampleBuildsDir!, ThawPs2Build,
            ps2RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var gcPath = Path.Combine(paths.SampleBuildsDir!, ThawGcBuild,
            gcRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.SkipWhen(!File.Exists(ps2Path) || !File.Exists(gcPath), "THAW custom-key fixtures not found");

        var ps2Bytes = File.ReadAllBytes(ps2Path);
        var gcBytes = File.ReadAllBytes(gcPath);
        return (SkaFile.Parse(ps2Bytes), SkaFile.Parse(gcBytes), ps2Bytes, gcBytes);
    }

    private static void AssertCustomKeysEqual(SkaAnimation expected, SkaAnimation actual)
    {
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.CustomKeys.Length, actual.CustomKeys.Length);
        for (var i = 0; i < expected.CustomKeys.Length; i++)
        {
            var left = expected.CustomKeys[i];
            var right = actual.CustomKeys[i];
            Assert.Equal(left.Timestamp, right.Timestamp);
            Assert.Equal(left.Type, right.Type);
            Assert.Equal(left.Size, right.Size);
            Assert.Equal(left.Fov, right.Fov);
            Assert.Equal(left.ScriptQbKey, right.ScriptQbKey);
        }
    }

    private static int GetCustomKeyStart(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var r = new EndianSpanReader(data, bigEndian);
        var flags = r.U32(4);
        var numBones = data[0x0D];
        var numQKeys = r.U16(0x0E);
        var numTKeys = r.U16(0x10);
        var offset = 0x28;

        if ((flags & SkaFile.FlagUseCompressTable) != 0)
        {
            var qBytes = checked((int)r.U32(0x28));
            var tBytes = checked((int)r.U32(0x2C));
            offset = 0x30 + 4 * numBones;
            if ((flags & SkaFile.FlagPartialAnim) != 0)
            {
                var originalBones = checked((int)r.U32(offset));
                offset += 4 + 4 * ((originalBones + 31) / 32);
            }

            offset += qBytes + tBytes;
        }
        else
        {
            if ((flags & SkaFile.FlagObjectAnimData) != 0)
                offset += 4 * numBones;
            if ((flags & SkaFile.FlagPartialAnim) != 0)
            {
                var originalBones = checked((int)r.U32(offset));
                offset += 4 + 4 * ((originalBones + 31) / 32);
            }

            offset += ((flags & SkaFile.FlagHiResFramePointers) != 0 ? 4 : 2) * numBones;
            offset = (offset + 3) & ~3;
            offset += 16 * (numQKeys + numTKeys);
        }

        return (offset + 3) & ~3;
    }
}
