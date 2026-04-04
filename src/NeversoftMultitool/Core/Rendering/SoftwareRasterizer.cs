using System.Numerics;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Scanline triangle rasterizer with per-pixel Z-buffer, normal-based lighting,
///     and vertex color support. Ported from Xbox360MemoryCarver's NifScanlineRasterizer,
///     stripped to essentials (no texture sampling, bump mapping, or alpha blending).
/// </summary>
internal static class SoftwareRasterizer
{
    // Lighting constants — soft fill for low-poly game models
    private const float SkyAmbient = 0.65f;
    private const float GroundAmbient = 0.45f;
    private const float LightIntensity = 0.40f;

    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.3f, 0.2f, 1.0f));
    private static readonly Vector3 HalfVec = Vector3.Normalize(LightDir + Vector3.UnitZ);
    private static readonly float HdotNegL = Vector3.Dot(HalfVec, -LightDir);

    /// <summary>
    ///     Rasterize a single filled triangle into the pixel/depth buffers.
    /// </summary>
    internal static void RasterizeTriangle(byte[] pixels, float[] depthBuffer,
        int width, int height, RenderTriangle tri,
        List<RenderSubmesh>? submeshes = null)
    {
        // Bounding box (clipped to image)
        var minPx = Math.Max(0, (int)MathF.Floor(MathF.Min(tri.Sx0, MathF.Min(tri.Sx1, tri.Sx2))));
        var maxPx = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(tri.Sx0, MathF.Max(tri.Sx1, tri.Sx2))));
        var minPy = Math.Max(0, (int)MathF.Floor(MathF.Min(tri.Sy0, MathF.Min(tri.Sy1, tri.Sy2))));
        var maxPy = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(tri.Sy0, MathF.Max(tri.Sy1, tri.Sy2))));

        if (minPx > maxPx || minPy > maxPy)
            return;

        // Edge function denominator — sign indicates winding
        var denom = (tri.Sy1 - tri.Sy2) * (tri.Sx0 - tri.Sx2) +
                    (tri.Sx2 - tri.Sx1) * (tri.Sy0 - tri.Sy2);
        if (MathF.Abs(denom) < 0.0001f)
            return; // Degenerate

        var isBackFacing = denom > 0;
        if (isBackFacing && !tri.IsDoubleSided)
            return;

        var invDenom = 1f / denom;

        for (var py = minPy; py <= maxPy; py++)
        {
            for (var px = minPx; px <= maxPx; px++)
            {
                var cx = px + 0.5f;
                var cy = py + 0.5f;

                // Barycentric coordinates
                var w0 = ((tri.Sy1 - tri.Sy2) * (cx - tri.Sx2) +
                          (tri.Sx2 - tri.Sx1) * (cy - tri.Sy2)) * invDenom;
                var w1 = ((tri.Sy2 - tri.Sy0) * (cx - tri.Sx2) +
                          (tri.Sx0 - tri.Sx2) * (cy - tri.Sy2)) * invDenom;
                var w2 = 1f - w0 - w1;

                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;

                // Depth test
                var z = tri.Z0 * w0 + tri.Z1 * w1 + tri.Z2 * w2;
                var idx = py * width + px;
                if (z <= depthBuffer[idx])
                    continue;

                var prevDepth = depthBuffer[idx];
                depthBuffer[idx] = z;

                // Compute shade
                float shade;
                if (tri.HasNormals)
                {
                    var nx = tri.Nx0 * w0 + tri.Nx1 * w1 + tri.Nx2 * w2;
                    var ny = tri.Ny0 * w0 + tri.Ny1 * w1 + tri.Ny2 * w2;
                    var nz = tri.Nz0 * w0 + tri.Nz1 * w1 + tri.Nz2 * w2;

                    if (isBackFacing)
                    {
                        nx = -nx;
                        ny = -ny;
                        nz = -nz;
                    }

                    var nLen = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nLen > 0.001f)
                    {
                        nx /= nLen;
                        ny /= nLen;
                        nz /= nLen;
                    }

                    shade = ComputeShade(nx, ny, nz, tri.IsDoubleSided);
                }
                else
                {
                    shade = tri.FlatShade;
                }

                // Sample texture if available
                float texR = 255f, texG = 255f, texB = 255f, texA = 255f;
                var hasTexture = false;
                RenderSubmesh? submesh = null;

                if (submeshes != null && tri.SubmeshIndex < submeshes.Count)
                    submesh = submeshes[tri.SubmeshIndex];

                if (submesh?.TextureData != null)
                {
                    hasTexture = true;
                    var u = tri.U0 * w0 + tri.U1 * w1 + tri.U2 * w2;
                    var v = tri.V0 * w0 + tri.V1 * w1 + tri.V2 * w2;

                    // REPEAT wrap
                    u = u - MathF.Floor(u);
                    v = v - MathF.Floor(v);

                    // Nearest-neighbor sample
                    var tx = Math.Clamp((int)(u * submesh.TextureWidth), 0, submesh.TextureWidth - 1);
                    var ty = Math.Clamp((int)(v * submesh.TextureHeight), 0, submesh.TextureHeight - 1);
                    var tIdx = (ty * submesh.TextureWidth + tx) * 4;
                    texR = submesh.TextureData[tIdx];
                    texG = submesh.TextureData[tIdx + 1];
                    texB = submesh.TextureData[tIdx + 2];
                    texA = submesh.TextureData[tIdx + 3];
                }

                // MASK alpha test (before depth write)
                if (submesh is { AlphaMode: 1 } && texA < submesh.AlphaCutoff * 255f)
                {
                    depthBuffer[idx] = prevDepth; // restore depth
                    continue;
                }

                // Base color: texture * vertex color * base color factor * shade
                float fr, fg, fb;
                if (hasTexture)
                {
                    var bcR = submesh!.BaseColorR;
                    var bcG = submesh.BaseColorG;
                    var bcB = submesh.BaseColorB;

                    if (tri.HasVertexColors)
                    {
                        var cr = (tri.R0 * w0 + tri.R1 * w1 + tri.R2 * w2) / 255f;
                        var cg = (tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2) / 255f;
                        var cb = (tri.B0 * w0 + tri.B1 * w1 + tri.B2 * w2) / 255f;
                        fr = texR * cr * bcR * shade;
                        fg = texG * cg * bcG * shade;
                        fb = texB * cb * bcB * shade;
                    }
                    else
                    {
                        fr = texR * bcR * shade;
                        fg = texG * bcG * shade;
                        fb = texB * bcB * shade;
                    }
                }
                else if (tri.HasVertexColors)
                {
                    var cr = tri.R0 * w0 + tri.R1 * w1 + tri.R2 * w2;
                    var cg = tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2;
                    var cb = tri.B0 * w0 + tri.B1 * w1 + tri.B2 * w2;
                    fr = cr * shade;
                    fg = cg * shade;
                    fb = cb * shade;
                }
                else
                {
                    fr = 200f * shade;
                    fg = 200f * shade;
                    fb = 200f * shade;
                }

                var pIdx = idx * 4;
                pixels[pIdx + 0] = (byte)Math.Clamp((int)fr, 0, 255);
                pixels[pIdx + 1] = (byte)Math.Clamp((int)fg, 0, 255);
                pixels[pIdx + 2] = (byte)Math.Clamp((int)fb, 0, 255);
                pixels[pIdx + 3] = 255;
            }
        }
    }

    /// <summary>
    ///     Hemisphere ambient + directional + Fresnel rim light shading.
    ///     Ported from NifSpriteRenderer.ComputeShade.
    /// </summary>
    internal static float ComputeShade(float nx, float ny, float nz, bool twoSided = false)
    {
        // Hemisphere ambient: blend sky/ground based on vertical normal component
        var hemiBlend = -ny * 0.5f + 0.5f;
        var ambient = GroundAmbient + (SkyAmbient - GroundAmbient) * hemiBlend;

        // Diffuse with wrap lighting
        const float wrap = 0.25f;
        var rawNdotL = nx * LightDir.X + ny * LightDir.Y + nz * LightDir.Z;
        if (twoSided) rawNdotL = MathF.Abs(rawNdotL);
        var ndotL = MathF.Max(0, (rawNdotL + wrap) / (1f + wrap));

        // Fresnel rim light
        var ndotH = MathF.Max(0, nx * HalfVec.X + ny * HalfVec.Y + nz * HalfVec.Z);
        var oneMinusNdotH = 1f - ndotH;
        var fresnel = MathF.Max(0, HdotNegL) * oneMinusNdotH * oneMinusNdotH;

        var directional = MathF.Min(LightIntensity * ndotL + LightIntensity * fresnel * 0.5f, 1f);
        return Math.Clamp(directional + ambient, 0f, 1f);
    }

    /// <summary>
    ///     Downsample a supersampled RGBA buffer by a given factor using box filter.
    ///     Ported from NifSpriteRenderer.Downsample.
    /// </summary>
    internal static byte[] Downsample(byte[] src, int srcW, int srcH, int factor)
    {
        var dstW = srcW / factor;
        var dstH = srcH / factor;
        var dst = new byte[dstW * dstH * 4];
        var invArea = 1f / (factor * factor);

        for (var dy = 0; dy < dstH; dy++)
        {
            for (var dx = 0; dx < dstW; dx++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                var sx0 = dx * factor;
                var sy0 = dy * factor;

                for (var sy = sy0; sy < sy0 + factor; sy++)
                {
                    var rowOff = sy * srcW * 4 + sx0 * 4;
                    for (var sx = 0; sx < factor; sx++)
                    {
                        var i = rowOff + sx * 4;
                        r += src[i];
                        g += src[i + 1];
                        b += src[i + 2];
                        a += src[i + 3];
                    }
                }

                var dIdx = (dy * dstW + dx) * 4;
                dst[dIdx] = (byte)(r * invArea);
                dst[dIdx + 1] = (byte)(g * invArea);
                dst[dIdx + 2] = (byte)(b * invArea);
                dst[dIdx + 3] = (byte)(a * invArea);
            }
        }

        return dst;
    }
}
