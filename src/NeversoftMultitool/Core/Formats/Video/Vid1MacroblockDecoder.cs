namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Per-macroblock decode orchestrator for Factor 5 M4Decoder.
///     A simplified first-pass port of <c>FUN_8029A878</c> from the THAW GC
///     DOL: iterates the 6 blocks of a 16×16 macroblock (4 luma + 2 chroma),
///     runs the <c>coefficient decode → dequantize → IDCT → prediction+residual</c>
///     pipeline for each, and writes the result into the output YUV planes.
/// </summary>
/// <remarks>
///     The following are intentionally simplified in this first pass; see
///     the plan file for the refinement backlog:
///     <list type="bullet">
///         <item>Motion vector decode — inter macroblocks use a zero motion vector
///               (prediction = reference block at the same position).</item>
///         <item>GMC sprite warping — stubbed to identity.</item>
///         <item>CBP extras (<c>FUN_8029C214</c> / <c>FUN_8029CE08</c>) — skipped;
///               we rely solely on <c>control.ControlWord</c> as the CBP mask.</item>
///     </list>
/// </remarks>
internal static class Vid1MacroblockDecoder
{
    private const int MbFlagsOffset = 0xDC;
    private const int MotionVectorOffsetBase = 0x08;
    private const int MotionVectorStride = 0x08;

    private static readonly byte[] ZigzagScan = Vid1CoefficientDecoder.GetScanTable("zigzag");
    private static readonly byte[] HorizontalScan = Vid1CoefficientDecoder.GetScanTable("horizontal");
    private static readonly byte[] VerticalScan = Vid1CoefficientDecoder.GetScanTable("vertical");
    private static readonly int[] FourMotionChromaMvRoundingTable =
    [
        0, 0, 0, 1,
        1, 1, 1, 1,
        1, 1, 1, 1,
        1, 1, 2, 2,
    ];

    public static void Decode(
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        Vid1ControlProbe control,
        Vid1FrameContext context,
        int mbX,
        int mbY)
    {
        if (control.Stage == Vid1ControlStage.SpriteWarp)
        {
            DecodeSpriteWarpMacroblock(vlcReader, control, context, mbX, mbY);
            return;
        }

        if (control.Stage == Vid1ControlStage.Special)
        {
            CopyMacroblockFromReference(context, mbX, mbY);
            return;
        }

        if (control.Stage == Vid1ControlStage.Motion)
        {
            DecodeMotionMacroblock(vlcReader, control, context, mbX, mbY);
            return;
        }

        DecodeBlockPipeline(vlcReader, flagReader, control, context, mbX, mbY);
    }

    internal static void CopyMacroblockFromReference(Vid1FrameContext ctx, int mbX, int mbY)
    {
        MarkSkippedMacroblock(ctx, mbX, mbY);

        // 16×16 luma
        var lumaX = mbX * 16;
        var lumaY = mbY * 16;
        for (var row = 0; row < 16; row++)
        {
            var srcOffset = ((lumaY + row) * ctx.Width) + lumaX;
            Buffer.BlockCopy(ctx.ReferenceY, srcOffset, ctx.OutputY, srcOffset, 16);
        }

        // 8×8 chroma (each plane)
        var chromaX = mbX * 8;
        var chromaY = mbY * 8;
        for (var row = 0; row < 8; row++)
        {
            var srcOffset = ((chromaY + row) * ctx.ChromaWidth) + chromaX;
            Buffer.BlockCopy(ctx.ReferenceCb, srcOffset, ctx.OutputCb, srcOffset, 8);
            Buffer.BlockCopy(ctx.ReferenceCr, srcOffset, ctx.OutputCr, srcOffset, 8);
        }
    }

    private static void MarkSkippedMacroblock(Vid1FrameContext ctx, int mbX, int mbY)
    {
        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        Array.Clear(ctx.MbState, mbBase, Vid1FrameContext.MbStateStride);
        ctx.MbState[mbBase] = 0x10;
        ctx.MbState[mbBase + 1] = (byte)(ctx.CurrentQuantizer & 0xFF);
    }

