using System.Runtime.CompilerServices;

namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Floating-point 8×8 IDCT matching Factor 5's M4Decoder (FUN_8029E8A0).
///     Constants are cos(k * π/16) / 2 with √(2/N) normalization baked in.
///     Two-pass separable: rows then columns, with a 64-float intermediate buffer.
///     Input/output: 64 shorts in row-major order (8×8 coefficient block).
/// </summary>
internal static class Vid1Idct
{
    private const double Sqrt2Inv = 0.70710678118654752; // 1/√2
    private const double C1 = 0.49039264020161522;  // cos(1π/16) / 2
    private const double C2 = 0.46193976625564337;  // cos(2π/16) / 2
    private const double C3 = 0.41573480615127262;  // cos(3π/16) / 2
    private const double C4 = 0.35355339059327373;  // cos(4π/16) / 2 = 1/(2√2)
    private const double C5 = 0.27778511650980114;  // cos(5π/16) / 2
    private const double C6 = 0.19134171618254492;  // cos(6π/16) / 2
    private const double C7 = 0.09754516100806417;  // cos(7π/16) / 2

    public static void Transform(short[] coefficients)
    {
        Span<float> temp = stackalloc float[64];
        RowPass(coefficients, temp);
        ColumnPass(temp, coefficients);
    }

    private static void RowPass(ReadOnlySpan<short> input, Span<float> output)
    {
        for (var row = 0; row < 8; row++)
        {
            var baseIdx = row * 8;
            double x0 = input[baseIdx];
            double x1 = input[baseIdx + 1];
            double x2 = input[baseIdx + 2];
            double x3 = input[baseIdx + 3];
            double x4 = input[baseIdx + 4];
            double x5 = input[baseIdx + 5];
            double x6 = input[baseIdx + 6];
            double x7 = input[baseIdx + 7];

            var a0 = x1 * C7 - x7 * C1;
            var a1 = x7 * C7 + x1 * C1;
            var a2 = x5 * C3 - x3 * C5;
            var a3 = x3 * C3 + x5 * C5;
            var a4 = (x0 + x4) * C4;
            var a5 = (x0 - x4) * C4;
            var a6 = x6 * C6 + x2 * C2;
            var a7 = x2 * C6 - x6 * C2;

            var b0 = a1 + a3;
            var b1 = a0 + a2;
            var b2 = a4 + a6;
            var b3 = a4 - a6;
            var b4 = a5 + a7;
            var b5 = a5 - a7;
            var b6 = a0 - a2;
            var b7 = a1 - a3;

            var c0 = Sqrt2Inv * (b7 - b6);
            var c1 = Sqrt2Inv * (b7 + b6);

            output[baseIdx + 0] = (float)(b2 + b0);
            output[baseIdx + 1] = (float)(b4 + c1);
            output[baseIdx + 2] = (float)(b5 + c0);
            output[baseIdx + 3] = (float)(b3 + b1);
            output[baseIdx + 4] = (float)(b3 - b1);
            output[baseIdx + 5] = (float)(b5 - c0);
            output[baseIdx + 6] = (float)(b4 - c1);
            output[baseIdx + 7] = (float)(b2 - b0);
        }
    }

    private static void ColumnPass(ReadOnlySpan<float> input, Span<short> output)
    {
        for (var col = 0; col < 8; col++)
        {
            double x0 = input[col];
            double x1 = input[col + 8];
            double x2 = input[col + 16];
            double x3 = input[col + 24];
            double x4 = input[col + 32];
            double x5 = input[col + 40];
            double x6 = input[col + 48];
            double x7 = input[col + 56];

            var a0 = x1 * C7 - x7 * C1;
            var a1 = x7 * C7 + x1 * C1;
            var a2 = x5 * C3 - x3 * C5;
            var a3 = x3 * C3 + x5 * C5;
            var a4 = (x0 + x4) * C4;
            var a5 = (x0 - x4) * C4;
            var a6 = x2 * C6 + x6 * C2;
            var a7 = x6 * C6 - x2 * C2;

            var b0 = a1 + a3;
            var b1 = a0 + a2;
            var b2 = a4 + a6;
            var b3 = a4 - a6;
            var b4 = a5 + a7;
            var b5 = a5 - a7;
            var b6 = a0 - a2;
            var b7 = a1 - a3;

            var c0 = Sqrt2Inv * (b7 - b6);
            var c1 = Sqrt2Inv * (b7 + b6);

            output[col + 0] = RoundToShort(b2 + b0);
            output[col + 8] = RoundToShort(b4 + c1);
            output[col + 16] = RoundToShort(b5 + c0);
            output[col + 24] = RoundToShort(b3 + b1);
            output[col + 32] = RoundToShort(b3 - b1);
            output[col + 40] = RoundToShort(b5 - c0);
            output[col + 48] = RoundToShort(b4 - c1);
            output[col + 56] = RoundToShort(b2 - b0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short RoundToShort(double value)
    {
        value += 0.5;
        if (value < 0.0)
            value -= 1.0;
        return (short)value;
    }
}
