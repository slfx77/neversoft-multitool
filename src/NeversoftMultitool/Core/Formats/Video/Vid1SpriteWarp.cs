namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Frame-level sprite-warp parameter setup from <c>FUN_8029C650</c>.
///     For THAW GC's long-form class-3 frames this reduces to the 4 global
///     fixed-point offsets consumed by the per-macroblock GMC helpers.
/// </summary>
internal static class Vid1SpriteWarp
{
    public static void ApplyFrame(Vid1FrameContext context, Vid1VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frame);

        context.ResetSpriteWarp();
        if (frame.PreambleClass != 3)
            return;

        context.SpritePointCount = Math.Clamp(frame.SpritePointCount ?? 0, 0, 3);
        context.SpriteWarpAccuracy = Math.Clamp(frame.SpriteWarpAccuracy ?? 0, 0, 3);
        if (context.SpritePointCount <= 0)
            return;

        ComputeWarpParameters(context, frame.SpriteTrajectoryDeltas);
    }

    private static void ComputeWarpParameters(Vid1FrameContext context, int[]? deltas)
    {
        Span<int> delta = stackalloc int[8];
        if (deltas is { Length: > 0 })
            deltas.AsSpan(0, Math.Min(deltas.Length, delta.Length)).CopyTo(delta);

        var width = context.Width;
        var pointCount = context.SpritePointCount;
        var accuracy = context.SpriteWarpAccuracy;

        var widthBits = 0;
        if (width > 1)
        {
            while (1 << widthBits < width)
                widthBits++;
        }

        var scale = 2 << accuracy;
        var spriteSubdivision = 0x10 / scale;
        var halfScale = scale >> 1;
        var widthPow2 = 1 << widthBits;

        var anchor0X = context.SpriteAnchor0X;
        var anchor0Y = context.SpriteAnchor0Y;
        var anchor1X = context.SpriteAnchor1X;
        var anchor1Y = context.SpriteAnchor1Y;

        var lumaBaseX = halfScale * (anchor0X * 2L + delta[0]);
        var lumaBaseY = halfScale * (anchor0Y * 2L + delta[1]);
        var numeratorX =
            (width - widthPow2) * (spriteSubdivision * lumaBaseX - anchor0X * 16L) +
            widthPow2 *
            (spriteSubdivision * halfScale * (anchor1X * 2L + delta[0] + delta[2]) - anchor1X * 16L);

        var widthHalf = width >> 1;
        var roundingX = numeratorX < 1 ? -widthHalf : widthHalf;

        var numeratorY =
            (width - widthPow2) * (spriteSubdivision * lumaBaseY - anchor0Y * 16L) +
            widthPow2 *
            (spriteSubdivision * halfScale * (anchor1Y * 2L + delta[1] + delta[3]) - anchor1Y * 16L);

        var roundingY = numeratorY < 1 ? -widthHalf : widthHalf;
        var projectedY = anchor0Y * 16 + (int)((numeratorY + roundingY) / width);

        long warpLumaX;
        long warpLumaY;
        long warpChromaX;
        long warpChromaY;

        long scaleCoeffX;
        long crossCoeffX;
        long crossCoeffY;
        long scaleCoeffY;
        long scaleCoeffChromaX;
        long crossCoeffChromaX;
        long crossCoeffChromaY;
        long scaleCoeffChromaY;
        var shiftLumaX = 0;
        var shiftLumaY = 0;
        var shiftChromaX = 0;
        var shiftChromaY = 0;

        if (pointCount == 1)
        {
            warpChromaX = ((lumaBaseX >> 1) | (lumaBaseX & 1L)) - (long)scale * (anchor0X / 2);
            warpChromaY = ((lumaBaseY >> 1) | (lumaBaseY & 1L)) - (long)scale * (anchor0Y / 2);
            warpLumaX = lumaBaseX - (long)scale * anchor0X;
            warpLumaY = lumaBaseY - (long)scale * anchor0Y;

            scaleCoeffX = scale;
            crossCoeffX = 0;
            crossCoeffY = 0;
            scaleCoeffY = scale;
            scaleCoeffChromaX = scale;
            crossCoeffChromaX = 0;
            crossCoeffChromaY = 0;
            scaleCoeffChromaY = scale;
        }
        else if (pointCount > 1)
        {
            shiftLumaX = widthBits + (3 - accuracy);
            shiftLumaY = shiftLumaX;
            shiftChromaX = shiftLumaX + 2;
            shiftChromaY = shiftChromaX;

            var coeffY = -spriteSubdivision * lumaBaseY + projectedY;
            var coeffX = spriteSubdivision * lumaBaseY - projectedY;
            var coeffMain =
                -spriteSubdivision * lumaBaseX +
                (anchor0X + widthPow2) * 16L +
                (numeratorX + roundingX) / width;

            var doubleAnchorX = anchor0X * -2 + 1;
            var doubleAnchorY = anchor0Y * -2 + 1;
            var negAnchorX = -anchor0X;
            var negAnchorY = -anchor0Y;
            var chromaScale = widthPow2 * spriteSubdivision * 2L;

            warpChromaY = coeffY * doubleAnchorX + coeffMain * doubleAnchorY + chromaScale * lumaBaseY -
                          widthPow2 * 16L;
            warpLumaY = (lumaBaseY << shiftLumaX) + coeffY * negAnchorX + coeffMain * negAnchorY;
            warpChromaX = coeffMain * doubleAnchorX + coeffX * doubleAnchorY + chromaScale * lumaBaseX -
                          widthPow2 * 16L;
            warpLumaX = (lumaBaseX << shiftLumaX) + coeffMain * negAnchorX + coeffX * negAnchorY;

            scaleCoeffX = coeffMain;
            crossCoeffX = coeffX;
            crossCoeffY = coeffY;
            scaleCoeffY = coeffMain;
            scaleCoeffChromaX = coeffMain * 4;
            crossCoeffChromaX = coeffX * 4;
            crossCoeffChromaY = coeffY * 4;
            scaleCoeffChromaY = coeffMain * 4;
        }
        else
        {
            warpLumaX = 0;
            warpLumaY = 0;
            warpChromaX = 0;
            warpChromaY = 0;

            scaleCoeffX = scale;
            crossCoeffX = 0;
            crossCoeffY = 0;
            scaleCoeffY = scale;
            scaleCoeffChromaX = scale;
            crossCoeffChromaX = 0;
            crossCoeffChromaY = 0;
            scaleCoeffChromaY = scale;
        }

        if (scaleCoeffX == (long)scale << shiftLumaX &&
            crossCoeffX == 0 &&
            crossCoeffY == 0 &&
            scaleCoeffY == (long)scale << shiftLumaY &&
            scaleCoeffChromaX == (long)scale << shiftChromaX &&
            crossCoeffChromaX == 0 &&
            crossCoeffChromaY == 0 &&
            scaleCoeffChromaY == (long)scale << shiftChromaY)
        {
            warpLumaX >>= shiftLumaX;
            warpLumaY >>= shiftLumaY;
            warpChromaX >>= shiftChromaX;
            warpChromaY >>= shiftChromaY;
        }

        context.SpriteLumaX = unchecked((int)warpLumaX);
        context.SpriteLumaY = unchecked((int)warpLumaY);
        context.SpriteLumaScaleX = unchecked((int)scaleCoeffX);
        context.SpriteLumaCrossX = unchecked((int)crossCoeffX);
        context.SpriteLumaCrossY = unchecked((int)crossCoeffY);
        context.SpriteLumaScaleY = unchecked((int)scaleCoeffY);
        context.SpriteLumaTransformShift = shiftLumaX;

        context.SpriteChromaX = unchecked((int)warpChromaX);
        context.SpriteChromaY = unchecked((int)warpChromaY);
        context.SpriteChromaScaleX = unchecked((int)scaleCoeffChromaX);
        context.SpriteChromaCrossX = unchecked((int)crossCoeffChromaX);
        context.SpriteChromaCrossY = unchecked((int)crossCoeffChromaY);
        context.SpriteChromaScaleY = unchecked((int)scaleCoeffChromaY);
        context.SpriteChromaTransformShift = shiftChromaX;
    }
}
