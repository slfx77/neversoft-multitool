namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Partial, decomp-backed port of <c>FUN_80299DC0</c>, the class-2
///     B-frame/S-VOP dispatcher. Unsupported direct/field branches fall back
///     to the previous safe behavior: copy the current reference macroblock.
/// </summary>
internal static class Vid1BFrameDecoder
{
    private const int MbFlagsOffset = 0xDC;
    private const int MotionVectorOffsetBase = 0x08;
    private const int MotionVectorStride = 0x08;

    private static readonly byte[] ZigzagScan = Vid1CoefficientDecoder.GetScanTable("zigzag");

    internal readonly record struct DecodeStats(
        int DecodedMacroblocks,
        int FallbackMacroblocks,
        int UnsupportedBranches,
        int FieldOrGmcBranches);

    private sealed class VectorState
    {
        public int ForwardX { get; set; }
        public int ForwardY { get; set; }
        public int BackwardX { get; set; }
        public int BackwardY { get; set; }
    }

    private readonly record struct BControl(
        int Mode,
        int Cbp,
        int Quantizer,
        int Flags,
        bool Supported,
        bool DirectDeltaCoded,
        bool CopyPreviousReference);

    public static DecodeStats DecodeFrame(
        Vid1VideoFrame frame,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        Vid1FrameContext ctx)
    {
        var decoded = 0;
        var fallback = 0;
        var unsupportedBranches = 0;
        var fieldOrGmcBranches = 0;
        var quantizer = frame.Quantizer;
        var vectorState = new VectorState();

        for (var mbY = 0; mbY < ctx.MbRows; mbY++)
        {
            vectorState.ForwardX = 0;
            vectorState.ForwardY = 0;
            vectorState.BackwardX = 0;
            vectorState.BackwardY = 0;

            for (var mbX = 0; mbX < ctx.MbCols; mbX++)
            {
                try
                {
                    var control = ReadControl(vlcReader, flagReader, ctx, mbX, mbY, quantizer);
                    quantizer = control.Quantizer;
                    ctx.CurrentQuantizer = quantizer;

                    if (!control.Supported)
                    {
                        unsupportedBranches++;
                        if ((control.Flags & 0x0C) != 0)
                            fieldOrGmcBranches++;
                        CopyRemainingFromReference(ctx, mbX, mbY, ref decoded, ref fallback);
                        return new DecodeStats(decoded, fallback, unsupportedBranches, fieldOrGmcBranches);
                    }

                    switch (control.Mode)
                    {
                        case 0:
                            if (control.CopyPreviousReference)
                            {
                                CopyMacroblockFromReference(ctx, mbX, mbY, usePreviousReference: true);
                                fallback++;
                            }
                            else
                            {
                                DecodeDirectMacroblock(
                                    vlcReader,
                                    ctx,
                                    frame,
                                    mbX,
                                    mbY,
                                    control.Cbp,
                                    control.Flags,
                                    control.DirectDeltaCoded);
                            }

                            decoded++;
                            break;

                        case 1:
                            DecodeBidirectionalMacroblock(
                                vlcReader,
                                ctx,
                                mbX,
                                mbY,
                                control.Cbp,
                                control.Flags,
                                vectorState,
                                Math.Max(frame.ForwardCode ?? 1, 1),
                                Math.Max(frame.BackwardCode ?? 1, 1));
                            decoded++;
                            break;

                        case 2:
                            DecodeSingleReferenceMacroblock(
                                vlcReader,
                                ctx,
                                mbX,
                                mbY,
                                control.Cbp,
                                control.Flags,
                                usePreviousReference: false,
                                vectorState,
                                Math.Max(frame.BackwardCode ?? 1, 1));
                            decoded++;
                            break;

                        case 3:
                            DecodeSingleReferenceMacroblock(
                                vlcReader,
                                ctx,
                                mbX,
                                mbY,
                                control.Cbp,
                                control.Flags,
                                usePreviousReference: true,
                                vectorState,
                                Math.Max(frame.ForwardCode ?? 1, 1));
                            decoded++;
                            break;

                        default:
                            unsupportedBranches++;
                            CopyRemainingFromReference(ctx, mbX, mbY, ref decoded, ref fallback);
                            return new DecodeStats(decoded, fallback, unsupportedBranches, fieldOrGmcBranches);
                    }
                }
                catch (EndOfStreamException)
                {
                    CopyRemainingFromReference(ctx, mbX, mbY, ref decoded, ref fallback);
                    return new DecodeStats(decoded, fallback, unsupportedBranches, fieldOrGmcBranches);
                }
                catch (InvalidDataException)
                {
                    CopyRemainingFromReference(ctx, mbX, mbY, ref decoded, ref fallback);
                    return new DecodeStats(decoded, fallback, unsupportedBranches, fieldOrGmcBranches);
                }
            }
        }

        return new DecodeStats(decoded, fallback, unsupportedBranches, fieldOrGmcBranches);
    }

