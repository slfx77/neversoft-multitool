using System.Numerics;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     One surface a probe ray passed through.
/// </summary>
internal readonly record struct ProbeHit(
    float Distance,
    Vector3 Point,
    int SubmeshIndex,
    int TriangleIndex,
    string? NodeName,
    string? MeshName,
    bool IsDoubleSided,
    bool IsBackFacing,
    int AlphaMode,
    bool HasTexture);

/// <summary>
///     Casts a ray through a loaded scene and reports every surface along it.
/// </summary>
/// <remarks>
///     <para>
///         Reporting <i>every</i> hit rather than the nearest is the point. Two surfaces a
///         fraction of a unit apart is what z-fighting looks like from the outside, and the
///         printed distances name the culprits directly — which beats inferring them from a
///         picture, or from a throwaway test harness written per investigation.
///     </para>
///     <para>
///         Brute force over triangles. The corpus worst case is a level of a few hundred
///         thousand triangles against a handful of rays, which is milliseconds; there is no
///         acceleration structure until something measures slow.
///     </para>
/// </remarks>
internal static class ViewProbe
{
    /// <summary>Rays closer than this to parallel with a triangle's plane never hit it.</summary>
    private const float ParallelEpsilon = 1e-8f;

    /// <summary>Hits at or behind the eye are not in front of the camera.</summary>
    private const float MinDistance = 1e-5f;

    /// <summary>
    ///     Direction of the ray through a given output pixel, in world space.
    /// </summary>
    /// <param name="pose">The camera.</param>
    /// <param name="pixelX">Pixel column; the pixel centre is used.</param>
    /// <param name="pixelY">Pixel row, measured downward from the top.</param>
    internal static Vector3 RayDirection(ViewPose pose, float pixelX, float pixelY)
    {
        var (right, up, forward) = pose.GetBasis();
        var focal = pose.FocalLength(pose.Height);

        var offsetX = pixelX + 0.5f - pose.Width * 0.5f;
        var offsetY = pose.Height * 0.5f - (pixelY + 0.5f);

        return Vector3.Normalize(right * offsetX + up * offsetY + forward * focal);
    }

    /// <summary>
    ///     Every triangle the ray passes through, nearest first.
    /// </summary>
    internal static List<ProbeHit> Cast(RenderScene scene, Vector3 origin, Vector3 direction)
    {
        var hits = new List<ProbeHit>();
        if (direction.LengthSquared() <= 0f)
            return hits;

        direction = Vector3.Normalize(direction);

        for (var si = 0; si < scene.Submeshes.Count; si++)
        {
            var submesh = scene.Submeshes[si];
            var positions = submesh.Positions;
            var indices = submesh.Triangles;

            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var i0 = indices[t] * 3;
                var i1 = indices[t + 1] * 3;
                var i2 = indices[t + 2] * 3;
                if (i0 + 2 >= positions.Length || i1 + 2 >= positions.Length ||
                    i2 + 2 >= positions.Length)
                    continue;

                var v0 = new Vector3(positions[i0], positions[i0 + 1], positions[i0 + 2]);
                var v1 = new Vector3(positions[i1], positions[i1 + 1], positions[i1 + 2]);
                var v2 = new Vector3(positions[i2], positions[i2 + 1], positions[i2 + 2]);

                if (!TryIntersect(origin, direction, v0, v1, v2, out var distance, out var backFacing))
                    continue;

                hits.Add(new ProbeHit(
                    distance,
                    origin + direction * distance,
                    si,
                    t / 3,
                    submesh.NodeName,
                    submesh.MeshName,
                    submesh.IsDoubleSided,
                    backFacing,
                    submesh.AlphaMode,
                    submesh.TextureData != null));
            }
        }

        hits.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return hits;
    }

    /// <summary>Möller-Trumbore ray/triangle intersection, accepting both facings.</summary>
    /// <remarks>
    ///     Back faces are reported rather than culled: standing inside a room, the walls
    ///     you care about face away from you, and a suppressed hit is a missed diagnosis.
    /// </remarks>
    private static bool TryIntersect(
        Vector3 origin, Vector3 direction,
        Vector3 v0, Vector3 v1, Vector3 v2,
        out float distance, out bool isBackFacing)
    {
        distance = 0f;
        isBackFacing = false;

        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var pvec = Vector3.Cross(direction, edge2);
        var det = Vector3.Dot(edge1, pvec);

        if (MathF.Abs(det) < ParallelEpsilon)
            return false;

        var invDet = 1f / det;
        var tvec = origin - v0;

        var u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f)
            return false;

        var qvec = Vector3.Cross(tvec, edge1);
        var v = Vector3.Dot(direction, qvec) * invDet;
        if (v < 0f || u + v > 1f)
            return false;

        var t = Vector3.Dot(edge2, qvec) * invDet;
        if (t < MinDistance)
            return false;

        distance = t;
        isBackFacing = det < 0f;
        return true;
    }
}
