using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     The N64 half of the PS1 writer's blanket semi-transparent lift: every
///     semi-transparent face rises off the surface it overlays, so a glass
///     sheet or a painted street line stops z-fighting the wall or road it was
///     authored exactly coplanar with.
///     <para>
///         The ports ship the PS1's authored geometry unchanged, so the decals
///         arrive exactly coincident. Neither console resolved that with
///         geometry: the PS1 sequenced them through the ordering table, and the
///         RDP has a dedicated DECAL render mode that draws where depth is
///         EQUAL. A depth-tested glTF viewer has neither, which is why the
///         export has to separate them.
///     </para>
///     <para>
///         This is deliberately the SAME division of labour the PS1 path uses:
///         opaque coplanar decals split into draw-order overlay meshes
///         (<see cref="N64CoplanarOverlayDetector" />) while semi-transparent
///         faces lift geometrically. It is also why the detector skips any pair
///         with a semi-transparent member — resolving one face twice would push
///         it a full step further than intended.
///     </para>
///     <para>
///         Direction is POSITION-AVERAGED over the file's semi-transparent
///         faces rather than per-face, and that is load-bearing: neighbouring
///         faces of a curved connected surface (Spider-Man's all-semi-transparent
///         webdome) have different normals, so lifting each along its own would
///         tear the surface open at every shared edge. Averaging moves shared
///         corners together and reduces to the face normal on a flat decal. An
///         average that cancels to zero — two opposed sheets sharing an edge —
///         falls back to the face's own direction.
///     </para>
/// </summary>
internal sealed class N64SemiTransparentLift
{
    /// <summary>
    ///     Position key resolution, in export units. Two corners merge only if
    ///     they are within 1/64 of a unit, far below the authoring grid step
    ///     (one raw N64 unit, never less than ~0.44 export units on a level),
    ///     so only genuinely coincident corners are averaged together.
    /// </summary>
    private const float PositionQuantum = 64f;

    private readonly Dictionary<(int X, int Y, int Z), Vector3> _directions;
    private readonly float _magnitude;

    private N64SemiTransparentLift(Dictionary<(int X, int Y, int Z), Vector3> directions, float magnitude)
    {
        _directions = directions;
        _magnitude = magnitude;
    }

    /// <summary>
    ///     Builds the direction map from the writer's own candidate list — the
    ///     same triangles, in the same export space, with the same invisible-face
    ///     gate — or null when the model has no semi-transparent geometry at all
    ///     (most characters), in which case nothing lifts.
    /// </summary>
    internal static N64SemiTransparentLift? Build(
        IReadOnlyList<N64OverlayCandidateSource> candidates, float magnitude)
    {
        Dictionary<(int X, int Y, int Z), Vector3>? sums = null;
        foreach (var candidate in candidates)
        {
            if ((candidate.FaceFlags & PsxFaceFlags.SemiTransparent) == 0)
                continue;

            var points = candidate.Points;
            // Same winding the writer emits, so the normal points outward.
            var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
            var lengthSquared = normal.LengthSquared();
            if (lengthSquared <= 1e-12f)
                continue;

            normal /= MathF.Sqrt(lengthSquared);
            sums ??= [];
            foreach (var point in points)
            {
                var key = Quantize(point);
                sums[key] = sums.TryGetValue(key, out var sum) ? sum + normal : normal;
            }
        }

        if (sums == null)
            return null;

        var directions = new Dictionary<(int X, int Y, int Z), Vector3>(sums.Count);
        foreach (var (key, sum) in sums)
        {
            var lengthSquared = sum.LengthSquared();
            if (lengthSquared > 1e-12f)
                directions[key] = sum / MathF.Sqrt(lengthSquared);
        }

        return new N64SemiTransparentLift(directions, magnitude);
    }

    /// <summary>
    ///     The translation to add to one corner of a semi-transparent face.
    ///     <paramref name="faceDirection" /> is the fallback for a corner no
    ///     other semi-transparent face shares, or one whose average cancelled.
    /// </summary>
    internal Vector3 OffsetFor(Vector3 position, Vector3 faceDirection)
    {
        var direction = _directions.TryGetValue(Quantize(position), out var averaged)
            ? averaged
            : faceDirection;
        return direction * _magnitude;
    }

    private static (int X, int Y, int Z) Quantize(Vector3 position)
    {
        return ((int)MathF.Round(position.X * PositionQuantum),
            (int)MathF.Round(position.Y * PositionQuantum),
            (int)MathF.Round(position.Z * PositionQuantum));
    }
}
