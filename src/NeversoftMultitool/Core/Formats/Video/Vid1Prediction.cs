namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     DC/AC prediction context port of FUN_802A044C and FUN_8029D494.
///     MPEG-4-style left/top-neighbor gradient prediction with per-block
///     scan table selection. Stores reconstructed boundary coefficients
///     in the per-MB state buffer for future blocks to predict from.
/// </summary>
internal static class Vid1Prediction
{
    private const short DefaultDcPredictor = 1024;

    /// <summary>
    ///     Marks an MB as A878 (intra a878 stage) so neighbors can use it
    ///     for prediction. Also stores the macroblock quantizer.
    /// </summary>
    public static void MarkIntraMacroblock(Vid1FrameContext ctx, int mbX, int mbY, int quantizer)
    {
        var mbBase = MbStateOffset(ctx, mbX, mbY);
        ctx.MbState[mbBase + 0] = 3; // mark as a878 (decomp checks for value 3 or 4)
        ctx.MbState[mbBase + 1] = (byte)(quantizer & 0xFF);
    }

    /// <summary>
    ///     Computes DC + 7 AC predictions for a given block, mirroring
    ///     FUN_802A044C. Output buffer must hold at least 8 shorts (DC + 7 AC).
    ///     Returns the prediction direction (1 = top, 2 = left) which becomes
    ///     the scan table index for AC decode.
    /// </summary>
    public static int ComputePredictions(
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int blockIndex,
        int quantizer,
        int dcScale,
        Span<short> predictions)
    {
        var mbState = ctx.MbState;

        // Find left, top, top-left neighbor block prediction sources.
        // Decomp: a neighbor contributes only if mb_state[0] is 3 or 4 (a878 intra).
        int leftBlockOffset = -1;
        int topBlockOffset = -1;
        int topLeftBlockOffset = -1;
        var leftQuant = quantizer;
        var topQuant = quantizer;

        // Left MB exists if mbX > 0
        int leftMbBase = -1;
        if (mbX > 0)
        {
            leftMbBase = MbStateOffset(ctx, mbX - 1, mbY);
            var t = mbState[leftMbBase];
            if (t == 3 || t == 4)
                leftQuant = mbState[leftMbBase + 1];
            else
                leftMbBase = -1;
        }

        int topMbBase = -1;
        if (mbY > 0)
        {
            topMbBase = MbStateOffset(ctx, mbX, mbY - 1);
            var t = mbState[topMbBase];
            if (t == 3 || t == 4)
                topQuant = mbState[topMbBase + 1];
            else
                topMbBase = -1;
        }

        int topLeftMbBase = -1;
        if (mbX > 0 && mbY > 0)
        {
            topLeftMbBase = MbStateOffset(ctx, mbX - 1, mbY - 1);
            var t = mbState[topLeftMbBase];
            if (!(t == 3 || t == 4))
                topLeftMbBase = -1;
        }

        var currentMbBase = MbStateOffset(ctx, mbX, mbY);

        // Block-specific neighbor selection. Block layout in MB:
        //   0=Y0 (top-left)  1=Y1 (top-right)
        //   2=Y2 (bot-left)  3=Y3 (bot-right)
        //   4=Cb             5=Cr
        // Predictors come from spatially-adjacent blocks (which may be in
        // current MB, left MB, top MB, or top-left MB).
        int leftPred, topPred, topLeftPred;
        switch (blockIndex)
        {
            case 0:
                leftPred = (leftMbBase >= 0) ? OffsetForBlock(leftMbBase, 1) : -1;
                topPred = (topMbBase >= 0) ? OffsetForBlock(topMbBase, 2) : -1;
                topLeftPred = (topLeftMbBase >= 0) ? OffsetForBlock(topLeftMbBase, 3) : -1;
                break;
            case 1:
                leftPred = OffsetForBlock(currentMbBase, 0);
                topPred = (topMbBase >= 0) ? OffsetForBlock(topMbBase, 3) : -1;
                topLeftPred = (topMbBase >= 0) ? OffsetForBlock(topMbBase, 2) : -1;
                topQuant = (topMbBase >= 0) ? mbState[topMbBase + 1] : quantizer;
                leftQuant = quantizer;
                break;
            case 2:
                leftPred = (leftMbBase >= 0) ? OffsetForBlock(leftMbBase, 3) : -1;
                topPred = OffsetForBlock(currentMbBase, 0);
                topLeftPred = (leftMbBase >= 0) ? OffsetForBlock(leftMbBase, 1) : -1;
                leftQuant = (leftMbBase >= 0) ? mbState[leftMbBase + 1] : quantizer;
                topQuant = quantizer;
                break;
            case 3:
                leftPred = OffsetForBlock(currentMbBase, 2);
                topPred = OffsetForBlock(currentMbBase, 1);
                topLeftPred = OffsetForBlock(currentMbBase, 0);
                leftQuant = quantizer;
                topQuant = quantizer;
                break;
            case 4: // Cb
                leftPred = (leftMbBase >= 0) ? OffsetForBlock(leftMbBase, 4) : -1;
                topPred = (topMbBase >= 0) ? OffsetForBlock(topMbBase, 4) : -1;
                topLeftPred = (topLeftMbBase >= 0) ? OffsetForBlock(topLeftMbBase, 4) : -1;
                break;
            case 5: // Cr
                leftPred = (leftMbBase >= 0) ? OffsetForBlock(leftMbBase, 5) : -1;
                topPred = (topMbBase >= 0) ? OffsetForBlock(topMbBase, 5) : -1;
                topLeftPred = (topLeftMbBase >= 0) ? OffsetForBlock(topLeftMbBase, 5) : -1;
                break;
            default:
                leftPred = topPred = topLeftPred = -1;
                break;
        }

        // Read DC values from each predictor (or 1024 = neutral when missing,
        // matching the &DAT_802A078C default block).
        var leftDc = leftPred >= 0 ? ReadShort(mbState, leftPred) : DefaultDcPredictor;
        var topDc = topPred >= 0 ? ReadShort(mbState, topPred) : DefaultDcPredictor;
        var topLeftDc = topLeftPred >= 0 ? ReadShort(mbState, topLeftPred) : DefaultDcPredictor;

        // Gradient comparison: |left - topLeft| vs |topLeft - top|.
        // Smaller horizontal gradient → predict from top; else predict from left.
        var horizGrad = Math.Abs(leftDc - topLeftDc);
        var vertGrad = Math.Abs(topLeftDc - topDc);

        int direction; // 1 = top, 2 = left

        if (horizGrad < vertGrad)
        {
            direction = 1;
            predictions[0] = (short)((topDc + (dcScale >> 1)) / Math.Max(dcScale, 1));
            for (var i = 0; i < 7; i++)
            {
                predictions[i + 1] = ScalePredictionComponent(
                    topQuant,
                    quantizer,
                    topPred >= 0 ? ReadShort(mbState, topPred + 2 + (i * 2)) : (short)0);
            }
        }
        else
        {
            direction = 2;
            predictions[0] = (short)((leftDc + (dcScale >> 1)) / Math.Max(dcScale, 1));
            for (var i = 0; i < 7; i++)
            {
                predictions[i + 1] = ScalePredictionComponent(
                    leftQuant,
                    quantizer,
                    leftPred >= 0 ? ReadShort(mbState, leftPred + 0x10 + (i * 2)) : (short)0);
            }
        }

        // Store the scan table index for this block in MB state.
        ctx.MbState[currentMbBase + 2 + blockIndex] = (byte)direction;
        return direction;
    }

