using System.Numerics;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Projects world-space triangles through an explicit <see cref="ViewPose" /> camera,
///     clipping against the near plane on the way.
/// </summary>
/// <remarks>
///     <para>
///         This is the alternative to <c>GlbRenderer.ApplyViewRotation</c>, which is
///         orthographic and auto-framed. It exists so a pose copied out of the interactive
///         viewer can be replayed headlessly.
///     </para>
///     <para>
///         <b>View-space convention</b> matches the orthographic path exactly, because both
///         feed the same rasterizer and the same <see cref="SoftwareRasterizer.ComputeShade" />:
///         X is screen-right, Y is screen-<i>down</i>, Z points back toward the viewer.
///         Depth is written as <c>1 / distance</c>, which is larger when nearer — precisely
///         what <c>RasterizeTriangle</c>'s <c>z &gt; depthBuffer</c> test wants — and is also
///         the only quantity that interpolates linearly in screen space under perspective,
///         so the depth buffer stays exact.
///     </para>
///     <para>
///         Colour, UV and normal interpolation stay affine in screen space (the rasterizer's
///         plain barycentric weights). Under perspective that is the classic PS1 texture
///         warp — consistent with the viewer's deliberate affine-Gouraud emulation, and
///         irrelevant to the depth ordering this path exists to diagnose. It is not a
///         defect to be "fixed".
///     </para>
/// </remarks>
internal static class PerspectiveProjector
{
    /// <summary>
    ///     Transform, near-clip and project every triangle into supersampled screen space.
    /// </summary>
    /// <param name="worldTriangles">Triangles carrying world positions in Sx/Sy/Z.</param>
    /// <param name="pose">The camera to look through.</param>
    /// <param name="pixelWidth">Supersampled canvas width.</param>
    /// <param name="pixelHeight">Supersampled canvas height.</param>
    /// <param name="nearPlane">Distance in front of the eye below which geometry is cut.</param>
    internal static List<RenderTriangle> Project(
        List<RenderTriangle> worldTriangles,
        ViewPose pose,
        int pixelWidth,
        int pixelHeight,
        float nearPlane)
    {
        var (right, up, forward) = pose.GetBasis();
        var eye = pose.Eye;
        var focal = pose.FocalLength(pixelHeight);
        var centreX = pixelWidth * 0.5f;
        var centreY = pixelHeight * 0.5f;

        // A triangle clipped by one plane yields at most four corners, hence at most two
        // output triangles.
        var projected = new List<RenderTriangle>(worldTriangles.Count);
        Span<ClipVertex> corners = stackalloc ClipVertex[3];
        Span<ClipVertex> clipped = stackalloc ClipVertex[4];

        foreach (var tri in worldTriangles)
        {
            corners[0] = ToViewSpace(tri, 0, eye, right, up, forward);
            corners[1] = ToViewSpace(tri, 1, eye, right, up, forward);
            corners[2] = ToViewSpace(tri, 2, eye, right, up, forward);

            var count = ClipToNearPlane(corners, nearPlane, clipped);
            if (count < 3)
                continue;

            // Fan from corner 0 so the winding of every emitted triangle matches the
            // input; the rasterizer decides backfacing from screen winding.
            for (var i = 1; i + 1 < count; i++)
            {
                projected.Add(BuildTriangle(
                    tri, clipped[0], clipped[i], clipped[i + 1],
                    focal, centreX, centreY));
            }
        }

        return projected;
    }

    /// <summary>
    ///     Clip a triangle against <c>depth &gt;= near</c> using Sutherland-Hodgman.
    /// </summary>
    /// <returns>0 (fully behind), 3 or 4 output corners.</returns>
    /// <remarks>
    ///     Mandatory rather than an optimisation: in a first-person level view there is
    ///     geometry behind the eye everywhere, and a corner at depth &lt;= 0 projects
    ///     mirrored through the origin and smears across the whole frame.
    /// </remarks>
    internal static int ClipToNearPlane(
        ReadOnlySpan<ClipVertex> corners, float nearPlane, Span<ClipVertex> output)
    {
        var count = 0;

        for (var i = 0; i < corners.Length; i++)
        {
            var current = corners[i];
            var next = corners[(i + 1) % corners.Length];

            var currentInside = current.Depth >= nearPlane;
            var nextInside = next.Depth >= nearPlane;

            if (currentInside)
                output[count++] = current;

            if (currentInside == nextInside)
                continue;

            // The two depths straddle the plane, so their difference is non-zero.
            var t = (nearPlane - current.Depth) / (next.Depth - current.Depth);
            output[count++] = ClipVertex.Lerp(current, next, t);
        }

        return count;
    }

