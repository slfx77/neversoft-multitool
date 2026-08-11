namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Decodes Nintendo 64 ADPCM frames with ABI1/libultra audio-microcode
///     runtime semantics using the predictor book stored in a Sound Tools PTR
///     bank. This is intentionally rate-independent: it decodes the stored mono
///     sample once and does not apply pitch, loops, or cue/song ownership.
/// </summary>
public static class N64AdpcmDecoder
{
    public const int FrameSize = 9;
    public const int SamplesPerFrame = 16;
    public const int SamplesPerSubvector = 8;
    public const int MaximumScale = 12;
    public const int MaximumPredictors = 16;

    private const int CoefficientsPerPredictor = 16;
    private const int FractionBits = 11;

    /// <summary>
    ///     Decodes complete 9-byte frames to exactly 16 signed PCM samples per
    ///     frame. Decoder history starts at zero and carries only across the
    ///     stored frames supplied in <paramref name="encodedFrames" />.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The encoded length, book shape, scale, or predictor index is outside
    ///     the checked Sound Tools format.
    /// </exception>
    public static short[] Decode(
        ReadOnlySpan<byte> encodedFrames,
        N64SoundToolsAdpcmBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        ValidateBook(book);
        if (encodedFrames.Length % FrameSize != 0)
        {
            throw new InvalidDataException(
                $"N64 ADPCM payload length {encodedFrames.Length} is not a multiple of {FrameSize}");
        }

        var frameCount = encodedFrames.Length / FrameSize;
        var sampleCount = (long)frameCount * SamplesPerFrame;
        if (sampleCount > Array.MaxLength)
            throw new InvalidDataException("N64 ADPCM decoded sample count exceeds the runtime array limit");

        // Validate every header before allocating or decoding. A malformed late
        // frame therefore cannot yield a prefix that looks successfully decoded.
        for (var frame = 0; frame < frameCount; frame++)
        {
            var header = encodedFrames[frame * FrameSize];
            var scale = header >> 4;
            var predictor = header & 0x0F;
            if (scale > MaximumScale)
            {
                throw new InvalidDataException(
                    $"N64 ADPCM frame {frame} scale {scale} exceeds {MaximumScale}");
            }

            if (predictor >= book.PredictorCount)
            {
                throw new InvalidDataException(
                    $"N64 ADPCM frame {frame} predictor {predictor} exceeds the " +
                    $"{book.PredictorCount}-predictor book");
            }
        }

        if (frameCount == 0)
            return [];

        var samples = new short[(int)sampleCount];
        Span<short> state = stackalloc short[SamplesPerSubvector];
        state.Clear();
        Span<int> residuals = stackalloc int[SamplesPerSubvector];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = frame * FrameSize;
            var header = encodedFrames[frameOffset];
            var scale = header >> 4;
            var predictor = header & 0x0F;
            var coefficientBase = predictor * CoefficientsPerPredictor;

            for (var half = 0; half < 2; half++)
            {
                var payloadOffset = frameOffset + 1 + half * 4;
                for (var i = 0; i < SamplesPerSubvector; i++)
                {
                    var packed = encodedFrames[payloadOffset + (i >> 1)];
                    var nibble = (i & 1) == 0 ? packed >> 4 : packed & 0x0F;
                    var signedNibble = (nibble & 8) == 0 ? nibble : nibble - 16;
                    residuals[i] = signedNibble << scale;
                }

                // Both history taps are frozen for this whole eight-sample
                // subvector. Intra-subvector feedback uses RAW residuals below,
                // not samples that have already been clamped.
                var older = state[6];
                var newer = state[7];
                for (var i = 0; i < SamplesPerSubvector; i++)
                {
                    // ABI1's VMUDH/VMADH sequence exposes ACC[47:16] as a signed
                    // 32-bit Q11 sum. Nintendo's sdk-tools reference likewise uses
                    // int32_t: accepted pathological books can wrap even though
                    // authored banks do not. Accumulate explicitly modulo 2^32 so
                    // behavior is deterministic in checked and unchecked builds.
                    var accumulator = unchecked((uint)((long)residuals[i] << FractionBits));
                    accumulator = AddProduct(
                        accumulator,
                        book.Coefficients[coefficientBase + i],
                        older);
                    accumulator = AddProduct(
                        accumulator,
                        book.Coefficients[coefficientBase + SamplesPerSubvector + i],
                        newer);
                    for (var k = 0; k < i; k++)
                    {
                        accumulator = AddProduct(
                            accumulator,
                            book.Coefficients[coefficientBase + SamplesPerSubvector + k],
                            residuals[i - 1 - k]);
                    }

                    var decoded = unchecked((int)accumulator) >> FractionBits;
                    var clamped = (short)Math.Clamp(decoded, short.MinValue, short.MaxValue);
                    state[i] = clamped;
                    samples[frame * SamplesPerFrame + half * SamplesPerSubvector + i] = clamped;
                }
            }
        }

        return samples;
    }

    private static uint AddProduct(uint accumulator, short coefficient, int value) =>
        unchecked(accumulator + (uint)((long)coefficient * value));

    private static void ValidateBook(N64SoundToolsAdpcmBook book)
    {
        if (book.Order != 2)
            throw new InvalidDataException($"N64 ADPCM book order {book.Order} is not 2");
        if (book.PredictorCount is < 1 or > MaximumPredictors)
        {
            throw new InvalidDataException(
                $"N64 ADPCM predictor count {book.PredictorCount} is outside 1..{MaximumPredictors}");
        }

        if (book.Coefficients is null)
            throw new InvalidDataException("N64 ADPCM book coefficients are missing");
        var expected = book.PredictorCount * CoefficientsPerPredictor;
        if (book.Coefficients.Count != expected)
        {
            throw new InvalidDataException(
                $"N64 ADPCM book has {book.Coefficients.Count} coefficients; expected {expected}");
        }
    }
}
