using QbKeyTable = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal static class Ps2GeomRenderSemantics
{
    private const float WorldzoneBlendOverlayDepthBias = 0.005f;
    private const float WorldzoneMaskCutoutDepthBias = 0.010f;
    private const float WorldzoneRenderGroupSpacing = 0.002f;
    private const int FixBlendOpaqueThreshold = 96;

    internal static IReadOnlyList<WorldzoneLeafDrawItem> OrderWorldzoneLeavesForDraw(IReadOnlyList<Ps2GeomLeaf> leaves)
    {
        return leaves
            .Select(static (leaf, index) => new WorldzoneLeafDrawItem(leaf, index, 0))
            .OrderBy(static item => GetWorldzoneRenderOrderKey(item.Leaf))
            .ThenBy(static item => item.LeafIndex)
            .Select(static (item, drawIndex) => item with { DrawIndex = drawIndex })
            .ToArray();
    }

    internal static float ComputeWorldzoneMaterialDepthBias(Ps2GeomLeaf leaf, string alphaMode)
    {
        if (leaf.IsBillboard)
            return 0f;

        var modeBias = alphaMode switch
        {
            "MASK" => WorldzoneMaskCutoutDepthBias,
            "BLEND" => WorldzoneBlendOverlayDepthBias,
            _ => 0f
        };
        if (modeBias <= 0f)
            return 0f;

        var groupBias = leaf.GroupChecksum is > 0u and <= 0xFFu
            ? leaf.GroupChecksum * WorldzoneRenderGroupSpacing
            : 0f;

        return groupBias + modeBias;
    }

    internal static string ClassifyWorldzoneAlphaMode(Ps2GeomLeaf leaf)
    {
        var alpha = leaf.DmaAlpha1;
        var alphaBlend = (byte)(alpha & 0xFF);
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        var fixValue = (int)((alpha >> 32) & 0xFF);

        var alphaTestMask = UsesAlphaTestMask(leaf.DmaTest1);
        var isAdditive = aField == 0 && bField == 2 && dField == 1;
        var isSubtractive = aField == 2 && bField == 0 && dField == 1;

        if (leaf.IsBillboard)
        {
            // Additive/subtractive billboards are glow cards — z_dn's building
            // blink lights, z_sm's bulb/reflect sheets, 580 additive corpus-wide
            // and every one a light (B11) — forcing them to MASK rendered solid
            // panes. Ordinary billboards keep the cutout MASK so foliage does
            // not turn into translucent panes.
            return isAdditive || isSubtractive ? "BLEND" : "MASK";
        }
        var isStandardBlend = IsStandardSourceAlphaBlend(alphaBlend);
        var isFixedStandardBlend = UsesFixedSourceAlphaBlend(alphaBlend);
        var isOpaqueEquivalent = alphaBlend is 0x00 or 0x0A or 0x1A;

        if (isAdditive || isSubtractive || isStandardBlend)
            return "BLEND";

        if (isFixedStandardBlend)
        {
            if (fixValue < FixBlendOpaqueThreshold)
                return "BLEND";

            return alphaTestMask ? "MASK" : "OPAQUE";
        }

        if (UsesDestinationAlphaBlend(alphaBlend))
            return alphaTestMask ? "MASK" : "OPAQUE";

        if (alphaTestMask)
            return "MASK";

        return isOpaqueEquivalent ? "OPAQUE" : "BLEND";
    }

    internal static uint GetWorldzoneRenderOrderKey(Ps2GeomLeaf leaf)
    {
        if (leaf.GroupChecksum is > 0 and <= 0xFF)
            return leaf.GroupChecksum;

        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        if (alphaBlend is 0x0A or 0x1A or 0x00)
            return 0x0100;
        if (IsStandardSourceAlphaBlend(alphaBlend))
            return 0x0200;
        if (UsesDestinationAlphaBlend(alphaBlend))
            return 0x0300;
        return 0x0400;
    }

    /// <summary>
    ///     THAW's TOD system toggles NODES whose authored names carry the
    ///     NightOn_NN / NightOff_NN markers (matching the QB corpus's
    ///     TOD_NightOn_NN / TOD_NightOff_NN script groups), so the layer is
    ///     read from the leaf's resolved node name. The previous
    ///     additive-blend heuristic contradicted the authored tags in BOTH
    ///     directions: it dropped always-on additive effects from Day exports
    ///     (interior ceiling lights, steam, graffiti, water splashes —
    ///     z_dn 178, z_lv 416 leaves) and kept non-additive night content
    ///     (light bulbs, lit window panes — z_sm 206 leaves). Bare "night"
    ///     substrings deliberately do NOT match: Z_HO_HO_stores_night_salon
    ///     is a storefront and z_dn's nightSkybox is the permanently-night
    ///     district's only skybox.
    /// </summary>
    internal static Ps2GeomRenderLayer ClassifyWorldzoneRenderLayer(Ps2GeomLeaf leaf)
    {
        var name = QbKeyTable.TryResolve(leaf.Checksum);
        if (name == null)
            return Ps2GeomRenderLayer.Base;

        if (name.Contains("nightoff", StringComparison.OrdinalIgnoreCase))
            return Ps2GeomRenderLayer.DayOverlay;

        return name.Contains("nighton", StringComparison.OrdinalIgnoreCase)
            ? Ps2GeomRenderLayer.NightOverlay
            : Ps2GeomRenderLayer.Base;
    }

    /// <summary>
    ///     The texture-bake class the portable exporter would apply — the
    ///     STRICTER test from <c>GltfModelExporter.ProcessTextureForPortableGltf</c>
    ///     (it requires C∈{0,2} where <see cref="ClassifyWorldzoneAlphaMode" />
    ///     ignores C). Surfaced for diagnostics so a classifier/bake mismatch
    ///     is visible per leaf instead of silently rendering solid.
    /// </summary>
    internal static string ClassifyPortableBakeClass(byte alphaBlend)
    {
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        if (aField == 0 && bField == 2 && dField == 1 && cField is 0 or 2)
            return "additive";
        if (aField == 2 && bField == 0 && dField == 1 && cField is 0 or 2)
            return "subtractive";
        return "none";
    }

    internal static bool IsStandardSourceAlphaBlend(byte alphaBlend)
    {
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        return aField == 0 && bField == 1 && cField == 0 && dField == 1;
    }

    internal static bool UsesFixedSourceAlphaBlend(byte alphaBlend)
    {
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        return aField == 0 && bField == 1 && cField == 2 && dField == 1;
    }

    internal static bool BlendUsesSourceAlpha(byte alphaBlend)
    {
        var cField = (alphaBlend >> 4) & 0x03;
        return cField == 0;
    }

    internal static bool UsesDestinationAlphaBlend(byte alphaBlend)
    {
        var cField = (alphaBlend >> 4) & 0x03;
        return cField == 1;
    }

    internal static bool WritesFramebufferAlpha(Ps2GeomLeaf leaf)
    {
        var fbmsk = (uint)((leaf.DmaFrame1 >> 32) & 0xFFFFFFFFUL);
        var alphaByteMask = (fbmsk >> 24) & 0xFF;
        return alphaByteMask != 0xFF;
    }

    internal static bool TryGetDestinationAlphaSourceMaskMode(byte alphaBlend, out bool invertMask)
    {
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        invertMask = false;

        if (cField != 1)
            return false;

        if (aField == 0 && bField == 1 && dField == 1)
            return true;

        if (aField == 1 && bField == 0 && dField == 0)
        {
            invertMask = true;
            return true;
        }

        return false;
    }

    internal static bool UsesAlphaTestMask(ulong test)
    {
        var ateEnabled = (test & 0x1UL) != 0;
        if (!ateEnabled)
            return false;

        var atst = (int)((test >> 1) & 0x7);
        if (atst == 1) // ATST_ALWAYS is a pass-through.
            return false;

        // GEQUAL vs AREF=0 always passes — THUG mesh.cpp:402 programs
        // ATE=1/AGEQUAL/AREF unconditionally for every material, so this
        // engine-default state is not a real cutout test (treating it as one
        // exported MASK-cutoff-0 == fully-opaque materials, e.g. the Crown
        // chrome envmap stripe).
        var arefValue = (int)((test >> 4) & 0xFF);
        if (atst == 5 && arefValue == 0)
            return false;

        var afail = (int)((test >> 12) & 0x3);
        return afail is 0 or 2;
    }

    /// <summary>
    ///     True when the alpha-test register carries a DELIBERATE cutout
    ///     threshold, as opposed to the engine's unconditional default
    ///     (ATE=1/AGEQUAL/AREF&lt;=1, which kills only a == 0). The default
    ///     still counts as a mask for classification, but its computed cutoff
    ///     (1/128) is not an authored threshold.
    /// </summary>
    internal static bool HasDeliberateAlphaTestCutoff(ulong test)
    {
        if (!UsesAlphaTestMask(test))
            return false;

        var atst = (int)((test >> 1) & 0x7);
        var aref = (int)((test >> 4) & 0xFF);
        return atst switch
        {
            5 => aref >= 2, // GEQUAL: AREF <= 1 kills only a == 0.
            6 => aref >= 1, // GREATER: AREF 0 kills only a == 0.
            _ => true
        };
    }

    internal static float ComputeAlphaMaskCutoff(ulong test)
    {
        var aref = (int)((test >> 4) & 0xFF);
        var atst = (int)((test >> 1) & 0x7);
        if (atst == 6) // ATST_GREATER is exclusive: pass when alpha > AREF.
            aref = Math.Min(255, aref + 1);

        // Two-domain rule: exported PNG alpha is rescaled ×255/128 (GS 128 =
        // opaque), so the cutoff must live in the same domain — AREF/128, not
        // AREF/255 (a GS AREF of 20 cuts at ~0.156 of PNG alpha).
        return Math.Min(aref / 128f, 1f);
    }
}
