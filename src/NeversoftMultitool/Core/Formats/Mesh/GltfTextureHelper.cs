using NeversoftMultitool.Core.Formats.Archives;
using Pfim;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Handles texture discovery, loading, DDS decoding, and luminance-to-alpha conversion
///     for the DDM-to-glTF pipeline.
/// </summary>
internal static class GltfTextureHelper
{
    /// <summary>
    ///     Builds a list of directories to search for texture files, given a base texture path
    ///     and an optional DDM name for subdirectory matching.
    /// </summary>
    internal static List<string> BuildTextureSearchPaths(string? texturePath, string? ddmName)
    {
        var dirs = new List<string>();
        if (string.IsNullOrEmpty(texturePath))
            return dirs;

        // DDX extraction creates subdirectories matching DDM name (e.g. textures/skware/)
        if (!string.IsNullOrEmpty(ddmName))
        {
            // DDX archive extraction nests: DDX/<name>/<name>/*.DDS
            var nestedDir = Path.Combine(texturePath, ddmName, ddmName);
            if (Directory.Exists(nestedDir))
                dirs.Add(nestedDir);

            var subDir = Path.Combine(texturePath, ddmName);
            if (Directory.Exists(subDir))
                dirs.Add(subDir);
        }

        // Also search the root texture directory
        if (Directory.Exists(texturePath))
            dirs.Add(texturePath);

        return dirs;
    }

