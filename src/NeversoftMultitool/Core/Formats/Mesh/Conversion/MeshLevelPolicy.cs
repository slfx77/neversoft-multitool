using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     The scan-time facts a level verdict is drawn from, projected out of the
///     GUI's row model so the rule itself can live in Core.
/// </summary>
/// <remarks>
///     <c>App/**</c> is excluded from the cross-platform target and the test project
///     builds <c>net10.0</c> only, so a predicate that stays in App cannot be tested
///     at all. This mirrors <see cref="XbxSkeletonEligibility" /> and
///     <c>MeshGuiFileFilterPolicy</c>.
/// </remarks>
public readonly record struct MeshLevelFacts(
    string FileName,
    string FilePath,
    string RelativePath,
    bool IsPsx,
    bool IsN64Model,
    bool IsPs2Geom,
    bool PsxIsSuperModel,
    PsxMeshFormatRevision PsxFormatRevision,
    Ps2SceneSubFormat Ps2SubFormat,
    bool HasPlacedPsxCompanion,
    bool HasSupportedLevelObjectCompanion,
    float N64MaxBoundsRadius,
    int ObjectCount,
    bool IsNdsLevel = false)
{
    /// <summary>THAW <c>.pak.ps2</c> worldzones are levels by sub-format.</summary>
    public bool IsPakWorldzone => Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone;
}

/// <summary>
///     Decides which converted content is level-scale, and how tall the viewer's
///     walk-mode eye stands in it. Both rules drive the viewer only — the default
///     camera mode (levels start in Fly, everything else in Orbit) and walk tuning.
/// </summary>
public static class MeshLevelPolicy
{
    // Explicit player-scale hints keep walk height independent of a level's
    // bounding sphere. SM2:EE's player is 90.861 units tall, so 82 remains the
    // observed-correct eye. THAW's skater is 73 units tall, yielding 66. The
    // older Apocalypse v3 level space needs the lower empirically requested
    // height while retaining a stable header-based classification.
    //
    // THPS settled on the bench anthropometry after two rounds of user
    // feedback: 58 and then 70 both read as too short beside skny's park
    // benches (eye level with the backrest). The authored bench legs run
    // ground-to-seat over 28.444 units ≈ 0.45 m, giving ~63 units/m, so a
    // 1.6 m eye sits near 100. The old "giant at 82" report (THPS2 DC) is
    // superseded by the two direct bench comparisons — trust the furniture.
    // A platform-scale explanation was ruled out by measurement: THPS2 PSX
    // and DC skny have identical extents (21655.6 x 5059.1 x 32914.2) and
    // identical 28.444-unit bench legs.
    public const double PsxLevelWalkEyeHeight = 82d;
    public const double ApocalypseLevelWalkEyeHeight = 56d;
    public const double ThpsLevelWalkEyeHeight = 100d;
    public const double ThawWorldzoneWalkEyeHeight = 66d;

    /// <summary>
    ///     GBA levels export at <c>GbaLevelGeometryWriter.Scale</c> (16 GLB units
    ///     per world unit); a skater's eye at ~1.4 world units gives 22.
    /// </summary>
    public const double GbaLevelWalkEyeHeight = 22d;

    /// <summary>
    ///     DS levels export at the file's own scale, and the carts' own skater says
    ///     what that is: proMullen stands 2.24 units, feet on zero. A skater's eye at
    ///     ~93% of standing height gives 2.1 — measured off the shipped model rather
    ///     than fitted to a level's bounding box.
    /// </summary>
    public const double NdsLevelWalkEyeHeight = 2.1d;

