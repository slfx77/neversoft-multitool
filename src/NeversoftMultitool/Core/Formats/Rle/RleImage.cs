using System.Text;

namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
///     Converts Neversoft RLE and BMR bitmap files to RGB pixel data.
/// </summary>
public static class RleImage
{
    private const string RleMagic = "_RLE_16_";

    private static readonly int[] CandidateWidths = [512, 640, 768, 320, 256, 384, 480, 1024];
    private static readonly HashSet<long> PreferredHeights = [240, 256, 480, 512];

    /// <summary>
    ///     Detects the image width for an RLE or BMR file without fully decoding it.
    /// </summary>
    public static int DetectWidth(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        var totalPixels = GetTotalPixelCount(filePath, ext);
        return GuessWidth(totalPixels);
    }

    /// <summary>
    ///     Converts an RLE or BMR file to RGB pixel data with auto-detected width.
    /// </summary>
    public static RleConversionResult Convert(string filePath)
    {
        var ext = Path.GetExtension(filePath);

        if (!ext.Equals(".rle", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".bmr", StringComparison.OrdinalIgnoreCase))
        {
            return new RleConversionResult { ErrorMessage = $"Unsupported file extension: {ext}" };
        }

        var totalPixels = GetTotalPixelCount(filePath, ext);
        var width = GuessWidth(totalPixels);
        var result = Convert(filePath, width);
        result.WidthAutoDetected = true;
        return result;
    }

    /// <summary>
    ///     Converts an RLE or BMR file to RGB pixel data.
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
                if (IsBmpWrappedRle(reader))
                    return LoadRleAsBmp(reader);

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
    ///     Check if the file starts with the _RLE_16_ magic number.
    /// </summary>
    private static bool VerifyFileIsRle(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        var magic = Encoding.ASCII.GetString(bytes);
        return magic == RleMagic;
    }

    /// <summary>
    ///     Convert a 16-bit RGBA5551 color to 24-bit RGB.
    /// </summary>
    private static RgbColor ConvertRgba5551ToRgb(ushort rgba5551)
    {
        var r = (byte)((rgba5551 & 0x1F) << 3);
        var g = (byte)(((rgba5551 >> 5) & 0x1F) << 3);
        var b = (byte)(((rgba5551 >> 10) & 0x1F) << 3);
        return new RgbColor(r, g, b);
    }

