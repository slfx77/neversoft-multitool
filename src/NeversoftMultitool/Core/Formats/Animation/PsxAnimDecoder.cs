namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Bulk-decodes one PSX animation slot (<c>boneCount × 6 channels × frameCount</c>
///     samples) by invoking <see cref="PsxAnimDecompressor" /> sequentially per channel.
/// </summary>
public static class PsxAnimDecoder
{
    /// <summary>
    ///     Decodes the compressed stream for one animation into a fully-populated
    ///     <see cref="PsxAnimation" />. The codec advances through
    ///     <paramref name="stream" /> in order: bone 0 channels 0..5, bone 1
    ///     channels 0..5, etc.
    /// </summary>
    /// <param name="stream">Compressed bytes starting at the animation's pool offset.</param>
    /// <param name="boneCount">Number of bones (channels are decoded for each).</param>
    /// <param name="frameCount">Number of frames per channel (taken from the entry table).</param>
    /// <param name="bytesConsumed">Total bytes the codec consumed from <paramref name="stream" />.</param>
    public static PsxAnimation Decode(
        ReadOnlySpan<byte> stream, int boneCount, int frameCount, out int bytesConsumed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        var temp = new short[frameCount];
        var consumed = 0;

        for (var b = 0; b < boneCount; b++)
        {
            for (var c = 0; c < PsxAnimation.ChannelsPerBone; c++)
            {
                if (consumed >= stream.Length)
                {
                    throw new InvalidDataException(
                        $"PSX animation stream exhausted at bone {b} channel {c}: " +
                        $"consumed {consumed} of {stream.Length}.");
                }

                var bytes = PsxAnimDecompressor.Decompress(
                    stream[consumed..], temp, 1, frameCount);
                consumed += bytes;

                for (var f = 0; f < frameCount; f++)
                    channels[b, c, f] = temp[f];
            }
        }

        bytesConsumed = consumed;
        return new PsxAnimation
        {
            FrameCount = frameCount,
            BoneCount = boneCount,
            Channels = channels
        };
    }

    /// <summary>
    ///     Convenience overload that discards the bytes-consumed count.
    /// </summary>
    public static PsxAnimation Decode(ReadOnlySpan<byte> stream, int boneCount, int frameCount)
    {
        return Decode(stream, boneCount, frameCount, out _);
    }
}
