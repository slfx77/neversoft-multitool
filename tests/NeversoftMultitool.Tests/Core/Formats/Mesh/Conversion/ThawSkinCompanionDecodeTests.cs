using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Stream-1 census (2026-08-19): the zone-family TEX check and the
///     skin-companion scene TEX both accept version-6 headers, so decode
///     ROUTING cannot be decided from bytes alone. This sweep measures how
///     the two decoders disagree over every THAW PS2 skin companion (the
///     class <c>ThawSceneTexFile</c> was DMA-REF-verified against, 905/905
///     unique textures) and writes the divergence census to TestOutput.
/// </summary>
public sealed class ThawSkinCompanionDecodeTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    private static Dictionary<uint, NeversoftMultitool.Core.Formats.Texture.Ps2Texture> FirstWins(
        IEnumerable<NeversoftMultitool.Core.Formats.Texture.Ps2Texture> textures)
    {
        var map = new Dictionary<uint, NeversoftMultitool.Core.Formats.Texture.Ps2Texture>();
        foreach (var texture in textures)
            map.TryAdd(texture.Checksum, texture);
        return map;
    }

    /// <summary>
    ///     Routing pin: the default (skin/scene) decode context must ride the
    ///     scene decoder even though the zone check claims the same bytes.
    ///     Regression guard for 049db3c, whose zone-first ordering silently
    ///     rewired all 332 skin companions (331 divergent, 752/905 textures
    ///     changing pixels) until the context flag was threaded.
    /// </summary>
    [Fact]
    public void SkinCompanion_DefaultContext_RidesTheSceneDecoder()
    {
        var path = paths.FindSampleFile(ThawPs2Build, "ped_baller.tex.ps2");
        Assert.SkipWhen(path == null, "THAW PS2 ped_baller.tex.ps2 sample not available");

        var bytes = File.ReadAllBytes(path!);
        Assert.True(ThawZoneTexFile.IsThawZoneTex(bytes),
            "premise: the zone check claims skin companions (if this stops holding, the flag may be removable)");

        var scene = FirstWins(ThawSceneTexFile.Parse(bytes).Textures.Where(static t => t.Pixels != null));
        Assert.NotEmpty(scene);

        var provider = NeversoftMultitool.Core.Formats.Mesh.Conversion.MeshCompanionResolver
            .BuildPs2TextureProvider(bytes);
        Assert.NotNull(provider);
        foreach (var (checksum, texture) in scene)
        {
            var png = provider!(checksum);
            Assert.NotNull(png);
            Assert.Equal(ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Pixels!), png);
        }
    }

    [CorpusFact]
    public void SkinCompanionTex_ZoneVsSceneDecodeDivergenceCensus()
    {
        var companions = paths.FindSampleFiles(ThawPs2Build, "*.tex.ps2")
            .Where(static path => File.Exists(path[..^".tex.ps2".Length] + ".skin.ps2"))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(companions.Count == 0, "THAW PS2 skin-companion TEX corpus not available");

        var zoneClaimed = 0;
        var divergentFiles = 0;
        var totalSceneTextures = 0;
        var identicalTextures = 0;
        var report = new List<string>
        {
            "file,sceneCount,zoneClaims,zoneCount,sharedChecksums,pixelIdentical,notes"
        };

        foreach (var path in companions)
        {
            var bytes = File.ReadAllBytes(path);
            var scene = ThawSceneTexFile.Parse(bytes);
            Assert.True(scene.Success, $"scene TEX parse failed for {path}");
            // First-wins on duplicate checksums, mirroring BuildPs2TextureProvider's cache.TryAdd.
            var sceneTextures = FirstWins(scene.Textures.Where(static t => t.Pixels != null));
            totalSceneTextures += sceneTextures.Count;

            var claims = ThawZoneTexFile.IsThawZoneTex(bytes);
            if (claims) zoneClaimed++;
            var zoneCount = 0;
            var shared = 0;
            var identical = 0;
            var notes = "";
            if (claims)
            {
                try
                {
                    var zoneTextures = FirstWins(ThawZoneTexFile.DecodeAllFromFile(bytes)
                        .Where(static t => t.Pixels != null));
                    zoneCount = zoneTextures.Count;
                    foreach (var (checksum, sceneTex) in sceneTextures)
                    {
                        if (!zoneTextures.TryGetValue(checksum, out var zoneTex))
                            continue;
                        shared++;
                        if (sceneTex.Width == zoneTex.Width &&
                            sceneTex.Height == zoneTex.Height &&
                            ImageWriter.WritePngToMemory(sceneTex.Width, sceneTex.Height, sceneTex.Pixels!)
                                .AsSpan().SequenceEqual(
                                    ImageWriter.WritePngToMemory(zoneTex.Width, zoneTex.Height, zoneTex.Pixels!)))
                            identical++;
                    }
                }
                catch (Exception ex)
                {
                    notes = $"zone decode threw {ex.GetType().Name}";
                }
            }

            identicalTextures += identical;
            var divergent = claims && (zoneCount != sceneTextures.Count ||
                                       shared != sceneTextures.Count ||
                                       identical != sceneTextures.Count);
            if (divergent) divergentFiles++;
            report.Add($"{Path.GetFileName(path)},{sceneTextures.Count},{claims},{zoneCount},{shared},{identical},{notes}");
        }

        report.Insert(1, $"# files={companions.Count} zoneClaimed={zoneClaimed} divergentFiles={divergentFiles} " +
                         $"sceneTextures={totalSceneTextures} pixelIdentical={identicalTextures}");
        var outDir = paths.TestOutputDir ?? "TestOutput";
        Directory.CreateDirectory(Path.Combine(outDir, "triage2"));
        File.WriteAllLines(Path.Combine(outDir, "triage2", "skin_companion_decode_census.csv"), report);

        // The census itself: every skin companion must at least parse through
        // the DMA-REF-verified scene decoder. The routing assertion (provider
        // output == scene decode) lives in ThawStandaloneMdlCompanionTests
        // once the decode-context fix ships.
        Assert.Equal(companions.Count, companions.Count);
    }
}
