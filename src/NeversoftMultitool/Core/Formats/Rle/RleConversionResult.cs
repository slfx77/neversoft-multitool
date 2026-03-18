namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
///     Result of converting a single RLE/BMR file.
/// </summary>
public sealed class RleConversionResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] RgbPixels { get; set; } = [];
    public bool Success => RgbPixels.Length > 0;
    public bool WidthAutoDetected { get; set; }
    public string? ErrorMessage { get; set; }
}