    private static void DecodeBlockPipeline(
        Vid1BitReader vlcReader, Vid1BitReader flagReader, Vid1ControlProbe control,
        Vid1FrameContext ctx, int mbX, int mbY)
    {
        ctx.CurrentQuantizer = control.Quantizer;
        InitializeMacroblockState(ctx, mbX, mbY, control.MacroblockType, control.Quantizer);
        var isIntraMacroblock = control.Stage == Vid1ControlStage.A878;

        var cbp = control.ControlWord & 0x3F;
        var dcPreDecode = ctx.CurrentQuantizer < ctx.IntraDcThreshold;
        var useIntraDequant = ctx.UseIntraDequant;
        var predictionMode = GetA878PredictionMode();
        var scanMode = GetA878ScanMode();
        var configuredIntraOffset = GetConfiguredIntraWriteOffset();

        // Pre-GMC feature bit (FUN_8029A878:645) is still not consumed here.
        // The DOL gates that read on established sprite-anchor state in the
        // class-3 path; we do not yet model the exact enable condition.

        Span<short> quant = stackalloc short[64];
        Span<short> dequant = stackalloc short[64];
        Span<short> predictions = stackalloc short[8];
        for (var block = 0; block < 6; block++)
        {
            quant.Clear();
            predictions.Clear();

            var isLuma = block < 4;
            var dcScale = ComputeDcScale(ctx.CurrentQuantizer, isLuma);
            var usePrediction = ShouldApplyA878Prediction(predictionMode, isIntraMacroblock, block);
            var scanTableIndex = 0;
            if (usePrediction)
            {
                scanTableIndex = Vid1Prediction.ComputePredictions(
                    ctx,
                    mbX,
                    mbY,
                    block,
                    ctx.CurrentQuantizer,
                    dcScale,
                    predictions);

                if (control.FeatureBit == 0)
                {
                    Vid1Prediction.ForceZigzagScan(ctx, mbX, mbY, block);
                    scanTableIndex = 0;
                }

                if (scanMode is "zigzag" or "horizontal" or "vertical")
                {
                    scanTableIndex = scanMode switch
                    {
                        "horizontal" => 1,
                        "vertical" => 2,
                        _ => 0,
                    };
                    ctx.MbState[GetMbStateOffset(ctx, mbX, mbY) + 2 + block] = (byte)scanTableIndex;
                }
            }

            var scanTable = GetScanTable(scanTableIndex);

            var startIndex = 0;
            if (dcPreDecode)
            {
                var dcSize = Vid1IntraDc.DecodeSize(vlcReader, isLuma);
                var dcValue = dcSize == 0 ? 0 : Vid1IntraDc.DecodeValue(vlcReader, dcSize);
                if (dcSize > 8)
                    flagReader.SkipBits(1);
                quant[0] = (short)dcValue;
                startIndex = 1;
            }

            var isCoded = (cbp & (1 << (5 - block))) != 0;
            if (isCoded)
            {
                Vid1CoefficientDecoder.DecodeBlock(
                    vlcReader, useBundleB: true, scanTable, quant, startIndex: startIndex);
            }

            if (usePrediction)
            {
                Vid1Prediction.ApplyAndStorePredictions(
                    ctx,
                    mbX,
                    mbY,
                    block,
                    dcScale,
                    predictions,
                    quant);
            }

            if (useIntraDequant)
                Vid1Dequant.DequantIntra(dequant, quant, ctx.CurrentQuantizer, dcScale, ctx.IntraMatrix);
            else
                Vid1Dequant.DequantInter(dequant, quant, ctx.CurrentQuantizer, dcScale);

            Vid1Idct.Transform(dequant);

            // Intra/inter reconstruction is a macroblock-type decision, not a
            // side effect of whether DC arrived through the pre-decode path.
            var intraWriteOffset = configuredIntraOffset ?? 0;
            WriteBlockToPlane(ctx, dequant, mbX, mbY, block, intra: isIntraMacroblock, intraWriteOffset);
        }
    }

    private static void DecodeMotionMacroblock(
        Vid1BitReader vlcReader,
        Vid1ControlProbe control,
        Vid1FrameContext ctx,
        int mbX,
        int mbY)
    {
        ctx.CurrentQuantizer = control.Quantizer;
        InitializeMacroblockState(ctx, mbX, mbY, control.MacroblockType, control.Quantizer);

        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        // The DOL routes mb[0xDC] & 0x08 through BACC/A1410 field prediction.
        // Keep this opt-in until the linear-plane port is score-validated.
        var fieldPrediction = ShouldEnableFieldPrediction() && (control.BlockFlags & 0x08) != 0;
        var effectiveBlockFlags = fieldPrediction ? control.BlockFlags : control.BlockFlags & ~0x08;
        ctx.MbState[mbBase + MbFlagsOffset] = (byte)(effectiveBlockFlags & 0xFF);
        DecodeMotionVectors(
            vlcReader,
            ctx,
            mbX,
            mbY,
            control.MacroblockType,
            effectiveBlockFlags);

        if (fieldPrediction)
            PredictFieldMotionMacroblock(ctx, mbX, mbY);

        var cbp = control.ControlWord & 0x3F;
        Span<short> quant = stackalloc short[64];
        Span<short> dequant = stackalloc short[64];

        for (var block = 0; block < 6; block++)
        {
            quant.Clear();
            dequant.Clear();

            var isCoded = (cbp & (1 << (5 - block))) != 0;
            if (isCoded)
                Vid1CoefficientDecoder.DecodeInterBlock(vlcReader, ZigzagScan, quant);

            if (ctx.UseIntraDequant)
                Vid1Dequant.DequantInterResidual(dequant, quant, ctx.CurrentQuantizer, ctx.InterMatrix);
            else
                Vid1Dequant.DequantInterResidual(dequant, quant, ctx.CurrentQuantizer);

            Vid1Idct.Transform(dequant);
            if (fieldPrediction)
                AddResidualToWarpedBlock(ctx, dequant, mbX, mbY, block);
            else
                WriteMotionBlockToPlane(ctx, dequant, mbX, mbY, block, control.MacroblockType);
        }
    }