    private static BControl ReadControl(
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int currentQuantizer)
    {
        var refMbBase = GetMbStateOffset(ctx, mbX, mbY);
        if (ctx.ReferenceMbState[refMbBase] == 0x10)
        {
            return new BControl(
                Mode: 0,
                Cbp: 0,
                currentQuantizer,
                Flags: 3,
                Supported: true,
                DirectDeltaCoded: false,
                CopyPreviousReference: true);
        }

        var gate = flagReader.ReadBits(1);
        if (gate != 0)
        {
            return new BControl(
                Mode: 0,
                Cbp: 0,
                currentQuantizer,
                Flags: 0,
                Supported: true,
                DirectDeltaCoded: false,
                CopyPreviousReference: false);
        }

        var hasCbp = flagReader.ReadBits(1) == 0;
        var mode = ReadBFrameMode(vlcReader);
        var cbp = hasCbp ? flagReader.ReadBits(6) : 0;
        var quantizer = currentQuantizer;
        var flags = mode;

        if ((mode & 3) != 0 && hasCbp)
            quantizer = ClampQuantizer(quantizer + ReadBFrameQuantDelta(flagReader));

        if (ctx.GmcEnabled)
        {
            if (cbp != 0 && flagReader.ReadBits(1) != 0)
                flags |= 0x04;
            if ((mode & 3) != 0 && flagReader.ReadBits(1) != 0)
                flags |= 0x08 | (flagReader.ReadBits(2) << 4);
        }

        var lowerMode = mode & 3;
        var supported =
            (flags & 0x08) == 0 &&
            (flags & 0x04) == 0 &&
            lowerMode is >= 0 and <= 3;

        return new BControl(
            lowerMode,
            cbp,
            quantizer,
            flags,
            supported,
            DirectDeltaCoded: lowerMode == 0,
            CopyPreviousReference: false);
    }

    private static int ReadBFrameMode(Vid1BitReader reader)
    {
        var mode = 0;
        while (mode < 4 && reader.ReadBits(1) == 0)
            mode++;
        return mode;
    }

    private static int ReadBFrameQuantDelta(Vid1BitReader reader)
    {
        if (reader.ReadBits(1) == 0)
            return 0;

        return reader.ReadBits(1) == 0 ? -2 : 2;
    }

