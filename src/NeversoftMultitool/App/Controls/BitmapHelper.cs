using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NeversoftMultitool;

/// <summary>
///     Converts raw pixel data to WinUI 3 WriteableBitmap for display.
/// </summary>
internal static class BitmapHelper
{
    /// <summary>
    ///     Creates a WriteableBitmap from RGBA pixel data (converts to BGRA).
    /// </summary>
    internal static WriteableBitmap CreateFromRgba(int width, int height, byte[] rgba)
    {
        var bitmap = new WriteableBitmap(width, height);
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2]; // B
            bgra[i + 1] = rgba[i + 1]; // G
            bgra[i + 2] = rgba[i]; // R
            bgra[i + 3] = rgba[i + 3]; // A
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    ///     Creates a WriteableBitmap from RGB pixel data (converts to BGRA with full opacity).
    /// </summary>
    internal static WriteableBitmap CreateFromRgb(int width, int height, byte[] rgb)
    {
        var bitmap = new WriteableBitmap(width, height);
        var bgra = new byte[width * height * 4];
        var rgbIndex = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = rgb[rgbIndex + 2]; // B
            bgra[i + 1] = rgb[rgbIndex + 1]; // G
            bgra[i + 2] = rgb[rgbIndex]; // R
            bgra[i + 3] = 0xFF; // A
            rgbIndex += 3;
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
