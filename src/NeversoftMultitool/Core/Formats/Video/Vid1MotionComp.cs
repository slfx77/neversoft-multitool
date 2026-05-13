namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     8×8 block motion compensation for Vid1 inter prediction.
///     Implements MPEG-4 H.263-style half-pel bilinear interpolation:
///     H-only: (a + b + 1) &gt;&gt; 1
///     V-only: (a + b + 1) &gt;&gt; 1
///     HV:     (a + b + c + d + 2) &gt;&gt; 2
///     Semantically equivalent to the heavily-unrolled Gekko paired-single
///     kernels at <c>FUN_8029ECE8</c> / <c>FUN_8029F868</c>, just without the
///     platform-specific SIMD.
/// </summary>
internal static class Vid1MotionComp
{
    /// <summary>
    ///     Predict an 8×8 block via motion compensation + residual + clamp.
    /// </summary>
    /// <param name="refPlane">Reference frame plane (Y or Cb or Cr).</param>
    /// <param name="refStride">Bytes per reference row.</param>
    /// <param name="refWidth">Reference plane width in pixels.</param>
    /// <param name="refHeight">Reference plane height in pixels.</param>
    /// <param name="srcX">Source X (integer pel part; can be negative — clamped).</param>
    /// <param name="srcY">Source Y (integer pel part; can be negative — clamped).</param>
    /// <param name="halfX">0 for integer-X, 1 for half-pel-X.</param>
    /// <param name="halfY">0 for integer-Y, 1 for half-pel-Y.</param>
    /// <param name="residual">64 signed IDCT-output samples (row-major 8×8).</param>
    /// <param name="outPlane">Output frame plane.</param>
    /// <param name="outStride">Bytes per output row.</param>
    /// <param name="dstX">Destination X in output plane.</param>
    /// <param name="dstY">Destination Y in output plane.</param>
    public static void PredictInterBlock(
        byte[] refPlane, int refStride, int refWidth, int refHeight,
        int srcX, int srcY, int halfX, int halfY,
        ReadOnlySpan<short> residual,
        byte[] outPlane, int outStride,
        int dstX, int dstY,
        int roundingBias = 0)
    {
        var bias = roundingBias & 1;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var predicted = FetchPredictionPixel(
                    refPlane, refStride, refWidth, refHeight,
                    srcX + x, srcY + y, halfX, halfY, bias);
                var combined = predicted + residual[y * 8 + x];
                outPlane[(dstY + y) * outStride + dstX + x] = ClampByte(combined);
            }
        }
    }

    public static void PredictInterBlockToSpan(
        byte[] refPlane, int refStride, int refWidth, int refHeight,
        int srcX, int srcY, int halfX, int halfY,
        ReadOnlySpan<short> residual,
        Span<byte> output, int outputStride,
        int roundingBias = 0)
    {
        var bias = roundingBias & 1;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var predicted = FetchPredictionPixel(
                    refPlane, refStride, refWidth, refHeight,
                    srcX + x, srcY + y, halfX, halfY, bias);
                var combined = predicted + residual[y * 8 + x];
                output[y * outputStride + x] = ClampByte(combined);
            }
        }
    }

    /// <summary>
    ///     Write an intra 8×8 block — MPEG-4 reconstructs intra samples by
    ///     adding 128 (the DC-offset used during encoding) and clamping.
    /// </summary>
    public static void WriteIntraBlock(
        ReadOnlySpan<short> samples,
        byte[] outPlane, int outStride,
        int dstX, int dstY,
        int sampleOffset = 128)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                outPlane[(dstY + y) * outStride + dstX + x] = ClampByte(samples[y * 8 + x] + sampleOffset);
            }
        }
    }

    /// <summary>
    ///     Write an intra block after MPEG-4 DC/AC prediction has already
    ///     restored the coefficient-domain 128 DC bias.
    /// </summary>
    public static void WritePredictedIntraBlock(
        ReadOnlySpan<short> samples,
        byte[] outPlane, int outStride,
        int dstX, int dstY)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                outPlane[(dstY + y) * outStride + dstX + x] = ClampByte(samples[y * 8 + x]);
            }
        }
    }

    /// <summary>
    ///     Skipped-MB copy: just lift the reference block at the same position
    ///     into the output (no residual, no motion vector).
    /// </summary>
    public static void CopyReferenceBlock(
        byte[] refPlane, int refStride,
        byte[] outPlane, int outStride,
        int x, int y)
    {
        for (var row = 0; row < 8; row++)
        {
            var srcOffset = (y + row) * refStride + x;
            var dstOffset = (y + row) * outStride + x;
            Buffer.BlockCopy(refPlane, srcOffset, outPlane, dstOffset, 8);
        }
    }

    public static void WarpSpriteBlock(
        byte[] refPlane,
        int refStride,
        int refWidth,
        int refHeight,
        int srcX,
        int srcY,
        int fracX16,
        int fracY16,
        byte[] outPlane,
        int outStride,
        int dstX,
        int dstY,
        int roundingBias = 0)
    {
        var fx = Math.Clamp(fracX16, 0, 15);
        var fy = Math.Clamp(fracY16, 0, 15);
        var bias = roundingBias & 1;

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var px = srcX + x;
                var py = srcY + y;
                var a = Sample(refPlane, refStride, refWidth, refHeight, px, py);
                var b = Sample(refPlane, refStride, refWidth, refHeight, px + 1, py);
                var c = Sample(refPlane, refStride, refWidth, refHeight, px, py + 1);
                var d = Sample(refPlane, refStride, refWidth, refHeight, px + 1, py + 1);
                var predicted =
                    ((16 - fx) * (16 - fy) * a +
                     fx * (16 - fy) * b +
                     (16 - fx) * fy * c +
                     fx * fy * d +
                     (128 - bias)) >> 8;
                outPlane[(dstY + y) * outStride + dstX + x] = (byte)predicted;
            }
        }
    }

    public static void PredictFieldBlock(
        byte[] refPlane,
        int refStride,
        int refWidth,
        int refHeight,
        byte[] outPlane,
        int outStride,
        int dstX,
        int dstY,
        int blockWidth,
        int blockHeight,
        int firstMvX,
        int firstMvY,
        int secondMvX,
        int secondMvY,
        int fieldSelectBits,
        int roundingBias = 0)
    {
        var bias = roundingBias & 1;
        for (var row = 0; row < blockHeight; row++)
        {
            var field = row & 1;
            var mvX = field == 0 ? firstMvX : secondMvX;
            var mvY = field == 0 ? firstMvY : secondMvY;
            var fieldSelect = (fieldSelectBits >> field) & 1;
            var srcX = dstX + (mvX >> 1);
            var srcY = dstY + (row & ~1) + ((mvY >> 1) & ~1) + fieldSelect;
            var halfX = mvX & 1;
            var halfY = (mvY >> 1) & 1;

            for (var col = 0; col < blockWidth; col++)
            {
                var predicted = FetchPredictionPixel(
                    refPlane, refStride, refWidth, refHeight,
                    srcX + col, srcY, halfX, halfY, bias);
                outPlane[(dstY + row) * outStride + dstX + col] = (byte)predicted;
            }
        }
    }

    public static void WarpSpriteAffineBlock(
        byte[] refPlane,
        int refStride,
        int refWidth,
        int refHeight,
        int dstX,
        int dstY,
        int baseFixedX,
        int baseFixedY,
        int scaleX,
        int crossX,
        int crossY,
        int scaleY,
        int transformShift,
        int fixedShift,
        byte[] outPlane,
        int outStride,
        int roundingBias = 0)
    {
        var shift = Math.Max(transformShift, 0);
        var pixelShift = Math.Max(fixedShift, 0);
        var fracShift = Math.Max(4 - pixelShift, 0);
        var bias = roundingBias & 1;

        for (var y = 0; y < 8; y++)
        {
            var absoluteY = dstY + y;
            for (var x = 0; x < 8; x++)
            {
                var absoluteX = dstX + x;
                var sourceFixedX =
                    baseFixedX +
                    (((long)scaleX * absoluteX + (long)crossX * absoluteY) >> shift);
                var sourceFixedY =
                    baseFixedY +
                    (((long)crossY * absoluteX + (long)scaleY * absoluteY) >> shift);

                var srcX = (int)(sourceFixedX >> pixelShift);
                var srcY = (int)(sourceFixedY >> pixelShift);
                var fracX = fracShift == 0 ? 0 : (int)((sourceFixedX << fracShift) & 0xF);
                var fracY = fracShift == 0 ? 0 : (int)((sourceFixedY << fracShift) & 0xF);

                var a = Sample(refPlane, refStride, refWidth, refHeight, srcX, srcY);
                var b = Sample(refPlane, refStride, refWidth, refHeight, srcX + 1, srcY);
                var c = Sample(refPlane, refStride, refWidth, refHeight, srcX, srcY + 1);
                var d = Sample(refPlane, refStride, refWidth, refHeight, srcX + 1, srcY + 1);
                var predicted =
                    ((16 - fracX) * (16 - fracY) * a +
                     fracX * (16 - fracY) * b +
                     (16 - fracX) * fracY * c +
                     fracX * fracY * d +
                     (128 - bias)) >> 8;

                outPlane[(dstY + y) * outStride + dstX + x] = (byte)predicted;
            }
        }
    }

    public static void AddResidualToBlock(
        ReadOnlySpan<short> residual,
        byte[] outPlane,
        int outStride,
        int dstX,
        int dstY)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var offset = (dstY + y) * outStride + dstX + x;
                outPlane[offset] = ClampByte(outPlane[offset] + residual[y * 8 + x]);
            }
        }
    }

    /// <summary>
    ///     GMC sprite-warp stub: treat as identity (no warp). Full port is
    ///     deferred; see <c>FUN_8029F7B8</c>. For now, class-3 frames with
    ///     active GMC fall back to regular copy-reference behavior.
    /// </summary>
    public static void GmcWarpBlock(
        byte[] refPlane, int refStride,
        ReadOnlySpan<short> residual,
        byte[] outPlane, int outStride,
        int x, int y)
    {
        // Identity warp — pixel-for-pixel copy + residual.
        // This is a placeholder; real GMC uses the sprite trajectory points
        // to compute a per-pixel affine/perspective mapping.
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                var predicted = refPlane[(y + row) * refStride + x + col];
                var combined = predicted + residual[row * 8 + col];
                outPlane[(y + row) * outStride + x + col] = ClampByte(combined);
            }
        }
    }

    private static int FetchPredictionPixel(
        byte[] refPlane, int refStride, int refWidth, int refHeight,
        int x, int y, int halfX, int halfY, int roundingBias)
    {
        if (halfX == 0 && halfY == 0)
        {
            return Sample(refPlane, refStride, refWidth, refHeight, x, y);
        }

        if (halfX == 1 && halfY == 0)
        {
            var a = Sample(refPlane, refStride, refWidth, refHeight, x, y);
            var b = Sample(refPlane, refStride, refWidth, refHeight, x + 1, y);
            return (a + b + 1 - roundingBias) >> 1;
        }

        if (halfX == 0 && halfY == 1)
        {
            var a = Sample(refPlane, refStride, refWidth, refHeight, x, y);
            var b = Sample(refPlane, refStride, refWidth, refHeight, x, y + 1);
            return (a + b + 1 - roundingBias) >> 1;
        }

        // halfX == 1 && halfY == 1 — full 2×2 bilinear
        var a2 = Sample(refPlane, refStride, refWidth, refHeight, x, y);
        var b2 = Sample(refPlane, refStride, refWidth, refHeight, x + 1, y);
        var c2 = Sample(refPlane, refStride, refWidth, refHeight, x, y + 1);
        var d2 = Sample(refPlane, refStride, refWidth, refHeight, x + 1, y + 1);
        return (a2 + b2 + c2 + d2 + 2 - roundingBias) >> 2;
    }

    private static byte Sample(byte[] refPlane, int refStride, int refWidth, int refHeight, int x, int y)
    {
        // Edge-pad: clamp coordinates to frame bounds. Standard MPEG-4 unrestricted
        // motion vectors use the same replicated-edge behavior.
        var cx = Math.Clamp(x, 0, refWidth - 1);
        var cy = Math.Clamp(y, 0, refHeight - 1);
        return refPlane[cy * refStride + cx];
    }

    private static byte ClampByte(int value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return (byte)value;
    }
}