    /// <summary>
    ///     Adds DC/AC predictions to decoded coefficients and stores the
    ///     reconstructed top row + left column in the MB state buffer for
    ///     future blocks to predict from. Mirrors FUN_8029D494.
    /// </summary>
    public static void ApplyAndStorePredictions(
        Vid1FrameContext ctx,
        int mbX,
        int mbY,
        int blockIndex,
        int dcScale,
        ReadOnlySpan<short> predictions,
        Span<short> coefficients)
    {
        var currentMbBase = MbStateOffset(ctx, mbX, mbY);
        var blockBase = OffsetForBlock(currentMbBase, blockIndex);
        var mbState = ctx.MbState;

        // Always: add DC prediction, store dequantized DC
        var decodedDc = coefficients[0];
        var predDc = predictions[0];
        var finalDc = (short)(decodedDc + predDc);
        coefficients[0] = finalDc;
        WriteShort(mbState, blockBase, (short)(finalDc * dcScale));

        var scanTableIdx = mbState[currentMbBase + 2 + blockIndex];

        if (scanTableIdx == 1)
        {
            // Top prediction: add AC predictions to TOP ROW (positions 1-7)
            // Store top row (positions 1-7, with predictions added) in MB state at blockBase+2..blockBase+14
            // Store left column (positions 8, 16, 24, 32, 40, 48, 56) in MB state at blockBase+16..blockBase+28
            for (var i = 0; i < 7; i++)
            {
                var pos = i + 1;
                var v = (short)(coefficients[pos] + predictions[pos]);
                coefficients[pos] = v;
                WriteShort(mbState, blockBase + 2 + i * 2, v);
            }
            for (var i = 0; i < 7; i++)
            {
                WriteShort(mbState, blockBase + 16 + i * 2, coefficients[(i + 1) * 8]);
            }
        }
        else if (scanTableIdx == 2)
        {
            // Left prediction: add AC predictions to LEFT COLUMN (positions 8, 16, 24, ...)
            for (var i = 0; i < 7; i++)
            {
                var pos = (i + 1) * 8;
                var v = (short)(coefficients[pos] + predictions[i + 1]);
                coefficients[pos] = v;
                WriteShort(mbState, blockBase + 16 + i * 2, v);
            }
            // Store top row (positions 1-7, no prediction added)
            for (var i = 0; i < 7; i++)
            {
                WriteShort(mbState, blockBase + 2 + i * 2, coefficients[i + 1]);
            }
        }
        else
        {
            // No AC prediction (zigzag scan, scan_table_idx 0).
            // Just store top row + left column.
            for (var i = 0; i < 7; i++)
            {
                WriteShort(mbState, blockBase + 2 + i * 2, coefficients[i + 1]);
                WriteShort(mbState, blockBase + 16 + i * 2, coefficients[(i + 1) * 8]);
            }
        }
    }

