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
            _ => 0f,
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
        if (leaf.IsBillboard)
            return "MASK";

        var alpha = leaf.DmaAlpha1;
        var alphaBlend = (byte)(alpha & 0xFF);
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        var fixValue = (int)((alpha >> 32) & 0xFF);

        var alphaTestMask = UsesAlphaTestMask(leaf.DmaTest1);
        var isAdditive = aField == 0 && bField == 2 && dField == 1;
        var isSubtractive = aField == 2 && bField == 0 && dField == 1;
        var isStandardBlend = IsStandardSourceAlphaBlend(alphaBlend);
        var isFixedStandardBlend = UsesFixedSourceAlphaBlend(alphaBlend);
        var isOpaqueEquivalent = alphaBlend is 0x00 or 0x0A or 0x1A;

        if (isAdditive || isSubtractive || isStandardBlend)
            return "BLEND";

        if (isFixedStandardBlend)
            return fixValue < FixBlendOpaqueThreshold ? "BLEND" : alphaTestMask ? "MASK" : "OPAQUE";

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

    internal static Ps2GeomRenderLayer ClassifyWorldzoneRenderLayer(Ps2GeomLeaf leaf)
    {
        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;

        var isAdditiveOverlay = aField == 0 && bField == 2 && dField == 1;
        return isAdditiveOverlay && !leaf.IsBillboard
            ? Ps2GeomRenderLayer.NightOverlay
            : Ps2GeomRenderLayer.Base;
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

        var afail = (int)((test >> 12) & 0x3);
        return afail is 0 or 2;
    }

    internal static float ComputeAlphaMaskCutoff(ulong test)
    {
        var aref = (int)((test >> 4) & 0xFF);
        var atst = (int)((test >> 1) & 0x7);
        if (atst == 6) // ATST_GREATER is exclusive: pass when alpha > AREF.
            aref = Math.Min(255, aref + 1);

        return aref / 255f;
    }
}

internal readonly record struct WorldzoneLeafDrawItem(
    Ps2GeomLeaf Leaf,
    int LeafIndex,
    int DrawIndex);