    private static void DecodeDirectMacroblock(
        Vid1BitReader vlcReader,
        Vid1FrameContext ctx,
        Vid1VideoFrame frame,
        int mbX,
        int mbY,
        int cbp,
        int flags,
        bool directDeltaCoded)
    {
        if (!TryGetDirectTiming(ctx, frame, out var bDelta, out var referenceDelta))
        {
            Vid1MacroblockDecoder.CopyMacroblockFromReference(ctx, mbX, mbY);
            return;
        }

        var mbBase = InitializeBMacroblockState(ctx, mbX, mbY, flags);
        ctx.MbState[mbBase] = 2;

        var directDeltaX = 0;
        var directDeltaY = 0;
        if (directDeltaCoded)
            (directDeltaX, directDeltaY) = DecodeMotionVectorPair(vlcReader, fCode: 1, predictorX: 0, predictorY: 0);

        var refMbBase = GetMbStateOffset(ctx, mbX, mbY);
        Span<int> forwardX = stackalloc int[4];
        Span<int> forwardY = stackalloc int[4];
        Span<int> backwardX = stackalloc int[4];
        Span<int> backwardY = stackalloc int[4];

        for (var block = 0; block < 4; block++)
        {
            var refMvOffset = refMbBase + MotionVectorOffsetBase + block * MotionVectorStride;
            var futureX = ReadInt32(ctx.ReferenceMbState, refMvOffset);
            var futureY = ReadInt32(ctx.ReferenceMbState, refMvOffset + 4);

            forwardX[block] = DivideTowardsZero(bDelta * futureX, referenceDelta) + directDeltaX;
            forwardY[block] = DivideTowardsZero(bDelta * futureY, referenceDelta) + directDeltaY;
            backwardX[block] = directDeltaX == 0
                ? DivideTowardsZero((bDelta - referenceDelta) * futureX, referenceDelta)
                : forwardX[block] - futureX;
            backwardY[block] = directDeltaY == 0
                ? DivideTowardsZero((bDelta - referenceDelta) * futureY, referenceDelta)
                : forwardY[block] - futureY;

            WriteMotionVector(ctx.MbState, mbBase + MotionVectorOffsetBase + block * MotionVectorStride, forwardX[block], forwardY[block]);
            WriteMotionVector(ctx.MbState, mbBase + 0xE0 + block * MotionVectorStride, backwardX[block], backwardY[block]);
        }

        var forwardChroma = ComputeFourMotionChromaVector(forwardX, forwardY);
        var backwardChroma = ComputeFourMotionChromaVector(backwardX, backwardY);

        Span<short> residual = stackalloc short[64];
        for (var block = 0; block < 6; block++)
        {
            DecodeResidualBlock(vlcReader, ctx, cbp, block, residual);
            var (fx, fy) = block < 4 ? (forwardX[block], forwardY[block]) : forwardChroma;
            var (bx, by) = block < 4 ? (backwardX[block], backwardY[block]) : backwardChroma;
            WriteBidirectionalBlock(ctx, mbX, mbY, block, fx, fy, bx, by, residual);
        }
    }

    private static bool TryGetDirectTiming(Vid1FrameContext ctx, Vid1VideoFrame frame, out int bDelta, out int referenceDelta)
    {
        bDelta = 0;
        referenceDelta = 0;
        if (!frame.AlternateFrameStateWord.HasValue)
            return false;

        var previous = unchecked((int)ctx.PreviousReferenceStateWord);
        var current = unchecked((int)frame.AlternateFrameStateWord.Value);
        var next = unchecked((int)ctx.ReferenceStateWord);
        bDelta = current - previous;
        referenceDelta = next - previous;
        return referenceDelta != 0;
    }

    private static (int X, int Y) ComputeFourMotionChromaVector(ReadOnlySpan<int> x, ReadOnlySpan<int> y)
    {
        var sumX = x[0] + x[1] + x[2] + x[3];
        var sumY = y[0] + y[1] + y[2] + y[3];
        return (
            Vid1MacroblockDecoder.RoundFourMotionChroma(sumX),
            Vid1MacroblockDecoder.RoundFourMotionChroma(sumY));
    }

    private static int DivideTowardsZero(int numerator, int denominator)
        => numerator / denominator;

