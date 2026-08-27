using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins <see cref="MeshLevelPolicy" /> against a verbatim copy of the rule as it
///     stood inside the GUI before it moved to Core.
/// </summary>
/// <remarks>
///     The rule was untestable while it lived in <c>App/**</c> (excluded from the
///     cross-platform target), so the risk this move carries is a silent
///     transcription slip, not a design error. The reference bodies below are the
///     pre-move originals character-for-character; the cross-product below is
///     DERIVED rather than hand-written because two of the legs are easy to
///     "tidy" into something that reads identically and behaves differently:
///     the N64 leg is <c>IsWorldScale(radius)</c>, which — unlike
///     <c>Classify</c> — does not short-circuit on a zero object count, and
///     <c>ResolveWalkEyeHeight</c> ends with <c>!IsPsx =&gt; null</c> AFTER the
///     worldzone/GBA/N64 branches, so BSP/SCN/DDM levels deliberately get none.
/// </remarks>
public sealed class MeshLevelPolicyTests
{
    private static readonly string[] FileNames =
    [
        "skateshop.bsp", "SkCon.scn.xbx", "level.scn.wpc", "level.scn.ngc",
        "0_hangar.lvl.gba", "13_spider_man.chr.gba",
        "l1a1_g.psx", "skmall.psx", "hawk.psx", "items.psx",
        "mall_o.ddm", "Itm_Bonus01.ddm",
        "alc.geom.ps2", "car.geom.ps2",
        "skater_lasek.skin.ps2", "z_downtown.pak.ps2",
        "005_skdown_g.psx.n64", "008_c_kart.psx.n64",
        "0067ee06.geometry.bin", "no_extension"
    ];

    private static readonly string[] Paths =
    [
        "", "Levels/alc/alc.geom.ps2", @"pre\AlcScn\Levels\alc\alc.geom.ps2",
        "Models/car/car.geom.ps2", "levels/0_hangar.lvl.gba"
    ];

    // Straddles N64BundleClassifier.WorldScaleRadius (1213) and the measured
    // per-class ceiling/floor (1129 / 1298), plus the LevelMinObjectCount edge.
    private static readonly float[] Radii = [0f, 770f, 1129f, 1212f, 1213f, 1298f, 2040f];
    private static readonly int[] ObjectCounts = [0, 1, 23, 24, 642];

    /// <summary>
    ///     Every combination of the facts the rule reads must agree with the
    ///     pre-move body. Hand-written cases miss the two legs named above.
    /// </summary>
    [Fact]
    public void MatchesThePreMoveRuleOverEveryFactCombination()
    {
        var compared = 0;
        var levels = 0;
        var withEye = 0;

        foreach (var name in FileNames)
        foreach (var path in Paths)
        foreach (var subFormat in Enum.GetValues<Ps2SceneSubFormat>())
        foreach (var revision in Enum.GetValues<PsxMeshFormatRevision>())
        foreach (var isPsx in Bools)
        foreach (var isN64 in Bools)
        foreach (var isPs2Geom in Bools)
        foreach (var isSuper in Bools)
        foreach (var placedPsx in Bools)
        foreach (var levelObjects in Bools)
        foreach (var radius in Radii)
        foreach (var objectCount in ObjectCounts)
        {
            var facts = new MeshLevelFacts(
                name, path, path, isPsx, isN64, isPs2Geom, isSuper, revision, subFormat,
                placedPsx, levelObjects, radius, objectCount);

            var expectedLevel = ReferenceIsLevelModel(facts);
            var actualLevel = MeshLevelPolicy.IsLevelContent(facts);
            Assert.Equal(expectedLevel, actualLevel);

            var expectedEye = ReferenceResolveWalkEyeHeight(facts, expectedLevel);
            var actualEye = MeshLevelPolicy.ResolveWalkEyeHeight(facts, actualLevel);
            Assert.Equal(expectedEye, actualEye);

            compared++;
            if (actualLevel) levels++;
            if (actualEye != null) withEye++;
        }

        // Guard the guard: a cross-product that never produced a level, or never
        // produced an eye height, would pass vacuously.
        // 20 names x 5 paths x 7 sub-formats x 5 PSX revisions x 2^6 flags
        // x 7 radii x 5 object counts.
        Assert.Equal(7_840_000, compared);
        Assert.True(levels > 0 && levels < compared, $"levels={levels} of {compared}");
        Assert.True(withEye > 0 && withEye < levels, $"withEye={withEye} of {levels} levels");
    }

    private static bool[] Bools => [false, true];

    /// <summary>
    ///     The cases the cross-product proves but which are worth stating in
    ///     English, because they read like bugs and are not.
    /// </summary>
    [Fact]
    public void LevelsThatDeliberatelyGetNoWalkEyeHeight()
    {
        // A .bsp world, a .scn scene and a placed .ddm are all levels...
        foreach (var name in new[] { "skateshop.bsp", "SkCon.scn.xbx", "mall_o.ddm" })
        {
            var facts = Facts(name) with { HasPlacedPsxCompanion = true };
            Assert.True(MeshLevelPolicy.IsLevelContent(facts), name);
            // ...and none of them has a measured player height, so walk mode keeps
            // the viewer's own default rather than an invented one.
            Assert.Null(MeshLevelPolicy.ResolveWalkEyeHeight(facts, true));
        }
    }