    private static void DecodeSpriteWarpMacroblock(
        Vid1BitReader vlcReader,
        Vid1ControlProbe control,
        Vid1FrameContext ctx,
        int mbX,
        int mbY)
    {
        ctx.CurrentQuantizer = control.Quantizer;
        InitializeMacroblockState(ctx, mbX, mbY, control.MacroblockType, control.Quantizer);

        WarpSpriteMacroblock(ctx, mbX, mbY);

        var cbp = control.ControlWord < 0 ? 0 : control.ControlWord & 0x3F;
        if (cbp == 0)
            return;

        Span<short> quant = stackalloc short[64];
        Span<short> dequant = stackalloc short[64];
        for (var block = 0; block < 6; block++)
        {
            var isCoded = (cbp & (1 << (5 - block))) != 0;
            if (!isCoded)
                continue;

            quant.Clear();
            dequant.Clear();
            Vid1CoefficientDecoder.DecodeInterBlock(vlcReader, ZigzagScan, quant);

            if (ctx.UseIntraDequant)
                Vid1Dequant.DequantInterResidual(dequant, quant, ctx.CurrentQuantizer, ctx.InterMatrix);
            else
                Vid1Dequant.DequantInterResidual(dequant, quant, ctx.CurrentQuantizer);

            Vid1Idct.Transform(dequant);
            AddResidualToWarpedBlock(ctx, dequant, mbX, mbY, block);
        }
    }

    private static void WriteBlockToPlane(
        Vid1FrameContext ctx, ReadOnlySpan<short> residual, int mbX, int mbY, int blockIndex, bool intra, int intraWriteOffset)
    {
        byte[] refPlane;
        byte[] outPlane;
        int stride, width, height;
        int dstX, dstY;

        if (blockIndex < 4)
        {
            // Luma: 2x2 arrangement within 16x16 macroblock
            refPlane = ctx.ReferenceY;
            outPlane = ctx.OutputY;
            stride = ctx.Width;
            width = ctx.Width;
            height = ctx.Height;
            dstX = mbX * 16 + ((blockIndex & 1) * 8);
            dstY = mbY * 16 + ((blockIndex >> 1) * 8);
        }
        else if (blockIndex == 4)
        {
            // Cb
            refPlane = ctx.ReferenceCb;
            outPlane = ctx.OutputCb;
            stride = ctx.ChromaWidth;
            width = ctx.ChromaWidth;
            height = ctx.ChromaHeight;
            dstX = mbX * 8;
            dstY = mbY * 8;
        }
        else
        {
            // Cr
            refPlane = ctx.ReferenceCr;
            outPlane = ctx.OutputCr;
            stride = ctx.ChromaWidth;
            width = ctx.ChromaWidth;
            height = ctx.ChromaHeight;
            dstX = mbX * 8;
            dstY = mbY * 8;
        }

        if (intra)
        {
            Vid1MotionComp.WriteIntraBlock(residual, outPlane, stride, dstX, dstY, intraWriteOffset);
        }
        else
        {
            // Inter with zero MV = reference at (dstX, dstY) + residual.
            Vid1MotionComp.PredictInterBlock(
                refPlane, stride, width, height,
                srcX: dstX, srcY: dstY, halfX: 0, halfY: 0,
                residual,
                outPlane, stride,
                dstX: dstX, dstY: dstY,
                roundingBias: ctx.SubpixelRoundingBias);
        }
    }

