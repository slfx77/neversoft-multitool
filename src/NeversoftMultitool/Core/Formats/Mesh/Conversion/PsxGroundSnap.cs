using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Reseats a grounded TRG POWERUP pickup onto the terrain, reproducing the
///     engine's runtime ground-snap EXACTLY. Decompiled from the Spider-Man final
///     <c>CPowerUp</c> (SLUS_008.75, <c>tools/reverse-engineering/psx/psx_disasm.py</c>):
///     <list type="bullet">
///       <item>ctor @0x8001DDB0: a grounded pickup (<c>Flags &amp; 4</c>) sets
///       <c>mGroundY = Utils_GetGroundHeight(&amp;mOrgPos, above=512, below=8000)</c>
///       (@0x8005f524);</item>
///       <item>ctor @0x8001DCC4/DCD8: <c>mHoverHeight = 128</c> (world units);</item>
///       <item>DoPhysics @0x8001EB20: <c>mOrgPos.vy = mGroundY - (mHoverHeight &lt;&lt; 12)</c>.</item>
///     </list>
///     So the pickup's ORIGIN rests <c>hover</c> world units above the floor found
///     by searching <c>[origin-512, origin+8000]</c>. THPS pickups do NOT snap at
///     all: the matched THPS2 decomp's <c>CPowerUp::DoPhysics</c> touches
///     <c>mGroundY</c> only on the <c>mDropping</c> path (ctor <c>Flags &amp; 4</c>)
///     — a placed pickup renders at its authored <c>mOrgPos</c> plus a wobble
///     centered on it. Props (<c>CManipOb</c>) do NOT snap — never here.
///
///     All math is NATIVE engine space (+Y = down), before
///     <c>PsxMeshSemantics.ToGltfPosition</c>. World units are converted to
///     the file's model space by the translation divisor (our positions and the
///     terrain field are both divisor-scaled).
/// </summary>
internal static class PsxGroundSnap
{
    /// <summary>Spider-Man <c>CPowerUp::mHoverHeight</c> (world units), all builds.</summary>
    internal const float SpiderManHoverWorldUnits = 128f;

    // GetGroundHeight(&mOrgPos, above=512, below=8000): the search window, in world
    // units, around the pickup's origin.
    private const float SearchAboveWorldUnits = 512f;
    private const float SearchBelowWorldUnits = 8000f;

    /// <summary>
    ///     Returns <paramref name="nativePosition" /> (the pickup origin) with its Y
    ///     snapped to <c>groundY - hover</c>, or unchanged when no floor is found in
    ///     the engine's <c>[origin-512, origin+8000]</c> window (a pickup over a
    ///     hole stays put). <paramref name="hoverWorldUnits" /> and the window are
    ///     converted to model space by <paramref name="translationDivisor" />.
    /// </summary>
    internal static Vector3 SnapPickupToGround(
        PsxTerrainHeightField terrain,
        Vector3 nativePosition,
        float translationDivisor,
        float hoverWorldUnits)
    {
        var divisor = translationDivisor is > 0f and < float.PositiveInfinity ? translationDivisor : 1f;
        var above = SearchAboveWorldUnits / divisor;
        var below = SearchBelowWorldUnits / divisor;

        // The engine queries at &mOrgPos (the origin XZ), not the model footprint.
        if (!terrain.TryGetRestingFloor(
                nativePosition.X, nativePosition.Z, nativePosition.Y, above, below, out var groundY))
        {
            return nativePosition;
        }

        return nativePosition with { Y = groundY - hoverWorldUnits / divisor };
    }
}