    /// <summary>
    ///     Load a raw BMR bitmap file.
    /// </summary>
    private static List<List<RgbColor>> LoadBmr(BinaryReader reader, int width)
    {
        // BMR files have 8 bytes of trailing zero-padding
        var dataSize = Math.Max(0, reader.BaseStream.Length - 8);
        var canvas = new List<List<RgbColor>>();
        var row = new List<RgbColor>();

        for (long i = 0; i < dataSize; i += 2)
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
    ///     Load and decompress an RLE-encoded bitmap file.
    /// </summary>
    private static List<List<RgbColor>> LoadRle(BinaryReader reader, int maxWidth)
    {
        const int headerLength = 8;
        var fileSize = reader.BaseStream.Length;
        reader.BaseStream.Seek(headerLength, SeekOrigin.Begin);

        var decompressedFileSize = reader.ReadUInt32() - headerLength;
        var totalRows = (int)(decompressedFileSize / 2 / (uint)maxWidth);

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
    ///     Reads total pixel count from an RLE or BMR file without fully decoding it.
    /// </summary>
    private static long GetTotalPixelCount(string filePath, string ext)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        if (ext.Equals(".bmr", StringComparison.OrdinalIgnoreCase))
        {
            // BMR files have 8 bytes of trailing zero-padding
            return stream.Length >= 8 ? (stream.Length - 8) / 2 : stream.Length / 2;
        }

        // RLE: skip 8-byte magic, read decompressed size at offset 8
        if (stream.Length < 12) return 0;
        reader.ReadBytes(8); // skip magic
        var decompressedFileSize = reader.ReadUInt32();
        return (decompressedFileSize - 8) / 2;
    }

    /// <summary>
    ///     Guesses image width from total pixel count by trying common widths.
    ///     Prefers candidates that produce standard PS1 display heights (240, 256, 480, 512).
    /// </summary>
    internal static int GuessWidth(long totalPixels)
    {
        if (totalPixels <= 0) return 512;

        var bestWidth = -1;
        var bestHasPreferredHeight = false;

        foreach (var candidate in CandidateWidths)
        {
            if (totalPixels % candidate != 0) continue;
            var height = totalPixels / candidate;
            if (height is < 1 or > 4096) continue;

            var hasPreferredHeight = PreferredHeights.Contains(height);

            if (bestWidth < 0 || (hasPreferredHeight && !bestHasPreferredHeight))
            {
                bestWidth = candidate;
                bestHasPreferredHeight = hasPreferredHeight;
            }
        }

        return bestWidth > 0 ? bestWidth : 512;
    }

    /// <summary>
    ///     Checks if an RLE file contains a BMP wrapped inside the RLE stream.
    ///     Some Dreamcast RLE files (Spider-Man DC) decompress to 24-bit BMP data
    ///     instead of raw RGBA5551 pixels.
    /// </summary>
    private static bool IsBmpWrappedRle(BinaryReader reader)
    {
        // After VerifyFileIsRle, position is at 8
        var savedPos = reader.BaseStream.Position;

        reader.ReadUInt32(); // skip decompressedSize, now at 12

        // Read first RLE control word
        var byte1 = reader.ReadByte();
        var byte2 = reader.ReadByte();
        var quantity = (byte1 | (byte2 << 8)) & 0x7FFF;
        var isRepeat = (byte2 & 0x80) != 0;

        var isBmp = false;
        if (!isRepeat && quantity >= 1)
        {
            var firstValue = reader.ReadUInt16();
            isBmp = firstValue == 0x4D42; // 'BM' in LE
        }

        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
        return isBmp;
    }

    /// <summary>
    ///     Decompresses RLE data to raw uint16 values.
    ///     Expects the reader to be positioned at the start of the compressed data (offset 12).
    /// </summary>
    private static List<ushort> DecompressRle(BinaryReader reader)
    {
        var fileSize = reader.BaseStream.Length;
        var values = new List<ushort>();
        const ushort quantityMask = 0x7FFF;

        while (reader.BaseStream.Position + 1 < fileSize)
        {
            var b1 = reader.ReadByte();
            var b2 = reader.ReadByte();
            var quantity = (b1 | (b2 << 8)) & quantityMask;
            var isRepeat = (b2 & 0x80) != 0;

            if (!isRepeat)
            {
                for (var i = 0; i < quantity; i++)
                    values.Add(reader.ReadUInt16());
            }
            else
            {
                var value = reader.ReadUInt16();
                for (var i = 0; i < quantity; i++)
                    values.Add(value);
            }
        }

        return values;
    }

    /// <summary>
    ///     Decompresses an RLE stream and parses the result as a 24-bit BMP file.
    ///     Used for Dreamcast RLE files where the compressed data wraps a BMP.
    /// </summary>
    private static RleConversionResult LoadRleAsBmp(BinaryReader reader)
    {
        // Position is at 8 (after magic verification)
        reader.ReadUInt32(); // skip decompressedSize, now at 12

        var decompressed = DecompressRle(reader);

        // Convert uint16 array to byte array (LE)
        var bytes = new byte[decompressed.Count * 2];
        for (var i = 0; i < decompressed.Count; i++)
        {
            bytes[i * 2] = (byte)(decompressed[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(decompressed[i] >> 8);
        }

        return ParseBmpBytes(bytes);
    }

    /// <summary>
    ///     Parses a 24-bit BMP byte array into an RleConversionResult.
    /// </summary>
    private static RleConversionResult ParseBmpBytes(byte[] bytes)
    {
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            return new RleConversionResult { ErrorMessage = "Expected BMP data after RLE decompression" };

        var pixelOffset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var rawHeight = BitConverter.ToInt32(bytes, 22);
        var height = Math.Abs(rawHeight);
        var bpp = BitConverter.ToUInt16(bytes, 28);
        var topDown = rawHeight < 0;

        if (bpp != 24)
            return new RleConversionResult { ErrorMessage = $"Unsupported BMP bit depth in RLE: {bpp}" };

        var rowStride = (width * 3 + 3) / 4 * 4; // BMP rows are 4-byte aligned
        var rgbPixels = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            var srcRow = topDown ? y : height - 1 - y; // BMP is bottom-up by default
            var srcOffset = pixelOffset + srcRow * rowStride;
            var dstOffset = y * width * 3;

            for (var x = 0; x < width; x++)
            {
                var si = srcOffset + x * 3;
                if (si + 2 >= bytes.Length) continue;

                // BMP stores BGR, convert to RGB
                rgbPixels[dstOffset + x * 3] = bytes[si + 2];
                rgbPixels[dstOffset + x * 3 + 1] = bytes[si + 1];
                rgbPixels[dstOffset + x * 3 + 2] = bytes[si];
            }
        }

        return new RleConversionResult
        {
            Width = width,
            Height = height,
            RgbPixels = rgbPixels
        };
    }

    /// <summary>
    ///     Flatten a 2D canvas of colors to a flat RGB byte array.
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
