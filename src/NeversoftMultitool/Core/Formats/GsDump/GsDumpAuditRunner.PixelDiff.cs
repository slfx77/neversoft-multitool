using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal static partial class GsDumpAuditRunner
{
    private static GsPixelDiffStats CompareAgainstPng(
        byte[] renderPixels,
        int width,
        int height,
        string pngPath,
        string? diffPath)
    {
        using var reference = Image.Load<Rgba32>(pngPath);
        if (reference.Width != width || reference.Height != height)
            reference.Mutate(ctx => ctx.Resize(width, height));

        var referencePixels = new byte[width * height * 4];
        reference.CopyPixelDataTo(referencePixels);
        return CompareAgainstPixels(renderPixels, width, height, referencePixels, width, height, diffPath);
    }

    private static GsPixelDiffStats CompareAgainstPixels(
        byte[] renderPixels,
        int width,
        int height,
        byte[] referencePixels,
        int referenceWidth,
        int referenceHeight,
        string? diffPath)
    {
        if (referenceWidth != width || referenceHeight != height)
        {
            using var referenceImage = Image.LoadPixelData<Rgba32>(referencePixels, referenceWidth, referenceHeight);
            referenceImage.Mutate(ctx => ctx.Resize(width, height));
            referencePixels = new byte[width * height * 4];
            referenceImage.CopyPixelDataTo(referencePixels);
        }

        var diffPixels = new byte[width * height * 4];
        double absSum = 0;
        double squareSum = 0;
        var max = 0;
        var channelCount = width * height * 3;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var r = Math.Abs(renderPixels[i] - referencePixels[i]);
                var g = Math.Abs(renderPixels[i + 1] - referencePixels[i + 1]);
                var b = Math.Abs(renderPixels[i + 2] - referencePixels[i + 2]);
                absSum += r + g + b;
                squareSum += r * r + g * g + b * b;
                max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
                diffPixels[i] = (byte)Math.Clamp(r * 4, 0, 255);
                diffPixels[i + 1] = (byte)Math.Clamp(g * 4, 0, 255);
                diffPixels[i + 2] = (byte)Math.Clamp(b * 4, 0, 255);
                diffPixels[i + 3] = 255;
            }
        }

        if (diffPath != null)
            SaveRgba(diffPath, diffPixels, width, height);

        return new GsPixelDiffStats
        {
            Width = width,
            Height = height,
            MeanAbsoluteError = channelCount == 0 ? 0 : absSum / channelCount,
            RootMeanSquareError = channelCount == 0 ? 0 : Math.Sqrt(squareSum / channelCount),
            MaxChannelDifference = max,
            RenderBounds = BuildPixelBounds(renderPixels, width, height),
            ReferenceBounds = BuildPixelBounds(referencePixels, width, height),
            TopMismatchRegions = BuildMismatchRegions(diffPixels, width, height)
        };
    }

    private static GsPixelBounds? BuildPixelBounds(byte[] pixels, int width, int height)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        long count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                count++;
            }
        }

        return count == 0
            ? null
            : new GsPixelBounds
            {
                X = minX,
                Y = minY,
                Width = maxX - minX + 1,
                Height = maxY - minY + 1,
                NonBlackPixels = count
            };
    }

    private static List<GsMismatchRegion> BuildMismatchRegions(byte[] diffPixels, int width, int height)
    {
        const int tile = 32;
        var regions = new List<GsMismatchRegion>();
        for (var y0 = 0; y0 < height; y0 += tile)
        {
            for (var x0 = 0; x0 < width; x0 += tile)
            {
                var w = Math.Min(tile, width - x0);
                var h = Math.Min(tile, height - y0);
                double sum = 0;
                for (var y = y0; y < y0 + h; y++)
                {
                    for (var x = x0; x < x0 + w; x++)
                    {
                        var i = (y * width + x) * 4;
                        sum += (diffPixels[i] + diffPixels[i + 1] + diffPixels[i + 2]) / 4.0;
                    }
                }

                regions.Add(new GsMismatchRegion
                {
                    X = x0,
                    Y = y0,
                    Width = w,
                    Height = h,
                    MeanAbsoluteError = sum / (w * h * 3)
                });
            }
        }

        return regions
            .OrderByDescending(static region => region.MeanAbsoluteError)
            .Take(10)
            .ToList();
    }

}
