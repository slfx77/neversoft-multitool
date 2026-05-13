namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Decompresses a single channel of PS1-era Neversoft character animation data.
///     Mechanical port of <c>DecompressStream</c> from the THPS2 PSX prototype
///     decompilation
///     (<c>\\wsl.localhost\Ubuntu\home\slfx77\thps2-psx-proto\src\DECOMP.cpp</c>,
///     lines 37–250). One stream produces <c>streamLength</c> 16-bit signed
///     samples scattered through <paramref name="dst" /> with a writer-controlled
///     stride, so 6 channels × N bones can be filled into a single buffer by
///     consecutive calls at staggered start offsets.
///     Wire format (per stream):
///     <list type="bullet">
///         <item>1 header byte: high nibble = numSegments − 1, low nibble = mode (0..15).</item>
///         <item>Mode 0: no payload (output = zeros).</item>
///         <item>Mode 1: 16-bit endpoints; segments linearly interpolated.</item>
///         <item>Modes 2..14: 16-bit start, then bit-packed deltas (bit width = mode + 1).</item>
///         <item>Mode 15: single 16-bit value repeated.</item>
///     </list>
/// </summary>
internal static class PsxAnimDecompressor
{
    /// <summary>
    ///     Decompresses one channel into <paramref name="dst" />, writing
    ///     <paramref name="streamLength" /> samples at indices
    ///     <c>0, step, 2·step, …, (streamLength−1)·step</c>.
    /// </summary>
    /// <param name="src">Compressed bytes; reading begins at offset 0.</param>
    /// <param name="dst">Destination buffer in s16 elements (NOT bytes).</param>
    /// <param name="step">Stride in s16 elements between consecutive samples.</param>
    /// <param name="streamLength">Number of samples to emit (typically frame count).</param>
    /// <returns>Number of <paramref name="src" /> bytes consumed.</returns>
    public static int Decompress(ReadOnlySpan<byte> src, Span<short> dst, int step, int streamLength)
    {
        var srcIdx = 0;
        var header = src[srcIdx++];
        var numSegments = (header >> 4) + 1;
        var mode = header & 0xF;

        int segLength;
        int remainder;
        if (numSegments >= 2)
        {
            segLength = (streamLength - 1) / numSegments;
            remainder = streamLength - (segLength * numSegments + 1);
        }
        else
        {
            segLength = streamLength - 1;
            remainder = 0;
        }

        switch (mode)
        {
            case 0:
                EmitZeros(dst, step, streamLength);
                break;
            case 1:
                srcIdx = ModeLinearInterp16(src, srcIdx, dst, step, numSegments, segLength, remainder);
                break;
            case 15:
                srcIdx = ModeRepeatConstant(src, srcIdx, dst, step, streamLength);
                break;
            default:
                if (mode is >= 2 and <= 14)
                    srcIdx = ModeBitPackedDelta(src, srcIdx, dst, step, mode + 1, numSegments, segLength, remainder);
                break;
        }

        return srcIdx;
    }

    // Mode 0: every output sample is zero. No payload.
    private static void EmitZeros(Span<short> dst, int step, int streamLength)
    {
        var idx = 0;
        for (var i = 0; i < streamLength; i++)
        {
            dst[idx] = 0;
            idx += step;
        }
    }

    // Mode 1: pairs of u16 endpoints; segments linearly interpolated.
    // Per-outer-iteration sample count is numSegments (NOT numSegments + 1) —
    // the formula `1 + segLength × numSegments + remainder = StreamLength`
    // settles this. The C reconstruction at DECOMP.cpp:114 writes an extra
    // endpoint sample per outer iteration, but that overcounts: e.g. for
    // numSegments=1, segLength=StreamLength-1, the reconstruction writes
    // 1 + 2×(StreamLength-1) ≈ 2×StreamLength samples, double the true count.
    // The endpoint u16 is still READ each iteration (advances srcIdx) and
    // becomes the next iteration's `prev`; it just isn't emitted as a sample.
    private static int ModeLinearInterp16(
        ReadOnlySpan<byte> src, int srcIdx, Span<short> dst,
        int step, int numSegments, int segLength, int remainder)
    {
        var prev = ReadInt16Le(src, srcIdx);
        srcIdx += 2;
        var dstIdx = 0;
        dst[dstIdx] = (short)prev;
        dstIdx += step;

        for (var seg = 0; seg < segLength; seg++)
        {
            var endpoint = ReadInt16Le(src, srcIdx);
            srcIdx += 2;
            var delta = endpoint - prev;

            for (var k = 0; k < numSegments; k++)
            {
                prev += (short)(delta / numSegments);
                dst[dstIdx] = (short)prev;
                dstIdx += step;
            }

            prev = endpoint;
        }

        if (remainder > 0)
        {
            var endpoint = ReadInt16Le(src, srcIdx);
            srcIdx += 2;
            var delta = endpoint - prev;

            // Remainder writes (remainder - 1) interpolated + 1 endpoint =
            // `remainder` samples total. The endpoint sample IS written here
            // because there's no next iteration to absorb it.
            for (var k = 0; k < remainder - 1; k++)
            {
                prev += (short)(delta / remainder);
                dst[dstIdx] = (short)prev;
                dstIdx += step;
            }

            dst[dstIdx] = (short)endpoint;
        }

        return srcIdx;
    }