    /// <summary>
    ///     Override scan table index for a block to 0 (zigzag). Called when
    ///     the macroblock's gate/feature bit is 0 (FUN_8029A878 line 689-691).
    /// </summary>
    public static void ForceZigzagScan(Vid1FrameContext ctx, int mbX, int mbY, int blockIndex)
    {
        var mbBase = MbStateOffset(ctx, mbX, mbY);
        ctx.MbState[mbBase + 2 + blockIndex] = 0;
    }

    public static int GetScanTableIndex(Vid1FrameContext ctx, int mbX, int mbY, int blockIndex)
    {
        var mbBase = MbStateOffset(ctx, mbX, mbY);
        return ctx.MbState[mbBase + 2 + blockIndex];
    }

    private static int MbStateOffset(Vid1FrameContext ctx, int mbX, int mbY)
        => (mbY * ctx.MbCols + mbX) * Vid1FrameContext.MbStateStride;

    private static int OffsetForBlock(int mbBase, int blockIndex)
        => mbBase + Vid1FrameContext.MbBlockOffsetBase + blockIndex * Vid1FrameContext.MbBlockStride;

    private static short ReadShort(byte[] buffer, int offset)
        => (short)((buffer[offset] << 8) | buffer[offset + 1]);

    private static void WriteShort(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    private static short ScalePredictionComponent(int sourceQuantizer, int currentQuantizer, short value)
    {
        if (value == 0)
            return 0;

        var scaled = sourceQuantizer * value;
        var rounding = currentQuantizer >> 1;
        if (scaled < 0)
            rounding = -rounding;

        return (short)((scaled + rounding) / Math.Max(currentQuantizer, 1));
    }
}
