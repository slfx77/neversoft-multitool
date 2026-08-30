using System.Globalization;
using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     The loader's own naming for a DS model set, and the bindings that fall out of it.
///
///     A model set is keyed by one id, and its geometry and animation take a second:
///     the ARM9 composes <c>.\%08x.%s.bin</c> and <c>.\%08x.%08x.%s.bin</c> over the
///     kinds <c>textureinfo</c>, <c>collisionspheres</c>, <c>pvs</c>, <c>geometry</c>
///     and <c>animation</c>. So a model and its texture bank are related by SPELLING
///     — they share the first id — and nothing has to be inferred from content.
///
///     That matters because the alternative, <see cref="NdsTextureBankResolver" />,
///     can only speak when exactly one bank in the whole container is compatible with
///     a model's GX state. It is sound but quiet: it resolves 463 / 280 / 324 of the
///     866 / 946 / 1,330 textured models across the three carts, where the stated
///     binding resolves 866 / 944 / 1,329 — and the two never disagree on a model
///     both can name. The join therefore stays as the fallback for a model whose name
///     was not recovered.
/// </summary>
public static class NdsModelSet
{
    private const string GeometrySuffix = ".geometry.bin";

    /// <summary>
    ///     Reads the two ids out of a recovered <c>.\&lt;idA&gt;.&lt;idB&gt;.geometry.bin</c>
    ///     name. Returns false for any other shape, including the plain
    ///     <c>&lt;crc32&gt;.bin</c> an unnamed file keeps.
    /// </summary>
    public static bool TryParseGeometryName(string? name, out uint idA, out uint idB)
    {
        idA = 0;
        idB = 0;
        if (name == null || !name.EndsWith(GeometrySuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = name.AsSpan(0, name.Length - GeometrySuffix.Length);
        if (body.StartsWith(".\\", StringComparison.Ordinal))
            body = body[2..];

        // Exactly "<8 hex>.<8 hex>" — a third component means an indexed animation
        // clip, not a geometry file, and anything else is not a composed name.
        if (body.Length != 17 || body[8] != '.')
            return false;

        return uint.TryParse(body[..8], NumberStyles.HexNumber, null, out idA)
               && uint.TryParse(body[9..], NumberStyles.HexNumber, null, out idB);
    }

    /// <summary>
    ///     The single opaque id in a one-id animation name
    ///     (<c>.\&lt;id&gt;.animation.bin</c>), the form Downhill Jam and Proving
    ///     Ground use. Sk8land's indexed clips carry two ids and an ordinal and are
    ///     deliberately NOT matched here.
    /// </summary>
    public static bool TryParseAnimationName(string? name, out uint animationId)
    {
        animationId = 0;
        const string suffix = ".animation.bin";
        if (name == null || !name.StartsWith(".\\", StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || name.Length != 2 + 8 + suffix.Length)
        {
            return false;
        }

        return uint.TryParse(
            name.AsSpan(2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out animationId);
    }

    /// <summary>The container name of one geometry file in a set.</summary>
    public static string GeometryName(uint idA, uint idB) => $".\\{idA:x8}.{idB:x8}.geometry.bin";

    /// <summary>The name of the texture bank belonging to the model set keyed by <paramref name="idA" />.</summary>
    public static string TextureBankName(uint idA) => $".\\{idA:x8}.textureinfo.bin";

    /// <summary>
    ///     The container key of that bank — the same CRC-32-of-the-lowercased-name the
    ///     index stores, so it can be looked up without the name dictionary resolving it.
    /// </summary>
    public static uint TextureBankKey(uint idA) => GobNames.Hash(TextureBankName(idA));

    /// <summary>
    ///     Sk8land's indexed clip library for a model:
    ///     <c>.\&lt;idA&gt;.&lt;idB&gt;.&lt;n&gt;.animation.bin</c>. Clips are
    ///     contiguous from 0 (corpus-measured: runs of exactly 26 and 225, no
    ///     holes), so enumeration stops at the first missing index.
    /// </summary>
    public static string ClipName(uint idA, uint idB, int index)
        => $".\\{idA:x8}.{idB:x8}.{index}.animation.bin";

    public static uint ClipKey(uint idA, uint idB, int index)
        => GobNames.Hash(ClipName(idA, idB, index));
}