    private static void DecodeSingleReferenceMacroblock(
        Vid1BitReader vlcReader,
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int cbp,
        int flags,
        bool usePreviousReference,
        VectorState vectorState,
        int fCode)
    {
        var mbBase = InitializeBMacroblockState(ctx, mbX, mbY, flags);
        var predictorX = usePreviousReference ? vectorState.ForwardX : vectorState.BackwardX;
        var predictorY = usePreviousReference ? vectorState.ForwardY : vectorState.BackwardY;
        var (mvX, mvY) = DecodeMotionVectorPair(vlcReader, fCode, predictorX, predictorY);

        if (usePreviousReference)
        {
            vectorState.ForwardX = mvX;
            vectorState.ForwardY = mvY;
        }
        else
        {
            vectorState.BackwardX = mvX;
            vectorState.BackwardY = mvY;
        }

        for (var block = 0; block < 4; block++)
            WriteMotionVector(ctx.MbState, mbBase + MotionVectorOffsetBase + block * MotionVectorStride, mvX, mvY);

        Span<short> residual = stackalloc short[64];
        for (var block = 0; block < 6; block++)
        {
            DecodeResidualBlock(vlcReader, ctx, cbp, block, residual);
            var plane = GetPlane(ctx, block, usePreviousReference);
            var dst = GetBlockDestination(mbX, mbY, block);
            var (blockMvX, blockMvY) = block < 4
                ? (mvX, mvY)
                : ComputeChromaMotionVector(mvX, mvY);

            Vid1MotionComp.PredictInterBlock(
                plane.RefPlane,
                plane.Stride,
                plane.Width,
                plane.Height,
                dst.X + (blockMvX >> 1),
                dst.Y + (blockMvY >> 1),
                blockMvX & 1,
                blockMvY & 1,
                residual,
                plane.OutPlane,
                plane.Stride,
                dst.X,
                dst.Y,
                ctx.SubpixelRoundingBias);
        }
    }

    private static void DecodeBidirectionalMacroblock(
        Vid1BitReader vlcReader,
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int cbp,
        int flags,
        VectorState vectorState,
        int forwardFCode,
        int backwardFCode)
    {
        var mbBase = InitializeBMacroblockState(ctx, mbX, mbY, flags);
        var (forwardX, forwardY) = DecodeMotionVectorPair(
            vlcReader,
            forwardFCode,
            vectorState.ForwardX,
            vectorState.ForwardY);
        var (backwardX, backwardY) = DecodeMotionVectorPair(
            vlcReader,
            backwardFCode,
            vectorState.BackwardX,
            vectorState.BackwardY);

        vectorState.ForwardX = forwardX;
        vectorState.ForwardY = forwardY;
        vectorState.BackwardX = backwardX;
        vectorState.BackwardY = backwardY;

        for (var block = 0; block < 4; block++)
            WriteMotionVector(ctx.MbState, mbBase + MotionVectorOffsetBase + block * MotionVectorStride, forwardX, forwardY);

        Span<short> residual = stackalloc short[64];
        for (var block = 0; block < 6; block++)
        {
            DecodeResidualBlock(vlcReader, ctx, cbp, block, residual);
            var (forwardBlockX, forwardBlockY) = block < 4
                ? (forwardX, forwardY)
                : ComputeChromaMotionVector(forwardX, forwardY);
            var (backwardBlockX, backwardBlockY) = block < 4
                ? (backwardX, backwardY)
                : ComputeChromaMotionVector(backwardX, backwardY);

            WriteBidirectionalBlock(
                ctx,
                mbX,
                mbY,
                block,
                forwardBlockX,
                forwardBlockY,
                backwardBlockX,
                backwardBlockY,
                residual);
        }
    }

    private static void WriteBidirectionalBlock(
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int block,
        int forwardMvX,
        int forwardMvY,
        int backwardMvX,
        int backwardMvY,
        ReadOnlySpan<short> residual)
    {
        var dst = GetBlockDestination(mbX, mbY, block);
        var outPlane = GetPlane(ctx, block, usePreviousReference: false);
        var forwardPlane = GetPlane(ctx, block, usePreviousReference: true);
        var backwardPlane = GetPlane(ctx, block, usePreviousReference: false);
        Span<short> averaged = stackalloc short[64];

        PredictAveragedBlock(
            forwardPlane.RefPlane,
            backwardPlane.RefPlane,
            outPlane.Stride,
            outPlane.Width,
            outPlane.Height,
            dst.X,
            dst.Y,
            forwardMvX,
            forwardMvY,
            backwardMvX,
            backwardMvY,
            residual,
            averaged,
            ctx.SubpixelRoundingBias);

        Vid1MotionComp.WriteIntraBlock(
            averaged,
            outPlane.OutPlane,
            outPlane.Stride,
            dst.X,
            dst.Y,
            sampleOffset: 0);
    }

