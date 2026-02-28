using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Helpers;

/// <summary>
///     Compares two PNG images pixel-by-pixel.
/// </summary>
public static class PixelComparer
{
    /// <summary>
    ///     Compare two PNG files pixel-by-pixel as RGBA.
    /// </summary>
    public static CompareResult CompareRgba(string actualPath, string expectedPath)
    {
        using var actual = Image.Load<Rgba32>(actualPath);
        using var expected = Image.Load<Rgba32>(expectedPath);

        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            return new CompareResult(false, null, null,
                $"Dimension mismatch: actual={actual.Width}x{actual.Height}, expected={expected.Width}x{expected.Height}");
        }

        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var a = actual[x, y];
                var e = expected[x, y];
                if (a.R != e.R || a.G != e.G || a.B != e.B || a.A != e.A)
                {
                    return new CompareResult(false, x, y,
                        $"Pixel mismatch at ({x},{y}): actual=({a.R},{a.G},{a.B},{a.A}), expected=({e.R},{e.G},{e.B},{e.A})");
                }
            }
        }

        return new CompareResult(true, null, null, null);
    }

    /// <summary>
    ///     Compare two PNG files pixel-by-pixel as RGB (ignoring alpha).
    /// </summary>
    public static CompareResult CompareRgb(string actualPath, string expectedPath)
    {
        using var actual = Image.Load<Rgb24>(actualPath);
        using var expected = Image.Load<Rgb24>(expectedPath);

        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            return new CompareResult(false, null, null,
                $"Dimension mismatch: actual={actual.Width}x{actual.Height}, expected={expected.Width}x{expected.Height}");
        }

        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var a = actual[x, y];
                var e = expected[x, y];
                if (a.R != e.R || a.G != e.G || a.B != e.B)
                {
                    return new CompareResult(false, x, y,
                        $"Pixel mismatch at ({x},{y}): actual=({a.R},{a.G},{a.B}), expected=({e.R},{e.G},{e.B})");
                }
            }
        }

        return new CompareResult(true, null, null, null);
    }

    public record CompareResult(bool Match, int? FirstMismatchX, int? FirstMismatchY, string? Details);
}