    [Fact]
    public void N64LevelnessIgnoresObjectCountButTheEyeHeightDoesNot()
    {
        // IsWorldScale only looks at the radius: an object-less world-scale bundle
        // is still level-scale for camera purposes. Classify would call it Empty.
        var bundle = Facts("049_skdown.psx.n64") with
        {
            IsN64Model = true, N64MaxBoundsRadius = 2040f, ObjectCount = 0
        };
        Assert.True(MeshLevelPolicy.IsLevelContent(bundle));
        Assert.Equal(N64BundleClass.Empty, N64BundleClassifier.Classify(2040f, 0));

        // The eye height does gate on it — an object bank flies but has no floor.
        Assert.Null(MeshLevelPolicy.ResolveWalkEyeHeight(bundle, true));
        Assert.Equal(
            MeshLevelPolicy.ThpsLevelWalkEyeHeight,
            MeshLevelPolicy.ResolveWalkEyeHeight(bundle with { ObjectCount = 642 }, true));

        // A carved N64 Spider-Man level keeps the taller superhero eye via _g.psx.n64.
        var spidey = bundle with { FileName = "005_l1a1_g.psx.n64", ObjectCount = 642 };
        Assert.Equal(
            MeshLevelPolicy.PsxLevelWalkEyeHeight,
            MeshLevelPolicy.ResolveWalkEyeHeight(spidey, true));
    }

    [Fact]
    public void GbaLevelsAreLevelsAndGbaCharactersAreNot()
    {
        var level = Facts("0_hangar" + MeshTypeDetector.GbaLevelSuffix);
        Assert.True(MeshLevelPolicy.IsLevelContent(level));
        Assert.Equal(
            MeshLevelPolicy.GbaLevelWalkEyeHeight,
            MeshLevelPolicy.ResolveWalkEyeHeight(level, true));

        Assert.False(MeshLevelPolicy.IsLevelContent(Facts("13_spider_man.chr.gba")));
    }

    [Fact]
    public void PsxSupersAreCharactersEvenInApocalypseLevelSpace()
    {
        var bruce = Facts("bruce.psx") with
        {
            IsPsx = true, PsxIsSuperModel = true,
            PsxFormatRevision = PsxMeshFormatRevision.ApocalypseV3
        };
        Assert.False(MeshLevelPolicy.IsLevelContent(bruce));

        var world = bruce with { PsxIsSuperModel = false };
        Assert.True(MeshLevelPolicy.IsLevelContent(world));
        Assert.Equal(
            MeshLevelPolicy.ApocalypseLevelWalkEyeHeight,
            MeshLevelPolicy.ResolveWalkEyeHeight(world, true));
    }

    [Fact]
    public void Ps2GeomsAreLevelsOnlyUnderALevelsDirectorySegment()
    {
        var geom = Facts("alc.geom.ps2") with { IsPs2Geom = true };
        Assert.False(MeshLevelPolicy.IsLevelContent(geom));
        Assert.True(MeshLevelPolicy.IsLevelContent(
            geom with { RelativePath = "Levels/alc/alc.geom.ps2" }));
        // Backslashes and the archive-internal scene PRE path work too.
        Assert.True(MeshLevelPolicy.IsLevelContent(
            geom with { FilePath = @"pre\AlcScn\Levels\alc\alc.geom.ps2" }));
        // A trailing "Levels" that IS the file name does not count.
        Assert.False(MeshLevelPolicy.IsLevelContent(
            geom with { RelativePath = "Models/Levels" }));
    }

    private static MeshLevelFacts Facts(string fileName) => new(
        fileName, fileName, fileName, false, false, false, false,
        PsxMeshFormatRevision.Unknown, Ps2SceneSubFormat.None, false, false, 0f, 0);

    // ---- Reference implementations: the pre-move bodies, verbatim. ----

    private static bool ReferenceIsLevelModel(in MeshLevelFacts entry)
    {
        if (entry.IsN64Model)
            return N64BundleClassifier.IsWorldScale(entry.N64MaxBoundsRadius);

        if (entry.Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone)
            return true;

        if (entry.IsPsx && !entry.PsxIsSuperModel &&
            entry.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
            return true;

        if (entry.IsPsx && entry.HasSupportedLevelObjectCompanion)
            return true;

        var name = entry.FileName;
        if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.xbx", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.wpc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.ngc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(MeshTypeDetector.GbaLevelSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
            return entry.HasPlacedPsxCompanion;

        if (entry.IsPs2Geom && ReferencePathContainsLevelsSegment(entry))
            return true;

        return name.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReferencePathContainsLevelsSegment(in MeshLevelFacts entry)
    {
        return ContainsLevelsSegment(entry.RelativePath) || ContainsLevelsSegment(entry.FilePath);

        static bool ContainsLevelsSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var parts = path.Split(['/', '\\'], StringSplitOptions.None);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "Levels", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    private static double? ReferenceResolveWalkEyeHeight(in MeshLevelFacts entry, bool isLevel)
    {
        if (!isLevel) return null;
        if (entry.IsPakWorldzone) return 66d;
        if (entry.FileName.EndsWith(MeshTypeDetector.GbaLevelSuffix, StringComparison.OrdinalIgnoreCase))
            return 22d;

        if (entry.IsN64Model)
        {
            if (!N64BundleClassifier.IsLevel(entry.N64MaxBoundsRadius, entry.ObjectCount))
                return null;

            return entry.FileName.EndsWith("_g.psx.n64", StringComparison.OrdinalIgnoreCase)
                ? 82d
                : 100d;
        }

        if (!entry.IsPsx) return null;
        if (entry.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
            return 56d;

        if (entry.HasSupportedLevelObjectCompanion &&
            !entry.FileName.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase))
        {
            return 100d;
        }

        return 82d;
    }
}
