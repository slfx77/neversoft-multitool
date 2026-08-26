using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     The extent a DS model set occupies, and the one question that follows from it:
///     is this set a WORLD, or a model?
///
///     A model set is just "the geometry files sharing an id", and the three carts use
///     that one container for both — a level's 135 world-space pieces and a skater's 46
///     body parts are spelled identically. Compositing without telling them apart emits
///     a "level" for every multi-part car, prop and menu icon in the cart.
///
///     The pieces themselves settle it, because a DS level's pieces are authored in
///     WORLD space (measured: no level piece in any cart is authored at its own origin),
///     so the union of their declared boxes IS the world. Over every set with at least
///     six measurable pieces across the three carts the two populations separate with an
///     EMPTY BAND: models top out at a 78.0-unit span and worlds start at 107.8, so
///     <see cref="WorldScaleSpan" /> is that band's midpoint rather than a tuned
///     threshold. The result is stable — every piece-count floor from 6 to 12 selects
///     the same 22 sets (8 Sk8land, 7 Downhill Jam, 7 Proving Ground).
///
///     Sk8land refereed it independently: its ARM9 overlays each hold a manifest table
///     naming one set's pieces, and the eight overlay manifests are EXACTLY the eight
///     sets this classifier calls worlds — while the three manifests that live in ARM9
///     itself (the skater's 46 parts, 96 unit-sized icons, and a four-piece set) are
///     exactly the ones it rejects.
///
///     Pieces carrying the authoring tool's boilerplate box are skipped, not measured:
///     see <see cref="NdsGeometryFile.HasBoilerplateBox" />. They are authored-empty and
///     their nonsense extents would add ~65,000 units to any set that contains one.
/// </summary>
public static class NdsModelSetBounds
{
    /// <summary>
    ///     Midpoint of the empty band between model-scale and world-scale sets, in world
    ///     units. Measured, not chosen — see the type remarks.
    /// </summary>
    public const float WorldScaleSpan = 93f;

    /// <summary>
    ///     Fewest measurable pieces a set needs before its span is judged. Below this a
    ///     set is too small to be a level however far apart its pieces sit; the corpus
    ///     answer does not move anywhere in 6..12.
    /// </summary>
    public const int WorldPieceFloor = 6;

    /// <summary>
    ///     The union of the set's declared boxes, over the pieces that declare a real
    ///     one. Returns false when no piece does.
    /// </summary>
    public static bool TryMeasure(
        IEnumerable<NdsGeometryFile> pieces, out Vector3 min, out Vector3 max, out int measured)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        measured = 0;
        foreach (var piece in pieces)
        {
            if (piece.HasBoilerplateBox)
                continue;
            var half = piece.DeclaredExtent * 0.5f;
            min = Vector3.Min(min, piece.DeclaredCentre - half);
            max = Vector3.Max(max, piece.DeclaredCentre + half);
            measured++;
        }

        if (measured != 0)
            return true;

        min = Vector3.Zero;
        max = Vector3.Zero;
        return false;
    }

    /// <summary>
    ///     True when the set is a world — a level whose pieces are authored where they
    ///     belong — rather than a model assembled from parts.
    /// </summary>
    public static bool IsWorldScale(IEnumerable<NdsGeometryFile> pieces)
    {
        if (!TryMeasure(pieces, out var min, out var max, out var measured)
            || measured < WorldPieceFloor)
        {
            return false;
        }

        var size = max - min;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z)) >= WorldScaleSpan;
    }
}
