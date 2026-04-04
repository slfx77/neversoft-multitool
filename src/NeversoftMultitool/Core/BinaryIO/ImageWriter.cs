using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.BinaryIO;

public static class ImageWriter
{
    /// <summary>
    ///     Writes a flat RGBA byte array to a PNG file.
    ///     Used for PSX textures (which produce RGBA output).
    /// </summary>
    public static void WritePng(string outputPath, int width, int height, byte[] rgbaPixels)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = Image.LoadPixelData<Rgba32>(rgbaPixels, width, height);
        image.SaveAsPng(outputPath);
    }

    /// <summary>
    ///     Encodes a flat RGBA byte array to PNG bytes in memory.
    ///     Used for embedding textures in glTF files.
    /// </summary>
    public static byte[] WritePngToMemory(int width, int height, byte[] rgbaPixels)
    {
        using var image = Image.LoadPixelData<Rgba32>(rgbaPixels, width, height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Writes a flat RGB byte array to a PNG file.
    ///     Used for RLE/BMR bitmaps (which produce RGB output).
    /// </summary>
    public static void WritePngRgb(string outputPath, int width, int height, byte[] rgbPixels)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = Image.LoadPixelData<Rgb24>(rgbPixels, width, height);
        image.SaveAsPng(outputPath);
    }
}
