namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
///     Result of converting a single bitmap file. Neversoft RLE/BMR decodes
///     fill <see cref="RgbPixels" />; standard BMP/TGA and N64 fullscreen-image
///     decodes fill <see cref="RgbaPixels" /> (alpha preserved).
/// </summary>
public sealed class RleConversionResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] RgbPixels { get; set; } = [];
    public byte[]? RgbaPixels { get; set; }
    public bool Success => RgbPixels.Length > 0 || RgbaPixels is { Length: > 0 };
    public bool WidthAutoDetected { get; set; }
    public string? ErrorMessage { get; set; }
}
