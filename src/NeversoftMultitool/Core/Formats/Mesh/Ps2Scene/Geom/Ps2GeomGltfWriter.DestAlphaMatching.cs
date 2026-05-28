using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomGltfWriter
{
    private static bool IsAllTransparentWithUsefulRgb(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long usefulRgbPixels = 0;
        long maxChannelSum = 0;
        byte maxAlpha = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    maxAlpha = Math.Max(maxAlpha, p.A);
                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    maxChannelSum += maxChannel;
                    if (maxChannel >= 24)
                        usefulRgbPixels++;
                }
            }
        });

        if (maxAlpha > 8)
            return false;

        var averageMaxChannel = maxChannelSum / (double)totalPixels;
        return usefulRgbPixels * 10 >= totalPixels && averageMaxChannel >= 24.0;
    }

    private static bool TryComputeDestinationAlphaUvTransform(
        Ps2GeomLeaf sourceLeaf,
        Ps2GeomLeaf maskLeaf,
        out UvAffineTransform transform)
    {
        transform = default;
        var pairs = BuildPositionMatchedUvPairs(sourceLeaf, maskLeaf);
        if (pairs.Count < 3)
            return false;

        if (!TrySolveUvAffine(pairs, out transform))
            return false;

        var maxResidual = 0.0;
        foreach (var pair in pairs)
        {
            var (maskU, maskV) = transform.Transform(pair.SourceU, pair.SourceV);
            var du = maskU - pair.MaskU;
            var dv = maskV - pair.MaskV;
            maxResidual = Math.Max(maxResidual, Math.Sqrt(du * du + dv * dv));
        }

        return maxResidual <= 0.05;
    }

    private static List<UvPair> BuildPositionMatchedUvPairs(Ps2GeomLeaf sourceLeaf, Ps2GeomLeaf maskLeaf)
    {
        var sourceVertices = sourceLeaf.Vertices
            .Where(static v => v.HasUV)
            .ToArray();
        var maskVertices = maskLeaf.Vertices
            .Where(static v => v.HasUV)
            .ToArray();

        if (sourceVertices.Length < 3 || maskVertices.Length < 3)
            return [];

        var tolerance = ComputePositionMatchTolerance(sourceLeaf, maskLeaf);
        var toleranceSq = tolerance * tolerance;
        if (sourceVertices.Length == maskVertices.Length)
        {
            var orderedPairs = new List<UvPair>(sourceVertices.Length);
            var orderedMatch = true;
            for (var i = 0; i < sourceVertices.Length; i++)
            {
                if (Vector3.DistanceSquared(sourceVertices[i].Position, maskVertices[i].Position) > toleranceSq)
                {
                    orderedMatch = false;
                    break;
                }

                orderedPairs.Add(new UvPair(
                    sourceVertices[i].U,
                    sourceVertices[i].V,
                    maskVertices[i].U,
                    maskVertices[i].V));
            }

            if (orderedMatch)
                return orderedPairs;
        }

        var pairs = new List<UvPair>(Math.Min(sourceVertices.Length, maskVertices.Length));
        var usedMaskVertices = new bool[maskVertices.Length];
        foreach (var source in sourceVertices)
        {
            var bestIndex = -1;
            var bestDistanceSq = toleranceSq;
            for (var i = 0; i < maskVertices.Length; i++)
            {
                if (usedMaskVertices[i])
                    continue;

                var distanceSq = Vector3.DistanceSquared(source.Position, maskVertices[i].Position);
                if (distanceSq > bestDistanceSq)
                    continue;

                bestDistanceSq = distanceSq;
                bestIndex = i;
            }

            if (bestIndex < 0)
                continue;

            usedMaskVertices[bestIndex] = true;
            var mask = maskVertices[bestIndex];
            pairs.Add(new UvPair(source.U, source.V, mask.U, mask.V));
        }

        return pairs;
    }

    private static float ComputePositionMatchTolerance(Ps2GeomLeaf sourceLeaf, Ps2GeomLeaf maskLeaf)
    {
        var sourceBounds = ComputeBbox(sourceLeaf.Vertices);
        var maskBounds = ComputeBbox(maskLeaf.Vertices);
        var sourceSize = sourceBounds.Max - sourceBounds.Min;
        var maskSize = maskBounds.Max - maskBounds.Min;
        var maxDimension = Math.Max(
            Math.Max(Math.Abs(sourceSize.X), Math.Abs(sourceSize.Y)),
            Math.Max(Math.Abs(sourceSize.Z),
                Math.Max(Math.Abs(maskSize.X), Math.Max(Math.Abs(maskSize.Y), Math.Abs(maskSize.Z)))));
        return Math.Max(0.01f, maxDimension * 0.001f);
    }

    private static bool TrySolveUvAffine(IReadOnlyList<UvPair> pairs, out UvAffineTransform transform)
    {
        transform = default;
        Span<double> normal = stackalloc double[9];
        Span<double> rhsU = stackalloc double[3];
        Span<double> rhsV = stackalloc double[3];

        foreach (var pair in pairs)
        {
            var x0 = (double)pair.SourceU;
            var x1 = (double)pair.SourceV;
            var yU = (double)pair.MaskU;
            var yV = (double)pair.MaskV;

            normal[0] += x0 * x0;
            normal[1] += x0 * x1;
            normal[2] += x0;
            normal[3] += x1 * x0;
            normal[4] += x1 * x1;
            normal[5] += x1;
            normal[6] += x0;
            normal[7] += x1;
            normal[8] += 1.0;

            rhsU[0] += x0 * yU;
            rhsU[1] += x1 * yU;
            rhsU[2] += yU;
            rhsV[0] += x0 * yV;
            rhsV[1] += x1 * yV;
            rhsV[2] += yV;
        }

        Span<double> u = stackalloc double[3];
        Span<double> v = stackalloc double[3];
        if (!TrySolve3x3(normal, rhsU, u) || !TrySolve3x3(normal, rhsV, v))
            return false;

        transform = new UvAffineTransform(
            (float)u[0],
            (float)u[1],
            (float)u[2],
            (float)v[0],
            (float)v[1],
            (float)v[2]);
        return true;
    }

    private static bool TrySolve3x3(ReadOnlySpan<double> matrix, ReadOnlySpan<double> rhs, Span<double> solution)
    {
        var a00 = matrix[0];
        var a01 = matrix[1];
        var a02 = matrix[2];
        var a10 = matrix[3];
        var a11 = matrix[4];
        var a12 = matrix[5];
        var a20 = matrix[6];
        var a21 = matrix[7];
        var a22 = matrix[8];

        var det =
            a00 * (a11 * a22 - a12 * a21)
            - a01 * (a10 * a22 - a12 * a20)
            + a02 * (a10 * a21 - a11 * a20);
        if (Math.Abs(det) < 1e-8)
            return false;

        var b0 = rhs[0];
        var b1 = rhs[1];
        var b2 = rhs[2];

        solution[0] =
            (b0 * (a11 * a22 - a12 * a21)
             - a01 * (b1 * a22 - a12 * b2)
             + a02 * (b1 * a21 - a11 * b2)) / det;
        solution[1] =
            (a00 * (b1 * a22 - a12 * b2)
             - b0 * (a10 * a22 - a12 * a20)
             + a02 * (a10 * b2 - b1 * a20)) / det;
        solution[2] =
            (a00 * (a11 * b2 - b1 * a21)
             - a01 * (a10 * b2 - b1 * a20)
             + b0 * (a10 * a21 - a11 * a20)) / det;
        return true;
    }

    private static byte[] ApplyDestinationAlphaMask(
        byte[] sourcePng,
        byte[] maskPng,
        UvAffineTransform? maskFromSourceUv = null,
        ulong maskClamp1 = 0,
        ulong sourceTest1 = 0)
    {
        using var source = Image.Load<Rgba32>(sourcePng);
        using var mask = Image.Load<Rgba32>(maskPng);
        var maskAlpha = new byte[mask.Width * mask.Height];
        var clampS = (maskClamp1 & 0x03) != 0;
        var clampT = ((maskClamp1 >> 2) & 0x03) != 0;

        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    maskAlpha[y * mask.Width + x] = row[x].A;
            }
        });

        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    byte maskA;
                    if (maskFromSourceUv is { } transform)
                    {
                        var sourceU = (x + 0.5f) / row.Length;
                        var sourceV = (y + 0.5f) / accessor.Height;
                        var (maskU, maskV) = transform.Transform(sourceU, sourceV);
                        maskA = SampleAlpha(maskAlpha, mask.Width, mask.Height, maskU, maskV, clampS, clampT);
                    }
                    else
                    {
                        var maskX = x * mask.Width / row.Length;
                        var maskY = y * mask.Height / accessor.Height;
                        maskA = maskAlpha[maskY * mask.Width + maskX];
                    }

                    var sourceCoverage = ComputeSourceAlphaTestCoverage(p.A, sourceTest1);
                    var alpha = sourceCoverage * maskA / 255;
                    row[x] = new Rgba32(p.R, p.G, p.B, (byte)alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        source.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte ComputeSourceAlphaTestCoverage(byte sourceAlpha, ulong test)
    {
        var ateEnabled = (test & 0x1UL) != 0;
        if (!ateEnabled)
            return 255;

        var atst = (int)((test >> 1) & 0x7);
        if (atst == 1) // ATST_ALWAYS
            return 255;

        var afail = (int)((test >> 12) & 0x3);
        if (afail is not (0 or 2))
            return 255;

        var aref = (byte)((test >> 4) & 0xFF);
        return AlphaTestPasses(sourceAlpha, aref, atst) ? (byte)255 : (byte)0;
    }

    private static bool AlphaTestPasses(byte sourceAlpha, byte aref, int atst)
    {
        return atst switch
        {
            0 => false, // NEVER
            1 => true, // ALWAYS
            2 => sourceAlpha < aref, // LESS
            3 => sourceAlpha <= aref, // LEQUAL
            4 => sourceAlpha == aref, // EQUAL
            5 => sourceAlpha >= aref, // GEQUAL
            6 => sourceAlpha > aref, // GREATER
            7 => sourceAlpha != aref, // NOTEQUAL
            _ => true
        };
    }
}
