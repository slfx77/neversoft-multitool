using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal static partial class GsDumpAuditRunner
{
    private static string? ResolvePngPath(string gsPath, string? explicitPng)
    {
        if (!string.IsNullOrWhiteSpace(explicitPng))
            return File.Exists(explicitPng) ? explicitPng : null;

        var sibling = Path.ChangeExtension(gsPath, ".png");
        return File.Exists(sibling) ? sibling : null;
    }

    private static (int Width, int Height) ResolveRenderDimensions(GsDumpFile dump, string? pngPath)
    {
        if (dump.ScreenshotWidth > 0 && dump.ScreenshotHeight > 0)
            return (dump.ScreenshotWidth, dump.ScreenshotHeight);

        if (pngPath != null)
        {
            var image = Image.Identify(pngPath);
            if (image != null)
                return (image.Width, image.Height);
        }

        return (640, 448);
    }

    private static (float X, float Y) ResolveCoordinateScale(GsDumpFile dump)
    {
        _ = dump;
        return (1f, 1f);
    }

    private static bool HasEmbeddedScreenshot(GsDumpFile dump)
    {
        return dump.ScreenshotWidth > 0 &&
               dump.ScreenshotHeight > 0 &&
               dump.ScreenshotPixels.Length == dump.ScreenshotWidth * dump.ScreenshotHeight * 4;
    }

    private static ReferencePixels? LoadReferencePixels(GsDumpFile dump, string? pngPath)
    {
        if (HasEmbeddedScreenshot(dump))
            return new ReferencePixels(
                ConvertEmbeddedScreenshotToRgba(dump),
                dump.ScreenshotWidth,
                dump.ScreenshotHeight);

        if (pngPath == null)
            return null;

        using var reference = Image.Load<Rgba32>(pngPath);
        var pixels = new byte[reference.Width * reference.Height * 4];
        reference.CopyPixelDataTo(pixels);
        return new ReferencePixels(pixels, reference.Width, reference.Height);
    }

    private static bool TryFitToReferencePresentation(
        byte[] sourcePixels,
        int width,
        int height,
        GsPixelBounds? sourceBounds,
        GsPixelBounds? referenceBounds,
        out byte[] fittedPixels)
    {
        fittedPixels = sourcePixels;
        if (sourceBounds == null || referenceBounds == null)
            return false;

        if (!ShouldFitToReferencePresentation(sourceBounds, referenceBounds, width, height))
            return false;

        var crop = new Rectangle(sourceBounds.X, sourceBounds.Y, sourceBounds.Width, sourceBounds.Height);
        var target = new Point(referenceBounds.X, referenceBounds.Y);
        using var sourceImage = Image.LoadPixelData<Rgba32>(sourcePixels, width, height);
        sourceImage.Mutate(ctx => ctx
            .Crop(crop)
            .Resize(referenceBounds.Width, referenceBounds.Height));

        using var canvas = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));
        canvas.Mutate(ctx => ctx.DrawImage(sourceImage, target, 1f));

        fittedPixels = new byte[width * height * 4];
        canvas.CopyPixelDataTo(fittedPixels);
        return true;
    }

    private static bool ShouldFitToReferencePresentation(
        GsPixelBounds source,
        GsPixelBounds reference,
        int width,
        int height)
    {
        if (source.Width <= 0 || source.Height <= 0 || reference.Width <= 0 || reference.Height <= 0)
            return false;

        var alreadyAligned =
            Math.Abs(source.X - reference.X) <= 2 &&
            Math.Abs(source.Y - reference.Y) <= 2 &&
            Math.Abs(source.Width - reference.Width) <= 2 &&
            Math.Abs(source.Height - reference.Height) <= 2;
        if (alreadyAligned)
            return false;

        var sourceNearlyFullWidth = source.Width >= width * 0.90;
        var referenceNearlyFullWidth = reference.Width >= width * 0.90;
        var sourceMostlyFullHeight = source.Height >= height * 0.80;
        var referenceLetterboxed = reference.Height <= source.Height - 24 && reference.Y >= 16;
        return sourceNearlyFullWidth &&
               referenceNearlyFullWidth &&
               sourceMostlyFullHeight &&
               referenceLetterboxed;
    }

    private static byte[] ConvertEmbeddedScreenshotToRgba(GsDumpFile dump)
    {
        var rgba = new byte[dump.ScreenshotPixels.Length];
        for (var i = 0; i < dump.ScreenshotPixels.Length; i += 4)
        {
            rgba[i] = dump.ScreenshotPixels[i];
            rgba[i + 1] = dump.ScreenshotPixels[i + 1];
            rgba[i + 2] = dump.ScreenshotPixels[i + 2];
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    private static void AddCount(Dictionary<string, long> counts, string key)
    {
        counts.TryGetValue(key, out var current);
        counts[key] = current + 1;
    }

    private static GsTextureContext? BuildTextureContext(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
            return null;

        if (!ZoneTextureCatalog.TryBuild(texturePath, out var catalog) || catalog == null)
            return null;

        return new GsTextureContext(catalog.CreateTextureResolver(), catalog.CreateDebugTex0Resolver(texturePath));
    }

    private static Dictionary<string, long> BuildPacketTypeCounts(GsDumpFile dump)
    {
        return dump.Packets
            .GroupBy(static packet => packet.Kind.ToString())
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => (long)group.Count());
    }

    private static Dictionary<string, GsTransferStats> BuildTransferStats(GsDumpFile dump)
    {
        var stats = new Dictionary<string, GsTransferStats>(StringComparer.Ordinal);
        foreach (var packet in dump.Packets.Where(static packet => packet.Kind == GsDumpPacketKind.Transfer))
        {
            var key = packet.Path?.ToString() ?? "Unknown";
            if (!stats.TryGetValue(key, out var row))
            {
                row = new GsTransferStats();
                stats[key] = row;
            }

            row.Packets++;
            row.Bytes += packet.Data.Length;
        }

        return stats;
    }

    private static void SaveRgba(string path, byte[] pixels, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        image.SaveAsPng(path);
    }
}
