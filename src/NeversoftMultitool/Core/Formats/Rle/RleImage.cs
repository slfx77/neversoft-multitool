namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
/// Result of converting a single RLE/BMR file.
/// </summary>
public sealed class RleConversionResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] RgbPixels { get; set; } = [];
    public bool Success => RgbPixels.Length > 0;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Converts Neversoft RLE and BMR bitmap files to RGB pixel data.
/// </summary>
public static class RleImage
{
    private const string RleMagic = "_RLE_16_";

    /// <summary>
    /// Converts an RLE or BMR file to RGB pixel data.
    /// </summary>
    public static RleConversionResult Convert(string filePath, int width)
    {
        var result = new RleConversionResult { Width = width };
        var ext = Path.GetExtension(filePath);

        if (!ext.Equals(".rle", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".bmr", StringComparison.OrdinalIgnoreCase))
        {
            result.ErrorMessage = $"Unsupported file extension: {ext}";
            return result;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            List<List<RgbColor>> canvas;

            if (ext.Equals(".bmr", StringComparison.OrdinalIgnoreCase))
            {
                canvas = LoadBmr(reader, width);
            }
            else if (VerifyFileIsRle(reader))
            {
                canvas = LoadRle(reader, width);
            }
            else
            {
                result.ErrorMessage = "No _RLE_16_ magic number found, invalid RLE image";
                return result;
            }

            canvas = ColumnUnshifter.Unshift(canvas);

            result.Height = canvas.Count;
            result.RgbPixels = FlattenToRgb(canvas, width);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Check if the file starts with the _RLE_16_ magic number.
    /// </summary>
    private static bool VerifyFileIsRle(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        var magic = System.Text.Encoding.ASCII.GetString(bytes);
        return magic == RleMagic;
    }

    /// <summary>
    /// Convert a 16-bit RGBA5551 color to 24-bit RGB.
    /// </summary>
    private static RgbColor ConvertRgba5551ToRgb(ushort rgba5551)
    {
        var r = (byte)((rgba5551 & 0x1F) << 3);
        var g = (byte)(((rgba5551 >> 5) & 0x1F) << 3);
        var b = (byte)(((rgba5551 >> 10) & 0x1F) << 3);
        return new RgbColor(r, g, b);
    }

    /// <summary>
    /// Load a raw BMR bitmap file.
    /// </summary>
    private static List<List<RgbColor>> LoadBmr(BinaryReader reader, int width)
    {
        var fileSize = reader.BaseStream.Length;
        var canvas = new List<List<RgbColor>>();
        var row = new List<RgbColor>();

        for (long i = 0; i < fileSize; i += 2)
        {
            var colorBytes = reader.ReadUInt16();
            var color = ConvertRgba5551ToRgb(colorBytes);
            row.Add(color);

            if (row.Count >= width)
            {
                canvas.Add(row);
                row = [];
            }
        }

        return canvas;
    }

    /// <summary>
    /// Load and decompress an RLE-encoded bitmap file.
    /// </summary>
    private static List<List<RgbColor>> LoadRle(BinaryReader reader, int maxWidth)
    {
        const int headerLength = 8;
        var fileSize = reader.BaseStream.Length;
        reader.BaseStream.Seek(headerLength, SeekOrigin.Begin);

        var decompressedFileSize = reader.ReadUInt32() - (uint)headerLength;
        var totalRows = (int)((decompressedFileSize / 2) / (uint)maxWidth);

        var canvas = new List<List<RgbColor>>();
        var row = new List<RgbColor>();
        var rowLen = 0;
        const ushort quantityMask = 0x7FFF;

        while (reader.BaseStream.Position + 1 < fileSize && canvas.Count < totalRows)
        {
            var byte1 = reader.ReadByte();
            var byte2 = reader.ReadByte();

            var quantity = (byte1 | (byte2 << 8)) & quantityMask;
            var isRepeat = (byte2 & 0x80) != 0;

            if (!isRepeat)
            {
                // READ_NUM_COLORS: read `quantity` distinct colors
                for (var i = 0; i < quantity; i++)
                {
                    var colorBytes = reader.ReadUInt16();
                    var color = ConvertRgba5551ToRgb(colorBytes);
                    row.Add(color);
                    rowLen++;

                    if (rowLen >= maxWidth)
                    {
                        canvas.Add(row);
                        row = [];
                        rowLen = 0;
                    }
                }
            }
            else
            {
                // REPEAT_COLOR: read one color, repeat `quantity` times
                var colorBytes = reader.ReadUInt16();
                var color = ConvertRgba5551ToRgb(colorBytes);

                for (var i = 0; i < quantity; i++)
                {
                    row.Add(color);
                    rowLen++;

                    if (rowLen >= maxWidth)
                    {
                        canvas.Add(row);
                        row = [];
                        rowLen = 0;
                    }
                }
            }
        }

        return canvas;
    }

    /// <summary>
    /// Flatten a 2D canvas of colors to a flat RGB byte array.
    /// </summary>
    private static byte[] FlattenToRgb(List<List<RgbColor>> canvas, int width)
    {
        if (canvas.Count == 0) return [];

        var height = canvas.Count;
        var pixels = new byte[width * height * 3];
        var offset = 0;

        foreach (var row in canvas)
        {
            foreach (var pixel in row)
            {
                pixels[offset++] = pixel.R;
                pixels[offset++] = pixel.G;
                pixels[offset++] = pixel.B;
            }

            // Pad incomplete rows with black
            var remaining = width - row.Count;
            offset += remaining * 3;
        }

        return pixels;
    }
}

/// <summary>
/// Simple RGB color value.
/// </summary>
internal readonly record struct RgbColor(byte R, byte G, byte B);
