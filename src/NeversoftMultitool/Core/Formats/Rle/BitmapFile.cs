using NeversoftMultitool.Core.BinaryIO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
///     Unified entry point for the bitmap converter. Neversoft formats
///     (.rle/.bmr/.zlb) route through <see cref="RleImage" />; N64 fullscreen
///     image records route through <see cref="N64TexFile" />; standard bitmaps
///     (.bmp/.tga/.tif/.png/.jpg/.gif, as shipped on THPS/Spider-Man discs)
///     decode via an extension-specific ImageSharp decoder with alpha preserved.
/// </summary>
public static class BitmapFile
{
    public static bool IsSupportedExtension(string path)
    {
        return IsNeversoftExtension(path) || IsStandardExtension(path) || IsN64ImageExtension(path);
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
               || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
               || IsTiffExtension(path);
    }

    /// <summary>
    ///     Authoring TIFFs shipped on disc (PG Wii 2,099, THAW GC 3,257,
    ///     THAW PC 3,175). Most are mip chains — see <see cref="TiffMipChain" />.
    /// </summary>
    public static bool IsTiffExtension(string path)
    {
        return path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsN64ImageExtension(string path)
    {
        return path.EndsWith(".img.n64", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Standard bitmaps carry their dimensions in the header, so width
    ///     auto-detection/override only applies to the Neversoft formats.
    /// </summary>
    public static bool HasSelfDescribedDimensions(string path)
    {
        return IsStandardExtension(path) || IsN64ImageExtension(path);
    }

    public static int DetectWidth(string filePath)
    {
        return IsN64ImageExtension(filePath) || IsStandardExtension(filePath)
            ? DetectWidth(File.ReadAllBytes(filePath), filePath)
            : RleImage.DetectWidth(filePath);
    }

    public static int DetectWidth(byte[] data, string extensionOrName)
    {
        if (IsN64ImageExtension(extensionOrName))
            return TryDecodeN64Image(data)?.Width ?? 0;
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
        if (IsN64ImageExtension(extensionOrName))
            return ConvertN64Image(data);
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

    /// <summary>
    ///     Writes the top image, then one <c>_mipN.png</c> companion per lower
    ///     stored level — the same convention the N64 texture exporter uses.
    ///     Only mip-chain TIFFs have lower levels; everything else writes one
    ///     file and reports 1. A lower level that fails to decode is skipped
    ///     rather than losing the top image, and is visible as a returned count
    ///     below <see cref="GetStandardLevelCount" />.
    /// </summary>
    public static int SavePngWithMipLevels(
        RleConversionResult result, byte[] data, string name, string outputPath)
    {
        SavePng(result, outputPath);

        var levels = IsStandardExtension(name) ? GetStandardLevelCount(data, name) : 1;
        if (levels <= 1) return 1;

        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var written = 1;
        for (var level = 1; level < levels; level++)
        {
            try
            {
                using var image = DecodeStandardLevel(data, name, level);
                var rgba = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(rgba);
                ImageWriter.WritePng(
                    Path.Combine(directory, $"{stem}_mip{level}.png"),
                    image.Width, image.Height, rgba);
                written++;
            }
            catch (Exception)
            {
                // A damaged lower level must not cost the caller the top image.
            }
        }

        return written;
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

    private static RleConversionResult ConvertN64Image(byte[] data)
    {
        var texture = TryDecodeN64Image(data);
        return texture == null
            ? new RleConversionResult { ErrorMessage = "Failed to decode N64 image record" }
            : new RleConversionResult
            {
                Width = texture.Width,
                Height = texture.Height,
                RgbaPixels = texture.Rgba
            };
    }

    private static N64TexFile.N64Texture? TryDecodeN64Image(byte[] data)
    {
        try
        {
            return N64TexFile.IsImageRecord(data)
                ? N64TexFile.DecodeImageRecord(data)
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
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
        // TGA has no magic bytes. Pick every decoder from the extension so a
        // mislabeled file fails closed instead of being accepted by sniffing.
        using var stream = new MemoryStream(data, false);
        // A mip-chain TIFF has differently sized pages, which an Image cannot
        // hold, so every level is loaded on its own (MaxFrames = 1 keeps the
        // decoder from walking into page 2 and throwing).
        var options = IsTiffExtension(name) || name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            ? new DecoderOptions { MaxFrames = 1 }
            : new DecoderOptions();
        if (IsTiffExtension(name))
            return TiffDecoder.Instance.Decode<Rgba32>(options, stream);
        if (name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            return TgaDecoder.Instance.Decode<Rgba32>(options, stream);
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return PngDecoder.Instance.Decode<Rgba32>(options, stream);
        if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return JpegDecoder.Instance.Decode<Rgba32>(options, stream);
        if (name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return GifDecoder.Instance.Decode<Rgba32>(options, stream);
        return BmpDecoder.Instance.Decode<Rgba32>(options, stream);
    }

    /// <summary>
    ///     Decodes one page of a standard bitmap. Level 0 is the image itself;
    ///     higher levels exist only for mip-chain TIFFs.
    /// </summary>
    public static Image<Rgba32> DecodeStandardLevel(byte[] data, string name, int level)
    {
        if (level == 0)
            return DecodeStandard(data, name);

        if (!IsTiffExtension(name))
            throw new ArgumentOutOfRangeException(nameof(level), "Only TIFF carries additional levels");

        return DecodeStandard(TiffMipChain.ExtractLevel(data, level), name);
    }

    /// <summary>
    ///     Number of stored image levels: 1 for everything except a multi-page
    ///     TIFF, which reports its whole mip chain.
    /// </summary>
    public static int GetStandardLevelCount(byte[] data, string name)
    {
        if (!IsTiffExtension(name)) return 1;

        return Math.Max(1, TiffMipChain.GetLevelCount(data));
    }

    private static int DetectStandardWidth(byte[] data, string name)
    {
        // Header-only identify — the folder scan calls this once per file, so
        // a full pixel decode here would stall large directories.
        try
        {
            using var stream = new MemoryStream(data, false);
            var options = new DecoderOptions();
            if (IsTiffExtension(name))
                return TiffDecoder.Instance.Identify(options, stream).Width;
            if (name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                return TgaDecoder.Instance.Identify(options, stream).Width;
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return PngDecoder.Instance.Identify(options, stream).Width;
            if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                return JpegDecoder.Instance.Identify(options, stream).Width;
            if (name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                return GifDecoder.Instance.Identify(options, stream).Width;
            return BmpDecoder.Instance.Identify(options, stream).Width;
        }
        catch
        {
            return 0;
        }
    }
}
