using NeversoftMultitool.Core.BinaryIO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
///     Unified entry point for the bitmap converter. Neversoft formats
///     (.rle/.bmr/.zlb) route through <see cref="RleImage" />; standard
///     bitmaps (.bmp/.tga, as shipped on THPS/Spider-Man discs) decode via
///     ImageSharp with alpha preserved.
/// </summary>
public static class BitmapFile
{
    public static bool IsSupportedExtension(string path)
    {
        return IsNeversoftExtension(path) || IsStandardExtension(path);
    }

    public static bool IsNeversoftExtension(string path)
    {
        return path.EndsWith(".rle", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".bmr", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".zlb", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStandardExtension(string path)
    {
        return path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Standard bitmaps carry their dimensions in the header, so width
    ///     auto-detection/override only applies to the Neversoft formats.
    /// </summary>
    public static bool HasSelfDescribedDimensions(string path)
    {
        return IsStandardExtension(path);
    }

    public static int DetectWidth(string filePath)
    {
        return IsStandardExtension(filePath)
            ? DetectStandardWidth(File.ReadAllBytes(filePath), filePath)
            : RleImage.DetectWidth(filePath);
    }

    public static int DetectWidth(byte[] data, string extensionOrName)
    {
        return IsStandardExtension(extensionOrName)
            ? DetectStandardWidth(data, extensionOrName)
            : RleImage.DetectWidth(data, extensionOrName);
    }

    /// <summary>
    ///     Convert any supported bitmap to pixel data. <paramref name="width" />
    ///     is ignored for self-described formats.
    /// </summary>
    public static RleConversionResult Convert(byte[] data, string extensionOrName, int? width = null)
    {
        return IsStandardExtension(extensionOrName)
            ? ConvertStandard(data, extensionOrName)
            : RleImage.Convert(data, extensionOrName, width);
    }

    /// <summary>Write a conversion result as PNG, preserving alpha when present.</summary>
    public static void SavePng(RleConversionResult result, string outputPath)
    {
        if (result.RgbaPixels is { Length: > 0 })
            ImageWriter.WritePng(outputPath, result.Width, result.Height, result.RgbaPixels);
        else
            ImageWriter.WritePngRgb(outputPath, result.Width, result.Height, result.RgbPixels);
    }

    private static RleConversionResult ConvertStandard(byte[] data, string name)
    {
        try
        {
            return DecodeStandardToResult(data, name);
        }
        catch (Exception ex)
        {
            var repaired = TryRepairShortBmpPalette(data);
            if (repaired != null)
            {
                try
                {
                    return DecodeStandardToResult(repaired, name);
                }
                catch
                {
                    // Fall through to the original error.
                }
            }

            return new RleConversionResult
            {
                ErrorMessage = $"Failed to decode {Path.GetExtension(name)}: {ex.Message}"
            };
        }
    }

    private static RleConversionResult DecodeStandardToResult(byte[] data, string name)
    {
        using var image = DecodeStandard(data, name);
        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return new RleConversionResult
        {
            Width = image.Width,
            Height = image.Height,
            RgbaPixels = rgba
        };
    }

    /// <summary>
    ///     Some shipped paletted BMPs (e.g. THPS2 DC LOADBAR.BMP, biClrUsed=255)
    ///     are rejected by ImageSharp's BMP decoder despite being spec-legal.
    ///     Pad the palette to the full 2^bpp entries and clear biClrUsed, then
    ///     let the caller retry.
    /// </summary>
    private static byte[]? TryRepairShortBmpPalette(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') return null;

        var headerSize = BitConverter.ToInt32(data, 14);
        if (headerSize != 40) return null;

        var bpp = BitConverter.ToInt16(data, 28);
        if (bpp is not (1 or 4 or 8)) return null;

        var fullEntries = 1 << bpp;
        var clrUsed = BitConverter.ToInt32(data, 46);
        if (clrUsed <= 0 || clrUsed >= fullEntries) return null;

        var pixelOffset = BitConverter.ToInt32(data, 10);
        if (pixelOffset != 14 + headerSize + clrUsed * 4) return null;
        if (pixelOffset > data.Length) return null;

        var pad = (fullEntries - clrUsed) * 4;
        var repaired = new byte[data.Length + pad];
        Array.Copy(data, 0, repaired, 0, pixelOffset);
        Array.Copy(data, pixelOffset, repaired, pixelOffset + pad, data.Length - pixelOffset);
        BitConverter.TryWriteBytes(repaired.AsSpan(2), repaired.Length);
        BitConverter.TryWriteBytes(repaired.AsSpan(10), pixelOffset + pad);
        BitConverter.TryWriteBytes(repaired.AsSpan(46), 0);
        return repaired;
    }

    private static Image<Rgba32> DecodeStandard(byte[] data, string name)
    {
        // TGA has no magic bytes, so pick the decoder from the extension
        // instead of relying on ImageSharp's format sniffing.
        using var stream = new MemoryStream(data, false);
        var options = new DecoderOptions();
        return name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
            ? TgaDecoder.Instance.Decode<Rgba32>(options, stream)
            : BmpDecoder.Instance.Decode<Rgba32>(options, stream);
    }

    private static int DetectStandardWidth(byte[] data, string name)
    {
        // Header-only identify — the folder scan calls this once per file, so
        // a full pixel decode here would stall large directories.
        try
        {
            using var stream = new MemoryStream(data, false);
            var options = new DecoderOptions();
            var info = name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                ? TgaDecoder.Instance.Identify(options, stream)
                : BmpDecoder.Instance.Identify(options, stream);
            return info.Width;
        }
        catch
        {
            return 0;
        }
    }
}