    private static ClipVertex ToViewSpace(
        in RenderTriangle tri, int corner,
        Vector3 eye, Vector3 right, Vector3 up, Vector3 forward)
    {
        var world = corner switch
        {
            0 => new Vector3(tri.Sx0, tri.Sy0, tri.Z0),
            1 => new Vector3(tri.Sx1, tri.Sy1, tri.Z1),
            _ => new Vector3(tri.Sx2, tri.Sy2, tri.Z2)
        };

        var offset = world - eye;

        var vertex = new ClipVertex
        {
            X = Vector3.Dot(offset, right),
            Y = Vector3.Dot(offset, up),
            Depth = Vector3.Dot(offset, forward)
        };

        if (tri.HasNormals)
        {
            var normal = corner switch
            {
                0 => new Vector3(tri.Nx0, tri.Ny0, tri.Nz0),
                1 => new Vector3(tri.Nx1, tri.Ny1, tri.Nz1),
                _ => new Vector3(tri.Nx2, tri.Ny2, tri.Nz2)
            };

            // Screen-right / screen-down / toward-viewer, matching the orthographic path.
            vertex.Nx = Vector3.Dot(normal, right);
            vertex.Ny = -Vector3.Dot(normal, up);
            vertex.Nz = -Vector3.Dot(normal, forward);
        }

        switch (corner)
        {
            case 0:
                vertex.R = tri.R0; vertex.G = tri.G0; vertex.B = tri.B0; vertex.A = tri.A0;
                vertex.U = tri.U0; vertex.V = tri.V0;
                break;
            case 1:
                vertex.R = tri.R1; vertex.G = tri.G1; vertex.B = tri.B1; vertex.A = tri.A1;
                vertex.U = tri.U1; vertex.V = tri.V1;
                break;
            default:
                vertex.R = tri.R2; vertex.G = tri.G2; vertex.B = tri.B2; vertex.A = tri.A2;
                vertex.U = tri.U2; vertex.V = tri.V2;
                break;
        }

        return vertex;
    }

    private static RenderTriangle BuildTriangle(
        in RenderTriangle source,
        in ClipVertex a, in ClipVertex b, in ClipVertex c,
        float focal, float centreX, float centreY)
    {
        var tri = new RenderTriangle
        {
            SubmeshIndex = source.SubmeshIndex,
            IsDoubleSided = source.IsDoubleSided,
            HasNormals = source.HasNormals,
            HasVertexColors = source.HasVertexColors,

            Sx0 = centreX + focal * a.X / a.Depth,
            Sy0 = centreY - focal * a.Y / a.Depth,
            Z0 = 1f / a.Depth,
            Sx1 = centreX + focal * b.X / b.Depth,
            Sy1 = centreY - focal * b.Y / b.Depth,
            Z1 = 1f / b.Depth,
            Sx2 = centreX + focal * c.X / c.Depth,
            Sy2 = centreY - focal * c.Y / c.Depth,
            Z2 = 1f / c.Depth,

            Nx0 = a.Nx, Ny0 = a.Ny, Nz0 = a.Nz,
            Nx1 = b.Nx, Ny1 = b.Ny, Nz1 = b.Nz,
            Nx2 = c.Nx, Ny2 = c.Ny, Nz2 = c.Nz,

            R0 = a.R, G0 = a.G, B0 = a.B, A0 = a.A,
            R1 = b.R, G1 = b.G, B1 = b.B, A1 = b.A,
            R2 = c.R, G2 = c.G, B2 = c.B, A2 = c.A,

            U0 = a.U, V0 = a.V,
            U1 = b.U, V1 = b.V,
            U2 = c.U, V2 = c.V
        };

        if (!tri.HasNormals)
        {
            // Flat-shade from the view-space face normal, in the same
            // right/down/toward-viewer frame the orthographic path uses.
            var e1 = new Vector3(b.X - a.X, -(b.Y - a.Y), -(b.Depth - a.Depth));
            var e2 = new Vector3(c.X - a.X, -(c.Y - a.Y), -(c.Depth - a.Depth));
            var normal = Vector3.Cross(e1, e2);
            var length = normal.Length();
            if (length > 0.0001f)
                normal /= length;

            tri.FlatShade = SoftwareRasterizer.ComputeShade(
                normal.X, normal.Y, normal.Z, source.IsDoubleSided);
        }

        return tri;
    }

    /// <summary>A triangle corner in view space, carrying everything the rasterizer needs.</summary>
    internal struct ClipVertex
    {
        /// <summary>Screen-right offset from the eye.</summary>
        public float X;

        /// <summary>Screen-up offset from the eye.</summary>
        public float Y;

        /// <summary>Distance in front of the eye along the view direction.</summary>
        public float Depth;

        public float Nx, Ny, Nz;
        public float R, G, B, A;
        public float U, V;

        internal static ClipVertex Lerp(in ClipVertex a, in ClipVertex b, float t)
        {
            return new ClipVertex
            {
                X = a.X + (b.X - a.X) * t,
                Y = a.Y + (b.Y - a.Y) * t,
                Depth = a.Depth + (b.Depth - a.Depth) * t,
                Nx = a.Nx + (b.Nx - a.Nx) * t,
                Ny = a.Ny + (b.Ny - a.Ny) * t,
                Nz = a.Nz + (b.Nz - a.Nz) * t,
                R = a.R + (b.R - a.R) * t,
                G = a.G + (b.G - a.G) * t,
                B = a.B + (b.B - a.B) * t,
                A = a.A + (b.A - a.A) * t,
                U = a.U + (b.U - a.U) * t,
                V = a.V + (b.V - a.V) * t
            };
        }
    }
}
