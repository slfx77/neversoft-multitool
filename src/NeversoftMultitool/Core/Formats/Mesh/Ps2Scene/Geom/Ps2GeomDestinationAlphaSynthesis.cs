using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal static class Ps2GeomDestinationAlphaSynthesis
{
    internal static IReadOnlyList<Ps2DestinationAlphaMaskCandidate> BuildMaskCandidates(
        IReadOnlyList<WorldzoneLeafDrawItem> orderedLeaves,
        Ps2TexaTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        Func<Ps2GeomLeaf, bool>? leafFilter,
        Func<Ps2GeomLeaf, bool>? skipLeaf)
    {
        if (textureProvider == null ||
            !orderedLeaves.Any(static item =>
                Ps2GeomRenderSemantics.UsesDestinationAlphaBlend((byte)(item.Leaf.DmaAlpha1 & 0xFF))))
        {
            return [];
        }

        var candidates = new List<Ps2DestinationAlphaMaskCandidate>();
        foreach (var item in orderedLeaves)
        {
            var leaf = item.Leaf;
            if (leaf.Vertices.Length < 3 ||
                (leafFilter != null && !leafFilter(leaf)) ||
                (skipLeaf != null && skipLeaf(leaf)) ||
                !Ps2GeomRenderSemantics.WritesFramebufferAlpha(leaf) ||
                Ps2GeomRenderSemantics.UsesDestinationAlphaBlend((byte)(leaf.DmaAlpha1 & 0xFF)))
            {
                continue;
            }

            var textureChecksum = ResolveTextureChecksum(leaf, tex0Resolver);
            if (textureChecksum == 0)
                continue;

            var pngBytes = textureProvider(textureChecksum, leaf.DmaTexa);
            if (pngBytes == null || !HasUsefulDestinationAlpha(pngBytes))
                continue;

            candidates.Add(new Ps2DestinationAlphaMaskCandidate(
                CreateLeafGeometryKey(leaf),
                textureChecksum,
                leaf));
        }

        return candidates;
    }

    internal static bool TryCreateSyntheticTexture(
        Ps2GeomLeaf sourceLeaf,
        uint sourceChecksum,
        uint sourceRenderOrder,
        IReadOnlyList<Ps2DestinationAlphaMaskCandidate> candidates,
        IReadOnlyDictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate> recentExactMasks,
        Ps2TexaTextureResolver textureProvider,
        IDictionary<uint, byte[]> syntheticTextures,
        out uint syntheticChecksum)
    {
        syntheticChecksum = 0;
        if (sourceChecksum == 0 ||
            !Ps2GeomRenderSemantics.UsesDestinationAlphaBlend((byte)(sourceLeaf.DmaAlpha1 & 0xFF)) ||
            !DestinationAlphaSynthesisEligible())
        {
            return false;
        }

        var geometryKey = CreateLeafGeometryKey(sourceLeaf);
        if (!TryFindDestinationAlphaMask(
                geometryKey,
                sourceChecksum,
                sourceRenderOrder,
                candidates,
                out var maskCandidate) &&
            !recentExactMasks.TryGetValue(geometryKey, out maskCandidate))
        {
            return false;
        }

        if (maskCandidate.TextureChecksum == 0)
            return false;
        if (!Ps2GeomRenderSemantics.WritesFramebufferAlpha(maskCandidate.Leaf))
            return false;

        var sourcePng = textureProvider(sourceChecksum, sourceLeaf.DmaTexa);
        var maskPng = textureProvider(maskCandidate.TextureChecksum, maskCandidate.Leaf.DmaTexa);
        if (sourcePng == null || maskPng == null)
            return false;

        var maskAlphaBlend = (byte)(maskCandidate.Leaf.DmaAlpha1 & 0xFF);
        var sourceAlphaBlend = (byte)(sourceLeaf.DmaAlpha1 & 0xFF);
        if (!Ps2GeomRenderSemantics.TryGetDestinationAlphaSourceMaskMode(sourceAlphaBlend, out var invertMask))
            return false;

        var maskIsOpaqueWriter = maskAlphaBlend is 0x0A or 0x1A or 0x00;
        var effectiveMaskPng = maskIsOpaqueWriter
            ? CreateUniformOpaqueMask()
            : MaskShouldFlattenToAverage(maskPng, maskCandidate.Leaf)
                ? FlattenMaskAlphaToAverage(maskPng)
                : maskPng;

        var hasUvTransform = TryComputeDestinationAlphaUvTransform(
            sourceLeaf,
            maskCandidate.Leaf,
            out var maskFromSourceUv);
        var maskedPng = ApplyDestinationAlphaMask(
            sourcePng,
            effectiveMaskPng,
            hasUvTransform ? maskFromSourceUv : null,
            maskCandidate.Leaf.DmaClamp1,
            invertMask,
            sourceLeaf.DmaTest1);

        syntheticChecksum = CreateSyntheticTextureChecksum(sourceChecksum, maskCandidate.TextureChecksum);
        while (syntheticTextures.TryGetValue(syntheticChecksum, out var existing) &&
               !existing.SequenceEqual(maskedPng))
        {
            syntheticChecksum++;
        }

        syntheticTextures[syntheticChecksum] = maskedPng;
        return true;
    }

    internal static Ps2DestinationAlphaLeafGeometryKey CreateLeafGeometryKey(Ps2GeomLeaf leaf)
    {
        var (min, max) = ComputeBbox(leaf.Vertices);
        return new Ps2DestinationAlphaLeafGeometryKey(leaf.Vertices.Length, min, max);
    }

    internal static string ClassifyTextureAlphaMode(byte[] pngBytes)
    {
        return AnalyzeAlphaProfile(pngBytes) switch
        {
            AlphaProfile.Bimodal => "MASK",
            AlphaProfile.Graduated => "BLEND",
            _ => "OPAQUE"
        };
    }

    internal static bool ShouldFallbackToSourceAlphaBlend(Ps2GeomLeaf leaf)
    {
        return Ps2GeomRenderSemantics.UsesDestinationAlphaBlend((byte)(leaf.DmaAlpha1 & 0xFF)) &&
               ReadDestAlphaStrategy() == "blend";
    }

    private static bool TryFindDestinationAlphaMask(
        Ps2DestinationAlphaLeafGeometryKey geometryKey,
        uint sourceChecksum,
        uint sourceRenderOrder,
        IReadOnlyList<Ps2DestinationAlphaMaskCandidate> candidates,
        out Ps2DestinationAlphaMaskCandidate maskCandidate)
    {
        Ps2DestinationAlphaMaskCandidate? bestOpaque = null;
        uint bestOpaqueOrder = 0;
        Ps2DestinationAlphaMaskCandidate? bestBlend = null;
        uint bestBlendOrder = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.TextureChecksum == 0 || candidate.TextureChecksum == sourceChecksum)
                continue;
            if (!geometryKey.Equals(candidate.Geometry))
                continue;

            var candidateOrder = Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(candidate.Leaf);
            if (candidateOrder >= sourceRenderOrder)
                continue;

            var candidateAlphaBlend = (byte)(candidate.Leaf.DmaAlpha1 & 0xFF);
            var isOpaqueWriter = candidateAlphaBlend is 0x0A or 0x1A or 0x00;
            if (isOpaqueWriter)
            {
                if (bestOpaque is null || candidateOrder >= bestOpaqueOrder)
                {
                    bestOpaque = candidate;
                    bestOpaqueOrder = candidateOrder;
                }
            }
            else if (bestBlend is null || candidateOrder >= bestBlendOrder)
            {
                bestBlend = candidate;
                bestBlendOrder = candidateOrder;
            }
        }

        if (bestOpaque is { } foundOpaque)
        {
            maskCandidate = foundOpaque;
            return true;
        }

        if (bestBlend is { } foundBlend)
        {
            maskCandidate = foundBlend;
            return true;
        }

        maskCandidate = default;
        return false;
    }

    private static uint ResolveTextureChecksum(Ps2GeomLeaf leaf, Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        if (leaf.TextureChecksum != 0)
            return leaf.TextureChecksum;

        return leaf.DmaTex0 != 0 && tex0Resolver != null
            ? tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum)
            : 0;
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
        var (sourceMin, sourceMax) = ComputeBbox(sourceLeaf.Vertices);
        var (maskMin, maskMax) = ComputeBbox(maskLeaf.Vertices);
        var sourceSize = sourceMax - sourceMin;
        var maskSize = maskMax - maskMin;
        var maxDimension = Math.Max(
            Math.Max(Math.Abs(sourceSize.X), Math.Abs(sourceSize.Y)),
            Math.Max(Math.Abs(sourceSize.Z), Math.Max(Math.Abs(maskSize.X), Math.Max(Math.Abs(maskSize.Y), Math.Abs(maskSize.Z)))));
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
        bool invertMask = false,
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

                    if (invertMask)
                        maskA = (byte)(255 - maskA);

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

    private static bool AlphaTestPasses(byte sourceAlpha, byte aref, int atst) =>
        atst switch
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

    private static byte SampleAlpha(
        byte[] alpha,
        int width,
        int height,
        float u,
        float v,
        bool clampS,
        bool clampT)
    {
        var x = TextureCoordinateToPixel(u, width, clampS);
        var y = TextureCoordinateToPixel(v, height, clampT);
        return alpha[y * width + x];
    }

    private static int TextureCoordinateToPixel(float coordinate, int length, bool clamp)
    {
        if (length <= 1)
            return 0;

        double normalized;
        if (clamp)
        {
            normalized = Math.Clamp(coordinate, 0f, 1f);
            var clamped = (int)Math.Floor(normalized * length);
            return Math.Min(length - 1, Math.Max(0, clamped));
        }

        normalized = coordinate - Math.Floor(coordinate);
        if (normalized < 0)
            normalized += 1.0;
        var repeated = (int)Math.Floor(normalized * length);
        return Math.Min(length - 1, Math.Max(0, repeated));
    }

    private static bool HasUsefulDestinationAlpha(byte[] pngBytes) =>
        AnalyzeAlphaProfile(pngBytes) != AlphaProfile.AllOpaque;

    private static AlphaProfile AnalyzeAlphaProfile(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var total = image.Width * image.Height;
        if (total == 0)
            return AlphaProfile.AllOpaque;

        var counts = new int[3];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var a = row[x].A;
                    if (a <= 8) counts[0]++;
                    else if (a >= 248) counts[1]++;
                    else counts[2]++;
                }
            }
        });

        var low = counts[0];
        var high = counts[1];
        var mid = counts[2];
        if (low == 0 && mid == 0)
            return AlphaProfile.AllOpaque;
        if (high == 0 && mid > 0)
            return AlphaProfile.Graduated;
        if ((low + high) * 5 >= total * 4)
            return AlphaProfile.Bimodal;
        return AlphaProfile.Graduated;
    }

    private static bool MaskShouldFlattenToAverage(byte[] maskPng, Ps2GeomLeaf maskLeaf)
    {
        const float maxTilingFactor = 4.0f;

        using var image = Image.Load<Rgba32>(maskPng);
        var texW = image.Width;
        var texH = image.Height;
        if (texW <= 0 || texH <= 0)
            return false;

        var hasTransparentPixels = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !hasTransparentPixels; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A == 0)
                    {
                        hasTransparentPixels = true;
                        break;
                    }
                }
            }
        });
        if (!hasTransparentPixels)
            return false;

        var (min, max) = ComputeBbox(maskLeaf.Vertices);
        var size = max - min;
        var sx = MathF.Abs(size.X);
        var sy = MathF.Abs(size.Y);
        var sz = MathF.Abs(size.Z);
        var maxAxis = MathF.Max(sx, MathF.Max(sy, sz));
        var minAxis = MathF.Min(sx, MathF.Min(sy, sz));
        var midAxis = sx + sy + sz - maxAxis - minAxis;
        if (maxAxis <= 0f || midAxis <= 0f)
            return false;

        var tilingMajor = maxAxis / texW;
        var tilingMinor = midAxis / texH;
        if (tilingMajor < tilingMinor) (tilingMajor, tilingMinor) = (tilingMinor, tilingMajor);

        return tilingMajor > maxTilingFactor;
    }

    private static byte[] CreateUniformOpaqueMask()
    {
        using var flat = new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, 255));
        using var ms = new MemoryStream();
        flat.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] FlattenMaskAlphaToAverage(byte[] maskPng)
    {
        using var src = Image.Load<Rgba32>(maskPng);
        long sum = 0;
        long count = 0;
        src.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    sum += row[x].A;
                    count++;
                }
            }
        });

        var avgAlpha = count > 0 ? (byte)(sum / count) : (byte)0;
        using var flat = new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, avgAlpha));
        using var ms = new MemoryStream();
        flat.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static (Vector3 Min, Vector3 Max) ComputeBbox(IReadOnlyList<Ps2Vertex> vertices)
    {
        if (vertices.Count == 0)
            return (Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return (min, max);
    }

    private static bool DestinationAlphaSynthesisEligible() =>
        ReadDestAlphaStrategy() is "synthesize" or "blend";

    private static string ReadDestAlphaStrategy()
    {
        var value = Environment.GetEnvironmentVariable("THAW_DEST_ALPHA");
        return string.IsNullOrWhiteSpace(value)
            ? "synthesize"
            : value.Trim().ToLowerInvariant();
    }

    private static uint CreateSyntheticTextureChecksum(uint sourceChecksum, uint maskChecksum)
    {
        var hash = 0xA1F3D5B7u;
        hash ^= RotateLeft(sourceChecksum, 7);
        hash ^= RotateLeft(maskChecksum, 19);
        return hash | 0x80000000u;
    }

    private static uint RotateLeft(uint value, int shift) =>
        (value << shift) | (value >> (32 - shift));

    private enum AlphaProfile
    {
        AllOpaque,
        Bimodal,
        Graduated
    }

    private readonly record struct UvPair(float SourceU, float SourceV, float MaskU, float MaskV);

    private readonly record struct UvAffineTransform(
        float MaskUFromSourceU,
        float MaskUFromSourceV,
        float MaskUOffset,
        float MaskVFromSourceU,
        float MaskVFromSourceV,
        float MaskVOffset)
    {
        public (float U, float V) Transform(float sourceU, float sourceV)
        {
            var u = MaskUFromSourceU * sourceU + MaskUFromSourceV * sourceV + MaskUOffset;
            var v = MaskVFromSourceU * sourceU + MaskVFromSourceV * sourceV + MaskVOffset;
            return (u, v);
        }
    }
}

internal readonly record struct Ps2DestinationAlphaMaskCandidate(
    Ps2DestinationAlphaLeafGeometryKey Geometry,
    uint TextureChecksum,
    Ps2GeomLeaf Leaf);

internal readonly record struct Ps2DestinationAlphaLeafGeometryKey(
    int VertexCount,
    Vector3 Min,
    Vector3 Max);
