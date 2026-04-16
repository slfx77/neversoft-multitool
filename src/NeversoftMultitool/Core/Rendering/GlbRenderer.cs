using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Headless GLB-to-PNG renderer using CPU software rasterization.
///     Ported from Xbox360MemoryCarver's NifSpriteRenderer with orthographic projection,
///     azimuth/elevation camera, SSAA 2x, and hemisphere + directional lighting.
/// </summary>
public static class GlbRenderer
{
    private const int SsaaFactor = 2;

    // Transparent background
    private const byte BgR = 0, BgG = 0, BgB = 0, BgA = 0;

    /// <summary>
    ///     Render a GLB file to a PNG image file.
    /// </summary>
    /// <param name="glbPath">Path to the input .glb file.</param>
    /// <param name="pngPath">Path to the output .png file.</param>
    /// <param name="longEdge">Long edge of the output image in pixels.</param>
    /// <param name="azimuthDeg">Camera azimuth in degrees (0=front, 90=right side).</param>
    /// <param name="elevationDeg">Camera elevation in degrees above horizontal.</param>
    public static void RenderToFile(string glbPath, string pngPath,
        int longEdge = 512,
        float azimuthDeg = -90f, float elevationDeg = 10f)
    {
        using var image = RenderToImage(glbPath, longEdge, azimuthDeg, elevationDeg);
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        image.SaveAsPng(pngPath);
    }

    /// <summary>
    ///     Render a GLB file to an in-memory image.
    ///     Output dimensions are determined by the model's projected aspect ratio,
    ///     with the longer edge set to <paramref name="longEdge" /> pixels.
    /// </summary>
    public static Image<Rgba32> RenderToImage(string glbPath,
        int longEdge = 512,
        float azimuthDeg = -90f, float elevationDeg = 10f)
    {
        var scene = GlbModelLoader.Load(glbPath);
        return RenderScene(scene, longEdge, azimuthDeg, elevationDeg);
    }

    internal static Image<Rgba32> RenderScene(RenderScene scene,
        int longEdge = 512,
        float azimuthDeg = -90f, float elevationDeg = 10f,
        int fixedWidth = 0, int fixedHeight = 0,
        float referenceWidth = 0f, float referenceHeight = 0f)
    {
        if (!scene.HasGeometry)
            return CreateBackground(
                fixedWidth > 0 ? fixedWidth : longEdge,
                fixedHeight > 0 ? fixedHeight : longEdge);

        SmoothNormals(scene);

        // Collect all triangles from the scene
        var triangles = CollectTriangles(scene);
        if (triangles.Count == 0)
            return CreateBackground(longEdge, longEdge);

        // Apply view rotation (azimuth/elevation camera)
        var (projMinX, projMinY, projWidth, projHeight) =
            ApplyViewRotation(triangles, azimuthDeg, elevationDeg);

        if (projWidth < 0.001f && projHeight < 0.001f)
            return CreateBackground(longEdge, longEdge);

        // Compute output dimensions from model aspect ratio (or use fixed size)
        int width, height;
        if (fixedWidth > 0 && fixedHeight > 0)
        {
            width = fixedWidth;
            height = fixedHeight;
        }
        else if (projWidth >= projHeight)
        {
            width = longEdge;
            height = Math.Max(1, (int)(longEdge * projHeight / projWidth));
        }
        else
        {
            height = longEdge;
            width = Math.Max(1, (int)(longEdge * projWidth / projHeight));
        }

        // Compute SSAA dimensions
        var ssWidth = width * SsaaFactor;
        var ssHeight = height * SsaaFactor;

        // Compute effective pixels-per-unit to fit model with 10% margin.
        // When reference bounds are supplied (animation pre-pass), scale by those
        // so every frame uses a consistent units-per-pixel and centers correctly.
        var scaleW = referenceWidth > 0 ? referenceWidth : projWidth;
        var scaleH = referenceHeight > 0 ? referenceHeight : projHeight;
        var marginFactor = 1.0f;
        var effPpuX = ssWidth * marginFactor / Math.Max(scaleW, 0.001f);
        var effPpuY = ssHeight * marginFactor / Math.Max(scaleH, 0.001f);
        var effPpu = Math.Min(effPpuX, effPpuY);

        // Center model in canvas based on its current frame's bounds
        var offsetX = (ssWidth - projWidth * effPpu) / 2f - projMinX * effPpu;
        var offsetY = (ssHeight - projHeight * effPpu) / 2f - projMinY * effPpu;

        // Project triangles to screen space
        for (var i = 0; i < triangles.Count; i++)
        {
            var tri = triangles[i];
            tri.Sx0 = tri.Sx0 * effPpu + offsetX;
            tri.Sy0 = tri.Sy0 * effPpu + offsetY;
            tri.Sx1 = tri.Sx1 * effPpu + offsetX;
            tri.Sy1 = tri.Sy1 * effPpu + offsetY;
            tri.Sx2 = tri.Sx2 * effPpu + offsetX;
            tri.Sy2 = tri.Sy2 * effPpu + offsetY;
            triangles[i] = tri;
        }

        // Allocate SSAA buffers (zero-initialized = transparent background)
        var ssPixels = new byte[ssWidth * ssHeight * 4];
        var depthBuffer = new float[ssWidth * ssHeight];
        Array.Fill(depthBuffer, float.MinValue);

        // Rasterize all triangles
        var submeshes = scene.Submeshes;
        foreach (var tri in triangles)
            SoftwareRasterizer.RasterizeTriangle(ssPixels, depthBuffer, ssWidth, ssHeight, tri, submeshes);

        // Downsample SSAA → final resolution
        var pixels = SoftwareRasterizer.Downsample(ssPixels, ssWidth, ssHeight, SsaaFactor);

        // Convert to ImageSharp image
        return PixelsToImage(pixels, width, height);
    }