    /// <summary>
    ///     Loads DDX texture archives for a level (both base and _o variant).
    /// </summary>
    internal static Dictionary<string, byte[]>? LoadDdxTextures(string? ddxPath, string levelName)
    {
        if (string.IsNullOrEmpty(ddxPath) || !Directory.Exists(ddxPath))
            return null;

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Try level DDX
        var levelDdx = FindCompanionFile(ddxPath, levelName, ".ddx");
        if (levelDdx != null)
            MergeDdxEntries(result, DdxArchive.ReadAllEntries(levelDdx));

        // Try objects DDX
        var objectsDdx = FindCompanionFile(ddxPath, levelName + "_o", ".ddx");
        if (objectsDdx != null)
            MergeDdxEntries(result, DdxArchive.ReadAllEntries(objectsDdx));

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    ///     Searches DDX cache and texture directories for a matching texture.
    ///     DDS files are decoded and converted to PNG bytes in memory.
    /// </summary>
    internal static (byte[] Bytes, bool HasAlpha)? LoadTexture(
        List<string> textureDirs, string textureName,
        Dictionary<string, byte[]>? ddxTextures)
    {
        // Check DDX in-memory cache first
        if (ddxTextures != null && ddxTextures.TryGetValue(textureName, out var ddsBytes))
        {
            using var ms = new MemoryStream(ddsBytes);
            return DecodeDdsToPng(ms);
        }

        // Search directories
        foreach (var dir in textureDirs)
        {
            // Try PNG first (native glTF format)
            var pngFiles = Directory.GetFiles(dir, textureName + ".png",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (pngFiles.Length > 0)
                return (File.ReadAllBytes(pngFiles[0]), false);

            // Try DDS (decode to PNG)
            var ddsFiles = Directory.GetFiles(dir, textureName + ".dds",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (ddsFiles.Length > 0)
                return DecodeDdsToPng(ddsFiles[0]);
        }

        return null;
    }

    /// <summary>
    ///     Converts a texture for additive blend approximation in glTF.
    ///     Converts texture to white RGB with luminance-derived alpha.
    ///     Dark pixels become transparent (invisible, like additive black)
    ///     and bright pixels become white with proportional opacity.
    ///     Existing alpha is used as a multiplier (for DXT3/DXT5 textures
    ///     where alpha may mask out parts of the glow independently).
    /// </summary>
    internal static byte[] ConvertLuminanceToAlpha(byte[] pngBytes)
    {
        using var img = Image.Load<Rgba32>(pngBytes);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    var lum = (pixel.R * 77 + pixel.G * 150 + pixel.B * 29) >> 8;
                    var alpha = (lum * pixel.A) >> 8;
                    pixel = new Rgba32(255, 255, 255, (byte)alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Detects magenta (255, 0, 255) color-key backgrounds in textures and converts
    ///     them to alpha transparency. Uses Manhattan distance from magenta so antialiased
    ///     edges get smooth alpha gradients instead of hard pink fringing.
    ///     Only modifies textures that contain at least one exact magenta pixel.
    /// </summary>
    /// <returns>Processed PNG bytes and whether the texture has any alpha content.</returns>
    internal static (byte[] Bytes, bool HasAlpha) ApplyColorKey(byte[] pngBytes)
    {
        using var img = Image.Load<Rgba32>(pngBytes);
        var (hasMagenta, hasExistingAlpha) = ScanForColorKey(img);

        if (!hasMagenta)
            return (pngBytes, hasExistingAlpha);

        // Conversion pass: compute alpha from distance to magenta (255, 0, 255).
        // Manhattan distance: dist = (255 - R) + G + (255 - B).
        // Scale ×4 → full opacity at dist ≥ 64, covering the DXT antialiasing zone.
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    var dist = 255 - pixel.R + pixel.G + (255 - pixel.B);
                    var alpha = Math.Clamp(dist * 4, 0, 255);
                    alpha = Math.Min(alpha, pixel.A);

                    if (alpha == 0)
                        pixel = new Rgba32(0, 0, 0, 0);
                    else
                        pixel = new Rgba32(pixel.R, pixel.G, pixel.B, (byte)alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return (ms.ToArray(), true);
    }

    /// <summary>Scans an image for exact magenta pixels and any existing alpha content.</summary>
    private static (bool HasMagenta, bool HasExistingAlpha) ScanForColorKey(Image<Rgba32> img)
    {
        var hasMagenta = false;
        var hasExistingAlpha = false;
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !hasMagenta; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    hasExistingAlpha |= pixel.A < 255;
                    if (pixel.R == 255 && pixel.G == 0 && pixel.B == 255)
                    {
                        hasMagenta = true;
                        break;
                    }
                }
            }
        });

        return (hasMagenta, hasExistingAlpha);
    }

    /// <summary>
    ///     Converts a texture for additive/subtractive blend approximation in glTF.
    ///     Sets alpha = max(R,G,B) (luminance) and RGB = specified color.
    ///     Additive (r=255,g=255,b=255): bright areas render as opaque white overlay, dark = transparent.
    ///     Subtractive (r=0,g=0,b=0): bright areas render as dark shadow overlay, dark = transparent.
    /// </summary>
    internal static byte[] ConvertBlendTexture(byte[] pngBytes, byte r, byte g, byte b)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    var luminance = Math.Max(p.R, Math.Max(p.G, p.B));
                    row[x] = new Rgba32(r, g, b, (byte)(luminance * p.A / 255));
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    internal static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }

    private static void MergeDdxEntries(Dictionary<string, byte[]> target, Dictionary<string, byte[]> source)
    {
        foreach (var (name, bytes) in source)
            target.TryAdd(name, bytes);
    }

    /// <summary>
    ///     Decodes a DDS file to PNG bytes using Pfim for DXT decompression
    ///     and ImageSharp for PNG encoding.
    /// </summary>
    private static (byte[] Bytes, bool HasAlpha)? DecodeDdsToPng(string ddsPath)
    {
        try
        {
            using var image = Pfimage.FromFile(ddsPath);
            return EncodePfimToPng(image);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Decodes DDS bytes from a stream to PNG bytes.
    /// </summary>
    private static (byte[] Bytes, bool HasAlpha)? DecodeDdsToPng(Stream ddsStream)
    {
        try
        {
            using var image = Pfimage.FromStream(ddsStream);
            return EncodePfimToPng(image);
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] Bytes, bool HasAlpha)? EncodePfimToPng(IImage image)
    {
        using var ms = new MemoryStream();

        if (image.Format == ImageFormat.Rgba32)
        {
            // Check if any pixel actually has non-opaque alpha (BGRA layout: alpha at offset 3)
            var hasAlpha = false;
            var data = image.Data;
            var stride = image.Stride;
            for (var y = 0; y < image.Height && !hasAlpha; y++)
            {
                var rowStart = y * stride;
                for (var x = 0; x < image.Width; x++)
                {
                    if (data[rowStart + x * 4 + 3] < 255)
                    {
                        hasAlpha = true;
                        break;
                    }
                }
            }

            using var img = Image.LoadPixelData<Bgra32>(image.Data, image.Width, image.Height);
            img.SaveAsPng(ms);
            return (ms.ToArray(), hasAlpha);
        }

        if (image.Format == ImageFormat.Rgb24)
        {
            using var img = Image.LoadPixelData<Bgr24>(image.Data, image.Width, image.Height);
            img.SaveAsPng(ms);
            return (ms.ToArray(), false);
        }

        return null;
    }
}