    private static void DecodeResidualBlock(
        Vid1BitReader vlcReader,
        Vid1FrameContext ctx,
        int cbp,
        int block,
        Span<short> residual)
    {
        Span<short> quant = stackalloc short[64];
        residual.Clear();

        if ((cbp & (1 << (5 - block))) != 0)
            Vid1CoefficientDecoder.DecodeInterBlock(vlcReader, ZigzagScan, quant);

        if (ctx.UseIntraDequant)
            Vid1Dequant.DequantInterResidual(residual, quant, ctx.CurrentQuantizer, ctx.InterMatrix);
        else
            Vid1Dequant.DequantInterResidual(residual, quant, ctx.CurrentQuantizer);

        Vid1Idct.Transform(residual);
    }

    private static void PredictAveragedBlock(
        byte[] forwardRef,
        byte[] backwardRef,
        int stride,
        int width,
        int height,
        int dstX,
        int dstY,
        int forwardMvX,
        int forwardMvY,
        int backwardMvX,
        int backwardMvY,
        ReadOnlySpan<short> residual,
        Span<short> output,
        int roundingBias)
    {
        Span<byte> forward = stackalloc byte[64];
        Span<byte> backward = stackalloc byte[64];
        Span<short> zeroResidual = stackalloc short[64];

        Vid1MotionComp.PredictInterBlockToSpan(
            forwardRef,
            stride,
            width,
            height,
            dstX + (forwardMvX >> 1),
            dstY + (forwardMvY >> 1),
            forwardMvX & 1,
            forwardMvY & 1,
            zeroResidual,
            forward,
            8,
            roundingBias);
        Vid1MotionComp.PredictInterBlockToSpan(
            backwardRef,
            stride,
            width,
            height,
            dstX + (backwardMvX >> 1),
            dstY + (backwardMvY >> 1),
            backwardMvX & 1,
            backwardMvY & 1,
            zeroResidual,
            backward,
            8,
            roundingBias);

        for (var i = 0; i < 64; i++)
            output[i] = (short)(((forward[i] + backward[i] + 1) >> 1) + residual[i]);
    }

    private static int InitializeBMacroblockState(Vid1FrameContext ctx, int mbX, int mbY, int flags)
    {
        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        Array.Clear(ctx.MbState, mbBase, Vid1FrameContext.MbStateStride);
        ctx.MbState[mbBase] = 0;
        ctx.MbState[mbBase + 1] = (byte)(ctx.CurrentQuantizer & 0xFF);
        ctx.MbState[mbBase + MbFlagsOffset] = (byte)(flags & 0xFF);
        return mbBase;
    }

    private static void CopyRemainingFromReference(
        Vid1FrameContext ctx,
        int startMbX,
        int startMbY,
        ref int decoded,
        ref int fallback)
    {
        for (var mbY = startMbY; mbY < ctx.MbRows; mbY++)
        {
            var xStart = mbY == startMbY ? startMbX : 0;
            for (var mbX = xStart; mbX < ctx.MbCols; mbX++)
            {
                Vid1MacroblockDecoder.CopyMacroblockFromReference(ctx, mbX, mbY);
                decoded++;
                fallback++;
            }
        }
    }