    // Modes 2..14: 16-bit initial sample, then bit-packed signed deltas with
    // bitWidth = mode + 1. Same correction applies as in mode 1: per outer
    // iteration writes `numSegments` samples, NOT `numSegments + 1`. The
    // accumulated `prev` carries forward into the next outer iteration so the
    // endpoint isn't lost.
    private static int ModeBitPackedDelta(
        ReadOnlySpan<byte> src, int srcIdx, Span<short> dst,
        int step, int bitWidth, int numSegments, int segLength, int remainder)
    {
        var prev = ReadInt16Le(src, srcIdx);
        srcIdx++; // bitstream begins at srcIdx (which equals stream byte 2);
        // the high byte of the starting u16 also serves as the bitstream's first
        // byte (overlap noted in DECOMP.cpp:158-162).
        var dstIdx = 0;
        dst[dstIdx] = (short)prev;
        dstIdx += step;

        var bitOff = 0; // bit offset within current byte (0..7)
        var byteIdx = srcIdx;

        for (var seg = 0; seg < segLength; seg++)
        {
            var delta = ReadSignedBits(src, ref byteIdx, ref bitOff, bitWidth);
            for (var k = 0; k < numSegments; k++)
            {
                prev += (short)(delta / numSegments);
                dst[dstIdx] = (short)prev;
                dstIdx += step;
            }
            // No endpoint write — `prev` carries forward as the start of the
            // next iteration's interpolation. The encoder's per-outer step is
            // `numSegments` samples covered, matching the formula
            // `segLength × numSegments + 1 + remainder = StreamLength`.
        }

        if (remainder > 0)
        {
            var delta = ReadSignedBits(src, ref byteIdx, ref bitOff, bitWidth);
            // Remainder writes (remainder - 1) interpolated + 1 endpoint = `remainder`
            // samples total. The endpoint IS written here (no next iter to absorb).
            for (var k = 0; k < remainder - 1; k++)
            {
                prev += (short)(delta / remainder);
                dst[dstIdx] = (short)prev;
                dstIdx += step;
            }

            dst[dstIdx] = (short)(prev + delta);
        }

        // If we ended mid-byte, the next stream starts on the following byte.
        if (bitOff != 0)
            byteIdx++;

        return byteIdx;
    }

    // Mode 15: skip 1 byte (mode/segment header was already consumed; the
    // original advances `pbVar9 = src + 1` then reads bytes at [1] and [2]),
    // then write a single u16 LE constant for every sample.
    private static int ModeRepeatConstant(
        ReadOnlySpan<byte> src, int srcIdx, Span<short> dst, int step, int streamLength)
    {
        srcIdx++; // skip filler byte (matches DECOMP.cpp:218)
        var value = ReadInt16Le(src, srcIdx);
        srcIdx += 2;

        var dstIdx = 0;
        for (var i = 0; i < streamLength; i++)
        {
            dst[dstIdx] = (short)value;
            dstIdx += step;
        }

        return srcIdx;
    }

    /// <summary>
    ///     Read a signed bit-packed integer from a byte stream. Reads exactly
    ///     <paramref name="bitWidth" /> bits starting at <c>(byteIdx, bitOff)</c>,
    ///     advances both, and sign-extends the result from <paramref name="bitWidth" />
    ///     to 32 bits.
    /// </summary>
    private static int ReadSignedBits(ReadOnlySpan<byte> src, ref int byteIdx, ref int bitOff, int bitWidth)
    {
        // The decomp reads a 24-bit window and shifts; we replicate that
        // behaviour to handle bit widths up to 15 cleanly.
        var window =
            ((uint)src[byteIdx] << 16)
            | ((uint)src[byteIdx + 1] << 8)
            | src[byteIdx + 2];

        var shifted = (window << (bitOff + 8)) >> (32 - bitWidth);
        var value = (int)shifted;

        // Sign-extend from bitWidth.
        var signBit = 1 << (bitWidth - 1);
        if ((value & signBit) != 0)
            value |= -1 << bitWidth;

        var nextBit = bitOff + bitWidth;
        byteIdx += nextBit >> 3;
        bitOff = nextBit & 7;
        return value;
    }

    private static int ReadInt16Le(ReadOnlySpan<byte> src, int idx)
    {
        return (short)(src[idx] | (src[idx + 1] << 8));
    }
}