    internal static (float MinX, float MinY, float Width, float Height) ComputeProjectedBounds(
        RenderScene scene, float azimuthDeg, float elevationDeg)
    {
        if (!scene.HasGeometry)
            return (0, 0, 0, 0);

        SmoothNormals(scene);
        var triangles = CollectTriangles(scene);
        return ApplyViewRotation(triangles, azimuthDeg, elevationDeg);
    }

    /// <summary>
    ///     Average normals at coincident vertex positions across all submeshes
    ///     to produce smooth shading, even when exporters emit split vertices.
    /// </summary>
    private static void SmoothNormals(RenderScene scene)
    {
        // Build map: quantized position → list of (submesh index, vertex index)
        var posMap = new Dictionary<(int, int, int), List<(int si, int vi)>>();

        for (var si = 0; si < scene.Submeshes.Count; si++)
        {
            var sub = scene.Submeshes[si];
            if (sub.Normals == null) continue;

            var pos = sub.Positions;
            for (var vi = 0; vi < sub.VertexCount; vi++)
            {
                // Quantize to ~0.001 precision to handle float rounding
                var key = (
                    (int)MathF.Round(pos[vi * 3] * 1024f),
                    (int)MathF.Round(pos[vi * 3 + 1] * 1024f),
                    (int)MathF.Round(pos[vi * 3 + 2] * 1024f)
                );

                if (!posMap.TryGetValue(key, out var list))
                {
                    list = [];
                    posMap[key] = list;
                }

                list.Add((si, vi));
            }
        }

        // Average normals at shared positions
        foreach (var group in posMap.Values)
        {
            if (group.Count <= 1) continue;

            // Sum all normals at this position
            float nx = 0, ny = 0, nz = 0;
            foreach (var (si, vi) in group)
            {
                var nrm = scene.Submeshes[si].Normals!;
                nx += nrm[vi * 3];
                ny += nrm[vi * 3 + 1];
                nz += nrm[vi * 3 + 2];
            }

            var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0.001f)
            {
                nx /= len;
                ny /= len;
                nz /= len;
            }

            // Write averaged normal back
            foreach (var (si, vi) in group)
            {
                var nrm = scene.Submeshes[si].Normals!;
                nrm[vi * 3] = nx;
                nrm[vi * 3 + 1] = ny;
                nrm[vi * 3 + 2] = nz;
            }
        }
    }

    private static List<RenderTriangle> CollectTriangles(RenderScene scene)
    {
        var list = new List<RenderTriangle>();

        for (var si = 0; si < scene.Submeshes.Count; si++)
        {
            var submesh = scene.Submeshes[si];
            var pos = submesh.Positions;
            var nrm = submesh.Normals;
            var vcol = submesh.VertexColors;
            var uv = submesh.TexCoords;
            var tris = submesh.Triangles;

            for (var t = 0; t < tris.Length; t += 3)
            {
                var i0 = tris[t];
                var i1 = tris[t + 1];
                var i2 = tris[t + 2];

                var p0 = i0 * 3;
                var p1 = i1 * 3;
                var p2 = i2 * 3;

                if (p0 + 2 >= pos.Length || p1 + 2 >= pos.Length || p2 + 2 >= pos.Length)
                    continue;

                var tri = new RenderTriangle
                {
                    // Store world positions in Sx/Sy (will be projected later)
                    Sx0 = pos[p0], Sy0 = pos[p0 + 1], Z0 = pos[p0 + 2],
                    Sx1 = pos[p1], Sy1 = pos[p1 + 1], Z1 = pos[p1 + 2],
                    Sx2 = pos[p2], Sy2 = pos[p2 + 1], Z2 = pos[p2 + 2],
                    IsDoubleSided = submesh.IsDoubleSided,
                    SubmeshIndex = si
                };

                if (uv != null)
                {
                    var uv0 = i0 * 2;
                    var uv1 = i1 * 2;
                    var uv2 = i2 * 2;
                    if (uv0 + 1 < uv.Length && uv1 + 1 < uv.Length && uv2 + 1 < uv.Length)
                    {
                        tri.U0 = uv[uv0];
                        tri.V0 = uv[uv0 + 1];
                        tri.U1 = uv[uv1];
                        tri.V1 = uv[uv1 + 1];
                        tri.U2 = uv[uv2];
                        tri.V2 = uv[uv2 + 1];
                    }
                }

                if (nrm != null)
                {
                    var n0 = i0 * 3;
                    var n1 = i1 * 3;
                    var n2 = i2 * 3;
                    if (n0 + 2 < nrm.Length && n1 + 2 < nrm.Length && n2 + 2 < nrm.Length)
                    {
                        tri.Nx0 = nrm[n0];
                        tri.Ny0 = nrm[n0 + 1];
                        tri.Nz0 = nrm[n0 + 2];
                        tri.Nx1 = nrm[n1];
                        tri.Ny1 = nrm[n1 + 1];
                        tri.Nz1 = nrm[n1 + 2];
                        tri.Nx2 = nrm[n2];
                        tri.Ny2 = nrm[n2 + 1];
                        tri.Nz2 = nrm[n2 + 2];
                        tri.HasNormals = true;
                    }
                }

                if (vcol != null)
                {
                    var c0 = i0 * 4;
                    var c1 = i1 * 4;
                    var c2 = i2 * 4;
                    if (c0 + 3 < vcol.Length && c1 + 3 < vcol.Length && c2 + 3 < vcol.Length)
                    {
                        tri.R0 = vcol[c0];
                        tri.G0 = vcol[c0 + 1];
                        tri.B0 = vcol[c0 + 2];
                        tri.A0 = vcol[c0 + 3];
                        tri.R1 = vcol[c1];
                        tri.G1 = vcol[c1 + 1];
                        tri.B1 = vcol[c1 + 2];
                        tri.A1 = vcol[c1 + 3];
                        tri.R2 = vcol[c2];
                        tri.G2 = vcol[c2 + 1];
                        tri.B2 = vcol[c2 + 2];
                        tri.A2 = vcol[c2 + 3];
                        tri.HasVertexColors = true;
                    }
                }

                if (!tri.HasNormals)
                {
                    // Compute flat normal for shading
                    var ex1 = tri.Sx1 - tri.Sx0;
                    var ey1 = tri.Sy1 - tri.Sy0;
                    var ez1 = tri.Z1 - tri.Z0;
                    var ex2 = tri.Sx2 - tri.Sx0;
                    var ey2 = tri.Sy2 - tri.Sy0;
                    var ez2 = tri.Z2 - tri.Z0;
                    var nx = ey1 * ez2 - ez1 * ey2;
                    var ny = ez1 * ex2 - ex1 * ez2;
                    var nz = ex1 * ey2 - ey1 * ex2;
                    var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 0.0001f)
                    {
                        nx /= len;
                        ny /= len;
                        nz /= len;
                    }

                    tri.FlatShade = SoftwareRasterizer.ComputeShade(nx, ny, nz, submesh.IsDoubleSided);
                }

                list.Add(tri);
            }
        }

        return list;
    }

    /// <summary>
    ///     Apply azimuth/elevation view rotation to all triangles (in-place).
    ///     Returns the projected 2D bounds for canvas sizing.
    ///     Ported from NifSpriteRenderer.ApplyViewRotation.
    /// </summary>
    private static (float MinX, float MinY, float Width, float Height) ApplyViewRotation(
        List<RenderTriangle> triangles, float azimuthDeg, float elevationDeg)
    {
        var alpha = azimuthDeg * MathF.PI / 180f;
        var theta = elevationDeg * MathF.PI / 180f;

        var ca = MathF.Cos(alpha);
        var sa = MathF.Sin(alpha);
        var ct = MathF.Cos(theta);
        var st = MathF.Sin(theta);

        // View basis vectors (rows of rotation matrix)
        float r0 = -sa, r1 = ca, r2 = 0;
        float u0 = st * ca, u1 = st * sa, u2 = -ct;
        float f0 = ca * ct, f1 = sa * ct, f2 = st;

        var projMinX = float.MaxValue;
        var projMinY = float.MaxValue;
        var projMaxX = float.MinValue;
        var projMaxY = float.MinValue;

        for (var i = 0; i < triangles.Count; i++)
        {
            var tri = triangles[i];

            // Convert from glTF Y-up to camera Z-up: (x, y, z) → (x, -z, y)
            SwapYUp(ref tri.Sy0, ref tri.Z0);
            SwapYUp(ref tri.Sy1, ref tri.Z1);
            SwapYUp(ref tri.Sy2, ref tri.Z2);

            // Rotate vertex positions
            RotatePoint(ref tri.Sx0, ref tri.Sy0, ref tri.Z0, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            RotatePoint(ref tri.Sx1, ref tri.Sy1, ref tri.Z1, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            RotatePoint(ref tri.Sx2, ref tri.Sy2, ref tri.Z2, r0, r1, r2, u0, u1, u2, f0, f1, f2);

            // Rotate normals
            if (tri.HasNormals)
            {
                SwapYUp(ref tri.Ny0, ref tri.Nz0);
                SwapYUp(ref tri.Ny1, ref tri.Nz1);
                SwapYUp(ref tri.Ny2, ref tri.Nz2);
                RotatePoint(ref tri.Nx0, ref tri.Ny0, ref tri.Nz0, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Nx1, ref tri.Ny1, ref tri.Nz1, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Nx2, ref tri.Ny2, ref tri.Nz2, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            }
            else
            {
                // Recompute flat shade from rotated face normal
                var ex1 = tri.Sx1 - tri.Sx0;
                var ey1 = tri.Sy1 - tri.Sy0;
                var ez1 = tri.Z1 - tri.Z0;
                var ex2 = tri.Sx2 - tri.Sx0;
                var ey2 = tri.Sy2 - tri.Sy0;
                var ez2 = tri.Z2 - tri.Z0;
                var nx = ey1 * ez2 - ez1 * ey2;
                var ny = ez1 * ex2 - ex1 * ez2;
                var nz = ex1 * ey2 - ey1 * ex2;
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0.0001f)
                {
                    nx /= len;
                    ny /= len;
                    nz /= len;
                }

                tri.FlatShade = SoftwareRasterizer.ComputeShade(nx, ny, nz, tri.IsDoubleSided);
            }

            triangles[i] = tri;

            UpdateBounds(tri.Sx0, tri.Sy0, ref projMinX, ref projMinY, ref projMaxX, ref projMaxY);
            UpdateBounds(tri.Sx1, tri.Sy1, ref projMinX, ref projMinY, ref projMaxX, ref projMaxY);
            UpdateBounds(tri.Sx2, tri.Sy2, ref projMinX, ref projMinY, ref projMaxX, ref projMaxY);
        }

        return (projMinX, projMinY, projMaxX - projMinX, projMaxY - projMinY);

        static void UpdateBounds(float x, float y,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
    }

    private static void SwapYUp(ref float y, ref float z)
    {
        var oy = y;
        y = -z;
        z = oy;
    }

    private static void RotatePoint(ref float x, ref float y, ref float z,
        float r0, float r1, float r2,
        float u0, float u1, float u2,
        float f0, float f1, float f2)
    {
        var ox = x;
        var oy = y;
        var oz = z;
        x = r0 * ox + r1 * oy + r2 * oz;
        y = u0 * ox + u1 * oy + u2 * oz;
        z = f0 * ox + f1 * oy + f2 * oz;
    }

    private static Image<Rgba32> CreateBackground(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                row.Fill(new Rgba32(BgR, BgG, BgB, BgA));
            }
        });
        return image;
    }

    private static Image<Rgba32> PixelsToImage(byte[] pixels, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var idx = (y * width + x) * 4;
                    row[x] = new Rgba32(pixels[idx], pixels[idx + 1], pixels[idx + 2], pixels[idx + 3]);
                }
            }
        });
        return image;
    }
}