    private static void WriteMotionBlockToPlane(
        Vid1FrameContext ctx,
        ReadOnlySpan<short> residual,
        int mbX,
        int mbY,
        int blockIndex,
        int macroblockType)
    {
        byte[] refPlane;
        byte[] outPlane;
        int stride, width, height;
        int dstX, dstY;
        int mvX, mvY;

        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        if (blockIndex < 4)
        {
            refPlane = ctx.ReferenceY;
            outPlane = ctx.OutputY;
            stride = ctx.Width;
            width = ctx.Width;
            height = ctx.Height;
            dstX = mbX * 16 + ((blockIndex & 1) * 8);
            dstY = mbY * 16 + ((blockIndex >> 1) * 8);
            mvX = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + blockIndex * MotionVectorStride);
            mvY = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + blockIndex * MotionVectorStride + 4);
        }
        else
        {
            refPlane = blockIndex == 4 ? ctx.ReferenceCb : ctx.ReferenceCr;
            outPlane = blockIndex == 4 ? ctx.OutputCb : ctx.OutputCr;
            stride = ctx.ChromaWidth;
            width = ctx.ChromaWidth;
            height = ctx.ChromaHeight;
            dstX = mbX * 8;
            dstY = mbY * 8;
            (mvX, mvY) = ComputeChromaMotionVector(ctx, mbX, mbY, macroblockType);
        }

        Vid1MotionComp.PredictInterBlock(
            refPlane, stride, width, height,
            srcX: dstX + (mvX >> 1), srcY: dstY + (mvY >> 1),
            halfX: mvX & 1, halfY: mvY & 1,
            residual,
            outPlane, stride,
            dstX: dstX, dstY: dstY,
            roundingBias: ctx.SubpixelRoundingBias);
    }

    private static void WarpSpriteMacroblock(Vid1FrameContext ctx, int mbX, int mbY)
    {
        if (ctx.SpritePointCount > 0)
        {
            WarpSpriteAffineMacroblock(ctx, mbX, mbY);
            return;
        }

        var accuracy = Math.Clamp(ctx.SpriteWarpAccuracy, 0, 3);
        var luma = ComputeSpriteBlockSource(
            mbX * 16,
            mbY * 16,
            ctx.SpriteLumaX,
            ctx.SpriteLumaY,
            accuracy,
            minCoord: -16,
            maxX: ctx.Width,
            maxY: ctx.Height);

        Vid1MotionComp.WarpSpriteBlock(
            ctx.ReferenceY, ctx.Width, ctx.Width, ctx.Height,
            luma.X, luma.Y, luma.FracX, luma.FracY,
            ctx.OutputY, ctx.Width,
            mbX * 16, mbY * 16,
            ctx.SubpixelRoundingBias);
        Vid1MotionComp.WarpSpriteBlock(
            ctx.ReferenceY, ctx.Width, ctx.Width, ctx.Height,
            luma.X + 8, luma.Y, luma.FracX, luma.FracY,
            ctx.OutputY, ctx.Width,
            mbX * 16 + 8, mbY * 16,
            ctx.SubpixelRoundingBias);
        Vid1MotionComp.WarpSpriteBlock(
            ctx.ReferenceY, ctx.Width, ctx.Width, ctx.Height,
            luma.X, luma.Y + 8, luma.FracX, luma.FracY,
            ctx.OutputY, ctx.Width,
            mbX * 16, mbY * 16 + 8,
            ctx.SubpixelRoundingBias);
        Vid1MotionComp.WarpSpriteBlock(
            ctx.ReferenceY, ctx.Width, ctx.Width, ctx.Height,
            luma.X + 8, luma.Y + 8, luma.FracX, luma.FracY,
            ctx.OutputY, ctx.Width,
            mbX * 16 + 8, mbY * 16 + 8,
            ctx.SubpixelRoundingBias);

        var chroma = ComputeSpriteBlockSource(
            mbX * 8,
            mbY * 8,
            ctx.SpriteChromaX,
            ctx.SpriteChromaY,
            accuracy,
            minCoord: -8,
            maxX: ctx.ChromaWidth,
            maxY: ctx.ChromaHeight);

        Vid1MotionComp.WarpSpriteBlock(
            ctx.ReferenceCb, ctx.ChromaWidth, ctx.ChromaWidth, ctx.ChromaHeight,
            chroma.X, chroma.Y, chroma.FracX, chroma.FracY,
            ctx.OutputCb, ctx.ChromaWidth,
            mbX * 8, mbY * 8,
            ctx.SubpixelRoundingBias);
        Vid1MotionComp.WarpSpriteBlock(
            ctx.ReferenceCr, ctx.ChromaWidth, ctx.ChromaWidth, ctx.ChromaHeight,
            chroma.X, chroma.Y, chroma.FracX, chroma.FracY,
            ctx.OutputCr, ctx.ChromaWidth,
            mbX * 8, mbY * 8,
            ctx.SubpixelRoundingBias);
    }

    private static void WarpSpriteAffineMacroblock(Vid1FrameContext ctx, int mbX, int mbY)
    {
        var fixedShift = Math.Clamp(ctx.SpriteWarpAccuracy, 0, 3) + 1;
        var lumaX = mbX * 16;
        var lumaY = mbY * 16;

        for (var block = 0; block < 4; block++)
        {
            var dstX = lumaX + ((block & 1) * 8);
            var dstY = lumaY + ((block >> 1) * 8);
            Vid1MotionComp.WarpSpriteAffineBlock(
                ctx.ReferenceY, ctx.Width, ctx.Width, ctx.Height,
                dstX, dstY,
                ctx.SpriteLumaX,
                ctx.SpriteLumaY,
                ctx.SpriteLumaScaleX,
                ctx.SpriteLumaCrossX,
                ctx.SpriteLumaCrossY,
                ctx.SpriteLumaScaleY,
                ctx.SpriteLumaTransformShift,
                fixedShift,
                ctx.OutputY, ctx.Width,
                ctx.SubpixelRoundingBias);
        }

        var chromaX = mbX * 8;
        var chromaY = mbY * 8;
        Vid1MotionComp.WarpSpriteAffineBlock(
            ctx.ReferenceCb, ctx.ChromaWidth, ctx.ChromaWidth, ctx.ChromaHeight,
            chromaX, chromaY,
            ctx.SpriteChromaX,
            ctx.SpriteChromaY,
            ctx.SpriteChromaScaleX,
            ctx.SpriteChromaCrossX,
            ctx.SpriteChromaCrossY,
            ctx.SpriteChromaScaleY,
            ctx.SpriteChromaTransformShift,
            fixedShift,
            ctx.OutputCb, ctx.ChromaWidth,
            ctx.SubpixelRoundingBias);
        Vid1MotionComp.WarpSpriteAffineBlock(
            ctx.ReferenceCr, ctx.ChromaWidth, ctx.ChromaWidth, ctx.ChromaHeight,
            chromaX, chromaY,
            ctx.SpriteChromaX,
            ctx.SpriteChromaY,
            ctx.SpriteChromaScaleX,
            ctx.SpriteChromaCrossX,
            ctx.SpriteChromaCrossY,
            ctx.SpriteChromaScaleY,
            ctx.SpriteChromaTransformShift,
            fixedShift,
            ctx.OutputCr, ctx.ChromaWidth,
            ctx.SubpixelRoundingBias);
    }

    private static void PredictFieldMotionMacroblock(Vid1FrameContext ctx, int mbX, int mbY)
    {
        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        var fieldSelectBits = (ctx.MbState[mbBase + MbFlagsOffset] >> 4) & 0x03;
        var firstMvX = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase);
        var firstMvY = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + 4);
        var secondMvX = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + MotionVectorStride);
        var secondMvY = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + MotionVectorStride + 4);

        Vid1MotionComp.PredictFieldBlock(
            ctx.ReferenceY, ctx.Width, ctx.Width, ctx.Height,
            ctx.OutputY, ctx.Width,
            mbX * 16, mbY * 16,
            blockWidth: 16,
            blockHeight: 16,
            firstMvX, firstMvY,
            secondMvX, secondMvY,
            fieldSelectBits,
            ctx.SubpixelRoundingBias);

        var (firstChromaX, firstChromaY) = ComputeFieldChromaMotionVector(firstMvX, firstMvY);
        var (secondChromaX, secondChromaY) = ComputeFieldChromaMotionVector(secondMvX, secondMvY);
        var chromaX = mbX * 8;
        var chromaY = mbY * 8;

        Vid1MotionComp.PredictFieldBlock(
            ctx.ReferenceCb, ctx.ChromaWidth, ctx.ChromaWidth, ctx.ChromaHeight,
            ctx.OutputCb, ctx.ChromaWidth,
            chromaX, chromaY,
            blockWidth: 8,
            blockHeight: 8,
            firstChromaX, firstChromaY,
            secondChromaX, secondChromaY,
            fieldSelectBits,
            ctx.SubpixelRoundingBias);
        Vid1MotionComp.PredictFieldBlock(
            ctx.ReferenceCr, ctx.ChromaWidth, ctx.ChromaWidth, ctx.ChromaHeight,
            ctx.OutputCr, ctx.ChromaWidth,
            chromaX, chromaY,
            blockWidth: 8,
            blockHeight: 8,
            firstChromaX, firstChromaY,
            secondChromaX, secondChromaY,
            fieldSelectBits,
            ctx.SubpixelRoundingBias);
    }

    internal static (int X, int Y, int FracX, int FracY) ComputeSpriteBlockSource(
        int baseX,
        int baseY,
        int offsetX,
        int offsetY,
        int accuracy,
        int minCoord,
        int maxX,
        int maxY)
    {
        var clampedAccuracy = Math.Clamp(accuracy, 0, 3);
        var sourceShift = clampedAccuracy + 1;
        var fracShift = 3 - clampedAccuracy;

        var sourceX = ClampSpriteCoordinate(baseX + (offsetX >> sourceShift), minCoord, maxX, out var clippedHighX);
        var sourceY = ClampSpriteCoordinate(baseY + (offsetY >> sourceShift), minCoord, maxY, out var clippedHighY);
        var fracX = clippedHighX ? 0 : (offsetX << fracShift) & 0xF;
        var fracY = clippedHighY ? 0 : (offsetY << fracShift) & 0xF;

        return (sourceX, sourceY, fracX, fracY);
    }

    private static int ClampSpriteCoordinate(int value, int minCoord, int maxCoord, out bool clippedHigh)
    {
        if (value < minCoord)
        {
            clippedHigh = false;
            return minCoord;
        }

        if (value < maxCoord)
        {
            clippedHigh = false;
            return value;
        }

        clippedHigh = true;
        return maxCoord;
    }

    private static void AddResidualToWarpedBlock(
        Vid1FrameContext ctx,
        ReadOnlySpan<short> residual,
        int mbX,
        int mbY,
        int blockIndex)
    {
        byte[] outPlane;
        int stride;
        int dstX;
        int dstY;

        if (blockIndex < 4)
        {
            outPlane = ctx.OutputY;
            stride = ctx.Width;
            dstX = mbX * 16 + ((blockIndex & 1) * 8);
            dstY = mbY * 16 + ((blockIndex >> 1) * 8);
        }
        else
        {
            outPlane = blockIndex == 4 ? ctx.OutputCb : ctx.OutputCr;
            stride = ctx.ChromaWidth;
            dstX = mbX * 8;
            dstY = mbY * 8;
        }

        Vid1MotionComp.AddResidualToBlock(residual, outPlane, stride, dstX, dstY);
    }

    private static void DecodeMotionVectors(
        Vid1BitReader reader,
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int macroblockType,
        int blockFlags)
    {
        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        var isFourMotionVectors = macroblockType == 2;
        var isFieldPrediction = (blockFlags & 0x08) != 0;

        Span<int> predictor = stackalloc int[2];
        ComputeMotionPredictor(ctx, mbX, mbY, 0, predictor);

        if (isFieldPrediction && !isFourMotionVectors)
        {
            var predictorYHalf = DivideByTwoTruncated(predictor[1]);
            DecodeMotionVectorPair(reader, ctx.ForwardFCode, predictor[0], predictor[1], ctx.MbState, mbBase + MotionVectorOffsetBase);
            DecodeMotionVectorPair(
                reader,
                ctx.ForwardFCode,
                predictor[0],
                predictor[1],
                ctx.MbState,
                mbBase + MotionVectorOffsetBase + MotionVectorStride);
            WriteInt32(
                ctx.MbState,
                mbBase + MotionVectorOffsetBase + 4,
                (ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + 4) + predictorYHalf) * 2);
            WriteInt32(
                ctx.MbState,
                mbBase + MotionVectorOffsetBase + MotionVectorStride + 4,
                (ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + MotionVectorStride + 4) + predictorYHalf) * 2);
            return;
        }

        DecodeMotionVectorPair(reader, ctx.ForwardFCode, predictor[0], predictor[1], ctx.MbState, mbBase + MotionVectorOffsetBase);

        if (!isFourMotionVectors)
        {
            var mvX = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase);
            var mvY = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + 4);
            for (var block = 1; block < 4; block++)
            {
                var offset = mbBase + MotionVectorOffsetBase + block * MotionVectorStride;
                WriteInt32(ctx.MbState, offset, mvX);
                WriteInt32(ctx.MbState, offset + 4, mvY);
            }

            return;
        }

        for (var block = 1; block < 4; block++)
        {
            predictor.Clear();
            ComputeMotionPredictor(ctx, mbX, mbY, block, predictor);
            DecodeMotionVectorPair(
                reader,
                ctx.ForwardFCode,
                predictor[0],
                predictor[1],
                ctx.MbState,
                mbBase + MotionVectorOffsetBase + block * MotionVectorStride);
        }
    }

    private static void ComputeMotionPredictor(
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int subBlock,
        Span<int> predictor)
    {
        predictor[0] = 0;
        predictor[1] = 0;

        var left = TryGetMotionPredictorCandidate(ctx, mbX, mbY, subBlock, 0, out var leftX, out var leftY);
        var top = TryGetMotionPredictorCandidate(ctx, mbX, mbY, subBlock, 1, out var topX, out var topY);
        var topRight = TryGetMotionPredictorCandidate(ctx, mbX, mbY, subBlock, 2, out var topRightX, out var topRightY);

        var count = 0;
        if (left) count++;
        if (top) count++;
        if (topRight) count++;

        if (count == 0)
            return;

        if (count == 1)
        {
            if (left)
            {
                predictor[0] = leftX;
                predictor[1] = leftY;
            }
            else if (top)
            {
                predictor[0] = topX;
                predictor[1] = topY;
            }
            else
            {
                predictor[0] = topRightX;
                predictor[1] = topRightY;
            }

            return;
        }

        predictor[0] = Median3(left ? leftX : 0, top ? topX : 0, topRight ? topRightX : 0);
        predictor[1] = Median3(left ? leftY : 0, top ? topY : 0, topRight ? topRightY : 0);
    }

    private static bool TryGetMotionPredictorCandidate(
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int subBlock,
        int candidateIndex,
        out int mvX,
        out int mvY)
    {
        mvX = 0;
        mvY = 0;

        int candidateMbX;
        int candidateMbY;
        int blockIndex;

        switch (subBlock)
        {
            case 0:
                if (candidateIndex == 0)
                {
                    candidateMbX = mbX - 1;
                    candidateMbY = mbY;
                    blockIndex = 1;
                }
                else if (candidateIndex == 1)
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY - 1;
                    blockIndex = 2;
                }
                else
                {
                    candidateMbX = mbX + 1;
                    candidateMbY = mbY - 1;
                    blockIndex = 2;
                }
                break;

            case 1:
                if (candidateIndex == 0)
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY;
                    blockIndex = 0;
                }
                else if (candidateIndex == 1)
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY - 1;
                    blockIndex = 3;
                }
                else
                {
                    candidateMbX = mbX + 1;
                    candidateMbY = mbY - 1;
                    blockIndex = 2;
                }
                break;

            case 2:
                if (candidateIndex == 0)
                {
                    candidateMbX = mbX - 1;
                    candidateMbY = mbY;
                    blockIndex = 3;
                }
                else if (candidateIndex == 1)
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY;
                    blockIndex = 0;
                }
                else
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY;
                    blockIndex = 1;
                }
                break;

            default:
                if (candidateIndex == 0)
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY;
                    blockIndex = 2;
                }
                else if (candidateIndex == 1)
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY;
                    blockIndex = 0;
                }
                else
                {
                    candidateMbX = mbX;
                    candidateMbY = mbY;
                    blockIndex = 1;
                }
                break;
        }

        if ((uint)candidateMbX >= (uint)ctx.MbCols || (uint)candidateMbY >= (uint)ctx.MbRows)
            return false;

        var candidateBase = GetMbStateOffset(ctx, candidateMbX, candidateMbY);
        if ((ctx.MbState[candidateBase + MbFlagsOffset] & 0x08) == 0)
        {
            var offset = candidateBase + MotionVectorOffsetBase + blockIndex * MotionVectorStride;
            mvX = ReadInt32(ctx.MbState, offset);
            mvY = ReadInt32(ctx.MbState, offset + 4);
            return true;
        }

        mvX = RoundHalvedOdd(
            ReadInt32(ctx.MbState, candidateBase + MotionVectorOffsetBase) +
            ReadInt32(ctx.MbState, candidateBase + MotionVectorOffsetBase + MotionVectorStride));
        mvY = RoundHalvedOdd(
            ReadInt32(ctx.MbState, candidateBase + MotionVectorOffsetBase + 4) +
            ReadInt32(ctx.MbState, candidateBase + MotionVectorOffsetBase + MotionVectorStride + 4));
        return true;
    }

    private static void DecodeMotionVectorPair(
        Vid1BitReader reader,
        int fCode,
        int predictorX,
        int predictorY,
        byte[] state,
        int offset)
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

        WriteInt32(state, offset, mvX);
        WriteInt32(state, offset + 4, mvY);
    }

    private static (int X, int Y) ComputeChromaMotionVector(Vid1FrameContext ctx, int mbX, int mbY, int macroblockType)
    {
        var mbBase = GetMbStateOffset(ctx, mbX, mbY);

        if (macroblockType < 2)
        {
            var mvX = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase);
            var mvY = ReadInt32(ctx.MbState, mbBase + MotionVectorOffsetBase + 4);
            return (RoundHalfChroma(mvX), RoundHalfChroma(mvY));
        }

        var sumX = 0;
        var sumY = 0;
        for (var block = 0; block < 4; block++)
        {
            var offset = mbBase + MotionVectorOffsetBase + block * MotionVectorStride;
            sumX += ReadInt32(ctx.MbState, offset);
            sumY += ReadInt32(ctx.MbState, offset + 4);
        }

        return (RoundFourMotionChroma(sumX), RoundFourMotionChroma(sumY));
    }

    private static void InitializeMacroblockState(Vid1FrameContext ctx, int mbX, int mbY, int macroblockType, int quantizer)
    {
        var mbBase = GetMbStateOffset(ctx, mbX, mbY);
        ctx.MbState[mbBase] = (byte)(macroblockType & 0xFF);
        ctx.MbState[mbBase + 1] = (byte)(quantizer & 0xFF);
    }

    private static byte[] GetScanTable(int scanTableIndex) => scanTableIndex switch
    {
        // FUN_8029BFAC installs ctx+0x94 as zigzag, ctx+0x98 as the
        // horizontal scan, and ctx+0x9C as the vertical scan.
        1 => HorizontalScan,
        2 => VerticalScan,
        _ => ZigzagScan,
    };

    private static string GetA878PredictionMode()
        => Environment.GetEnvironmentVariable("VID1_A878_PREDICT")?.ToLowerInvariant() ?? "all";

    private static string GetA878ScanMode()
        => Environment.GetEnvironmentVariable("VID1_A878_SCAN_MODE")?.ToLowerInvariant() ?? "auto";

    private static bool ShouldEnableFieldPrediction()
        => Environment.GetEnvironmentVariable("VID1_FIELD_PREDICTION") == "1";

    private static bool ShouldApplyA878Prediction(string predictionMode, bool isIntraMacroblock, int blockIndex)
    {
        if (!isIntraMacroblock)
            return false;

        return predictionMode switch
        {
            "all" => true,
            "luma" => blockIndex < 4,
            "chroma" => blockIndex >= 4,
            _ => false,
        };
    }

    private static int? GetConfiguredIntraWriteOffset()
    {
        var value = Environment.GetEnvironmentVariable("VID1_A878_WRITE_OFFSET");
        return int.TryParse(value, out var offset) ? offset : null;
    }

    private static (int Min, int Max, int Wrap) GetMotionBounds(int fCode)
    {
        var rangeUnit = 1 << Math.Max(fCode - 1, 0);
        return (-rangeUnit * 0x20, rangeUnit * 0x20 - 1, rangeUnit * 0x40);
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

    private static int Median3(int a, int b, int c)
    {
        if (a > b)
            (a, b) = (b, a);
        if (b > c)
            (b, c) = (c, b);
        if (a > b)
            (a, b) = (b, a);
        return b;
    }

    private static int RoundHalfChroma(int value)
        => (value & 3) == 0 ? value / 2 : (value >> 1) | 1;

    private static (int X, int Y) ComputeFieldChromaMotionVector(int mvX, int mvY)
    {
        var chromaX = mvX >> 1;
        if ((mvX & 3) != 0)
            chromaX |= 1;

        var chromaY = mvY >> 1;
        if ((mvY & 6) != 0)
            chromaY |= 2;

        return (chromaX, chromaY);
    }

    internal static int RoundFourMotionChroma(int value)
    {
        if (value == 0)
            return 0;

        var magnitude = Math.Abs(value);
        var rounded = FourMotionChromaMvRoundingTable[magnitude & 0xF] + ((magnitude >> 4) * 2);
        return value < 0 ? -rounded : rounded;
    }

    private static int RoundHalvedOdd(int value)
        => (value >> 1) | (value & 1);

    private static int DivideByTwoTruncated(int value)
        => value < 0 ? -((-value) / 2) : value / 2;

    internal static int ComputeDcScale(int qp, bool isLuma)
    {
        if (isLuma)
        {
            if (qp is >= 1 and <= 4) return 8;
            if (qp is >= 5 and <= 8) return 2 * qp;
            if (qp is >= 9 and <= 24) return qp + 8;
            return 2 * qp - 16;
        }

        if (qp is >= 1 and <= 4) return 8;
        if (qp is >= 5 and <= 24) return (qp + 13) / 2;
        return qp - 6;
    }

}