    /// <summary>
    ///     Identifies level-scale content for the viewer's default camera mode
    ///     (levels start in Fly, everything else in Orbit) and walk-height
    ///     tuning. Worldzones, Apocalypse level files, RW BSP worlds, scene
    ///     files, placed DDM levels, _g.psx level geometry, and PSX levels the
    ///     companion resolver recognizes (THPS bare-stem levels with sibling
    ///     _o.psx+_t.trg, Apocalypse chunk primaries) qualify.
    /// </summary>
    public static bool IsLevelContent(in MeshLevelFacts facts)
    {
        // Carved N64 bundles: bounds.bin's largest per-mesh radius separates
        // world content (level geometry AND level object banks, both authored in
        // world space) from characters and props, with an empty band between the
        // two classes. Precision 1.000 over 328 PS1-Rosetta-labelled bundles.
        //
        // This is IsWorldScale, NOT N64BundleClassifier.Classify: Classify
        // short-circuits objectCount <= 0 to Empty BEFORE the radius test, so a
        // world-scale bundle with no objects would silently change camera mode.
        if (facts.IsN64Model)
            return N64BundleClassifier.IsWorldScale(facts.N64MaxBoundsRadius);

        if (facts.Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone)
            return true;

        // A DS level is not a file but a model SET, so the scanner synthesises the
        // row and says outright what it is. There is no shape to infer it from: the
        // container spells a level's 135 world pieces and a skater's 46 body parts
        // identically, and only the cart's own name separates them.
        if (facts.IsNdsLevel)
            return true;

        // Supers are animated characters by definition (the anim-chunk flag),
        // never levels — Apocalypse's war/thebeast/bruce are v3 supers.
        if (facts.IsPsx && !facts.PsxIsSuperModel &&
            facts.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
            return true;

        // THPS1/THPS2 bare-stem levels carry no _g suffix; the scanner's
        // corpus-proven level-companion resolution is the reliable signal.
        if (facts.IsPsx && facts.HasSupportedLevelObjectCompanion)
            return true;

        var name = facts.FileName;
        if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.xbx", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.wpc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.ngc", StringComparison.OrdinalIgnoreCase) ||
            // The next-gen scene families are the same CScene container on Xbox 360
            // and PS3, so their level scenes are levels for the same reason.
            name.EndsWith(".scn.xen", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.ps3", StringComparison.OrdinalIgnoreCase) ||
            // Carved GBA level records are levels by definition.
            name.EndsWith(MeshTypeDetector.GbaLevelSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Only PLACED DDMs are levels; a standalone DDM with no PSX layout
        // companion is a character/prop model (THPS2X skaters, items).
        if (name.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
            return facts.HasPlacedPsxCompanion;

        // THPS4/THUG whole-level geoms ship under Levels/<Stem>/ (both on
        // disc and inside the scene PREs); prop/vehicle geoms live elsewhere.
        if (facts.IsPs2Geom && PathContainsLevelsSegment(facts))
            return true;

        return name.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The walk-mode eye height for level content, or null to leave the viewer
    ///     on its own default.
    /// </summary>
    /// <remarks>
    ///     Note the trailing <c>!IsPsx =&gt; null</c>: RW BSP worlds, <c>.scn.*</c>
    ///     scenes and placed DDMs deliberately get NO eye height today. That is
    ///     current behaviour, preserved verbatim, not an oversight to be filled in
    ///     without measuring the games' player heights.
    /// </remarks>
    public static double? ResolveWalkEyeHeight(in MeshLevelFacts facts, bool isLevel)
    {
        if (!isLevel) return null;
        if (facts.IsPakWorldzone) return ThawWorldzoneWalkEyeHeight;
        if (facts.FileName.EndsWith(MeshTypeDetector.GbaLevelSuffix, StringComparison.OrdinalIgnoreCase))
            return GbaLevelWalkEyeHeight;
        if (facts.IsNdsLevel) return NdsLevelWalkEyeHeight;

        // N64 bundles are emitted at k / ScaleDivisor with k = 1 for non-supers,
        // which IS the PS1 translation divisor — the same level exports at the
        // same world scale on both platforms, so the PS1 eye heights transfer
        // unchanged. Only level GEOMETRY gets one: an object bank flies but has
        // no floor to stand on. An unnamed bundle falls back to the THPS eye.
        if (facts.IsN64Model)
        {
            if (!N64BundleClassifier.IsLevel(facts.N64MaxBoundsRadius, facts.ObjectCount))
                return null;

            return facts.FileName.EndsWith("_g.psx.n64", StringComparison.OrdinalIgnoreCase)
                ? PsxLevelWalkEyeHeight
                : ThpsLevelWalkEyeHeight;
        }

        if (!facts.IsPsx) return null;
        if (facts.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
            return ApocalypseLevelWalkEyeHeight;

        // THPS1/THPS2 levels are bare-stem (no _g suffix) and recognized via
        // their level-object companions; Spider-Man/SM2:EE levels keep the
        // taller superhero eye.
        if (facts.HasSupportedLevelObjectCompanion &&
            !facts.FileName.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase))
        {
            return ThpsLevelWalkEyeHeight;
        }

        return PsxLevelWalkEyeHeight;
    }

    private static bool PathContainsLevelsSegment(in MeshLevelFacts facts)
    {
        return ContainsLevelsSegment(facts.RelativePath) || ContainsLevelsSegment(facts.FilePath);

        static bool ContainsLevelsSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var parts = path.Split(['/', '\\'], StringSplitOptions.None);
            // The last part is the file name; only directory segments count.
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "Levels", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
