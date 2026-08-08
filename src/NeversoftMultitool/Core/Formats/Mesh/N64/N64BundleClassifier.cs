namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>What a carved N64 model bundle holds.</summary>
public enum N64BundleClass
{
    /// <summary>An authored-empty slot — the PS1 <c>_l</c> texture library, reduced to a 24-byte header.</summary>
    Empty,

    /// <summary>A character, vehicle or prop. Model-space geometry, viewed in isolation.</summary>
    CharacterProp,

    /// <summary>The PS1 <c>_o</c> object bank: world-placed props for one level.</summary>
    ObjectBank,

    /// <summary>Level geometry — the PS1 <c>_g</c> file, or a THPS bare-stem level.</summary>
    Level
}

/// <summary>
///     Classifies a carved bundle from two measurements: the largest bounding
///     radius in its <c>bounds.bin</c> and its object count.
///     <para>
///         This lives in Core rather than beside its GUI consumer because
///         <c>App/**</c> is excluded from the cross-platform target and the test
///         project builds <c>net10.0</c> only — a predicate in the App layer
///         cannot be tested at all.
///     </para>
///     <para>
///         World content versus character/prop separates with an EMPTY BAND.
///         Measured 2026-08-07 over ALL 450 non-empty bundles in the four carts
///         (not the 328 that carry a PS1-Rosetta label — a labelled subset
///         reported a wider 1045..1298 band, and two unlabelled models sit
///         above 1045): character/prop tops out at <b>1129</b> (Spider-Man) and
///         world content starts at <b>1298</b> (<c>l8a5_g.psx</c>), with
///         nothing in between. Per ROM the ceiling/floor pairs are THPS1
///         770/2016, THPS2 906/1538, THPS3 1115/2040, Spider-Man 1129/1298.
///         <see cref="WorldScaleRadius" /> is that band's midpoint, so it is
///         not a tuned constant — every threshold inside the band scores
///         identically on this corpus.
///     </para>
///     <para>
///         The THPS park-editor piece sets (<c>sked&lt;N&gt;_w&lt;M&gt;</c>, 40
///         bundles) were the one labelling risk, since the classification
///         probe's own oracle assumed they were levels. Measured
///         (<c>tools/diagnostics/n64_sked_piece_probe.py</c>): they span radius
///         4,122-10,357 with 27-49 objects, i.e. 3.4-8.5x the threshold, against
///         a THPS2 band of 906..1538. They are world geometry however they are
///         labelled, and cannot move the rule. Their PS1 siblings agree:
///         <c>sked&lt;N&gt;</c> owns a <c>_t.trg</c> and a <c>_l.psx</c>; the
///         <c>_w&lt;M&gt;</c> piece sets own neither.
///     </para>
///     <para>
///         Level versus its own BANK is the fuzzy leg — both are world-placed,
///         so no size feature resolves them; object count is the best single
///         feature, at accuracy 0.9145 in the labelled study. That is why the
///         viewer keys its camera on <see cref="IsWorldScale" /> rather than on
///         <see cref="IsLevel" />: the world/character split is clean by
///         measurement (the band is empty over all 450 non-empty bundles;
///         populations 241 world / 209 character-prop), whereas the labelled
///         study's level-vs-bank recall of 0.914 is a claim about a subset this
///         code cannot re-derive at runtime.
///     </para>
/// </summary>
public static class N64BundleClassifier
{
    /// <summary>
    ///     Midpoint of the measured empty band between character/prop (max
    ///     1129) and world content (min 1298). World units. Centring on the
    ///     real band rather than the labelled-subset one leaves classification
    ///     unchanged on this corpus — the range between them is empty by
    ///     measurement — while putting the most room on both sides for a build
    ///     nobody has looked at yet.
    /// </summary>
    public const float WorldScaleRadius = 1213f;

    /// <summary>
    ///     Object count separating level geometry from a level's object bank.
    ///     Accuracy 0.9145 — the best single feature, not a clean split.
    /// </summary>
    public const int LevelMinObjectCount = 24;

    public static N64BundleClass Classify(float maxBoundsRadius, int objectCount)
    {
        if (objectCount <= 0)
            return N64BundleClass.Empty;
        if (!IsWorldScale(maxBoundsRadius))
            return N64BundleClass.CharacterProp;

        return objectCount >= LevelMinObjectCount
            ? N64BundleClass.Level
            : N64BundleClass.ObjectBank;
    }

    /// <summary>
    ///     Whether the bundle is world content — level geometry or a level's
    ///     object bank — as opposed to a character or prop. This is the camera
    ///     predicate: fly through world content, orbit a model. Precision 1.000,
    ///     recall 0.914 (all 159 levels and 75 of 97 banks fly, all 169
    ///     character/props orbit, zero false positives).
    /// </summary>
    public static bool IsWorldScale(float maxBoundsRadius)
    {
        return maxBoundsRadius >= WorldScaleRadius;
    }

    /// <summary>
    ///     Whether the bundle is level GEOMETRY specifically. Used for the walk
    ///     eye height, which only means something when there is a floor: an
    ///     object bank flies but has no ground of its own.
    /// </summary>
    public static bool IsLevel(float maxBoundsRadius, int objectCount)
    {
        return Classify(maxBoundsRadius, objectCount) == N64BundleClass.Level;
    }
}