    private static void CopyMacroblockFromReference(Vid1FrameContext ctx, int mbX, int mbY, bool usePreviousReference)
    {
        var mbBase = InitializeBMacroblockState(ctx, mbX, mbY, flags: 3);
        ctx.MbState[mbBase + 1] = ctx.ReferenceMbState[mbBase + 1];

        var lumaX = mbX * 16;
        var lumaY = mbY * 16;
        var sourceY = usePreviousReference ? ctx.PreviousReferenceY : ctx.ReferenceY;
        for (var row = 0; row < 16; row++)
        {
            var offset = ((lumaY + row) * ctx.Width) + lumaX;
            Buffer.BlockCopy(sourceY, offset, ctx.OutputY, offset, 16);
        }

        var chromaX = mbX * 8;
        var chromaY = mbY * 8;
        var sourceCb = usePreviousReference ? ctx.PreviousReferenceCb : ctx.ReferenceCb;
        var sourceCr = usePreviousReference ? ctx.PreviousReferenceCr : ctx.ReferenceCr;
        for (var row = 0; row < 8; row++)
        {
            var offset = ((chromaY + row) * ctx.ChromaWidth) + chromaX;
            Buffer.BlockCopy(sourceCb, offset, ctx.OutputCb, offset, 8);
            Buffer.BlockCopy(sourceCr, offset, ctx.OutputCr, offset, 8);
        }
    }

    private static (byte[] RefPlane, byte[] OutPlane, int Stride, int Width, int Height) GetPlane(
        Vid1FrameContext ctx,
        int block,
        bool usePreviousReference)
    {
        if (block < 4)
        {
            return (
                usePreviousReference ? ctx.PreviousReferenceY : ctx.ReferenceY,
                ctx.OutputY,
                ctx.Width,
                ctx.Width,
                ctx.Height);
        }

        if (block == 4)
        {
            return (
                usePreviousReference ? ctx.PreviousReferenceCb : ctx.ReferenceCb,
                ctx.OutputCb,
                ctx.ChromaWidth,
                ctx.ChromaWidth,
                ctx.ChromaHeight);
        }

        return (
            usePreviousReference ? ctx.PreviousReferenceCr : ctx.ReferenceCr,
            ctx.OutputCr,
            ctx.ChromaWidth,
            ctx.ChromaWidth,
            ctx.ChromaHeight);
    }

    private static (int X, int Y) GetBlockDestination(int mbX, int mbY, int block)
    {
        if (block < 4)
            return (mbX * 16 + ((block & 1) * 8), mbY * 16 + ((block >> 1) * 8));

        return (mbX * 8, mbY * 8);
    }

    private static (int X, int Y) DecodeMotionVectorPair(
        Vid1BitReader reader,
        int fCode,
        int predictorX,
        int predictorY)
    {
        var (min, max, wrap) = GetMotionBounds(fCode);

        var mvX = predictorX + Vid1VlcDecoder.DecodeMvDelta(reader, fCode);
        if (mvX < min)
            mvX += wrap;
        else if (mvX > max)
            mvX -= wrap;

        var mvY = predictorY + Vid1VlcDecoder.DecodeMvDelta(reader, fCode);
        if (mvY < min)
            mvY += wrap;
        else if (mvY > max)
            mvY -= wrap;

        return (mvX, mvY);
    }

    private static void WriteMotionVector(byte[] state, int offset, int x, int y)
    {
        WriteInt32(state, offset, x);
        WriteInt32(state, offset + 4, y);
    }

    private static (int X, int Y) ComputeChromaMotionVector(int mvX, int mvY)
        => (RoundHalfChroma(mvX), RoundHalfChroma(mvY));

    private static int RoundHalfChroma(int value)
        => (value & 3) == 0 ? value / 2 : (value >> 1) | 1;

    private static (int Min, int Max, int Wrap) GetMotionBounds(int fCode)
    {
        var rangeUnit = 1 << Math.Max(fCode - 1, 0);
        return (-rangeUnit * 0x20, rangeUnit * 0x20 - 1, rangeUnit * 0x40);
    }

    private static int ClampQuantizer(int value)
    {
        if (value < 1) return 1;
        if (value > 0x1F) return 0x1F;
        return value;
    }

    private static int GetMbStateOffset(Vid1FrameContext ctx, int mbX, int mbY)
        => (mbY * ctx.MbCols + mbX) * Vid1FrameContext.MbStateStride;

    private static int ReadInt32(byte[] buffer, int offset)
        => (buffer[offset] << 24) |
           (buffer[offset + 1] << 16) |
           (buffer[offset + 2] << 8) |
           buffer[offset + 3];

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

}
