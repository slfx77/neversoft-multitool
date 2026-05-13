namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Inverse quantization for Factor 5 M4Decoder coefficient blocks.
///     Inter (FUN_802A01E4): MPEG-4 H.263-style with dead-zone offset.
///     Intra (FUN_802A0304): custom quantization matrix, scale >> 3.
///     Both clamp output to [-2048, 2047].
/// </summary>
internal static class Vid1Dequant
{
    public static void DequantInter(Span<short> output, ReadOnlySpan<short> input, int qp, int dcScale)
    {
        var qpByte = qp & 0xFF;
        var step = qpByte * 2;
        var offset = (qp & 1) == 0 ? qpByte - 1 : qpByte;

        output[0] = Clamp(input[0] * (dcScale & 0xFFFF));

        for (var i = 1; i < 64; i++)
        {
            int coeff = input[i];
            if (coeff == 0)
            {
                output[i] = 0;
                continue;
            }

            if (coeff > 0)
                output[i] = Clamp(coeff * step + offset);
            else
                output[i] = ClampNegativeMagnitude(-coeff * step + offset);
        }
    }

    public static void DequantIntra(Span<short> output, ReadOnlySpan<short> input, int qp, int dcScale,
        ReadOnlySpan<byte> matrix)
    {
        output[0] = Clamp(input[0] * (dcScale & 0xFFFF));

        var qpByte = qp & 0xFF;
        for (var i = 1; i < 64; i++)
        {
            int coeff = input[i];
            if (coeff == 0)
            {
                output[i] = 0;
                continue;
            }

            var scale = matrix[i] * qpByte;
            if (coeff > 0)
                output[i] = (short)Math.Min((coeff * scale) >> 3, 0x7FF);
            else
                output[i] = ClampNegativeMagnitude((-coeff * scale) >> 3);
        }
    }

    public static void DequantInterResidual(Span<short> output, ReadOnlySpan<short> input, int qp)
    {
        var qpByte = qp & 0xFF;
        var step = qpByte * 2;
        var offset = (qp & 1) == 0 ? qpByte - 1 : qpByte;

        for (var i = 0; i < 64; i++)
        {
            int coeff = input[i];
            if (coeff == 0)
            {
                output[i] = 0;
                continue;
            }

            if (coeff > 0)
                output[i] = Clamp(coeff * step + offset);
            else
                output[i] = ClampNegativeMagnitude(-coeff * step + offset);
        }
    }

    public static void DequantInterResidual(Span<short> output, ReadOnlySpan<short> input, int qp,
        ReadOnlySpan<byte> matrix)
    {
        uint parity = 0;
        var qpByte = qp & 0xFF;

        for (var i = 0; i < 64; i++)
        {
            int coeff = input[i];
            if (coeff == 0)
            {
                output[i] = 0;
                continue;
            }

            var scale = matrix[i] * qpByte;
            short value;
            if (coeff > 0)
                value = Clamp(((coeff * 2 + 1) * scale) >> 4);
            else
                value = ClampNegativeMagnitude(((-coeff * 2 + 1) * scale) >> 4);

            output[i] = value;
            parity ^= unchecked((ushort)value);
        }

        if ((parity & 1) == 0)
            output[63] ^= 1;
    }

    private static short Clamp(int value)
    {
        return (short)Math.Clamp(value, -0x800, 0x7FF);
    }

    private static short ClampNegativeMagnitude(int magnitude)
    {
        return (short)Math.Max(-magnitude, -0x800);
    }
}
