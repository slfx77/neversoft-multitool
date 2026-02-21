namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Holds a complete mip chain for a PowerVR texture.
/// Levels are ordered from largest (main surface) to smallest (1x1), matching DDS convention.
/// </summary>
public sealed class PvrMipChain
{
    /// <summary>
    /// Mip levels ordered largest to smallest. Index 0 is the main surface.
    /// Each level is a flat ushort[] of width * height pixels for that level.
    /// </summary>
    public required List<ushort[]> Levels { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    public int MipCount => Levels.Count;
    public ushort[] MainSurface => Levels[0];

    /// <summary>
    /// Renders all mip levels into a single RGBA atlas image for visual verification.
    /// Layout: main surface on the left, smaller levels stacked vertically to the right.
    /// </summary>
    public (byte[] Rgba, int AtlasWidth, int AtlasHeight) ToAtlasRgba(uint pixelFormat)
    {
        // Atlas dimensions: main width + half main width, full main height
        var atlasWidth = Width + Width / 2;
        var atlasHeight = Height;
        var atlas = new byte[atlasWidth * atlasHeight * 4];

        // Convert all levels to RGBA upfront
        var rgbaLevels = new byte[Levels.Count][];
        var dim = Width;
        for (var i = 0; i < Levels.Count; i++)
        {
            var d = Math.Max(dim >> i, 1);
            rgbaLevels[i] = ColorHelpers.Convert16BitTextureToRgba(pixelFormat, d, d, Levels[i]);
        }

        // Blit main surface at (0, 0)
        BlitRgba(atlas, atlasWidth, rgbaLevels[0], Width, Height, destX: 0, destY: 0);

        // Blit smaller levels stacked to the right of the main surface
        var yOffset = 0;
        var mipDim = Width / 2;

        for (var i = 1; i < Levels.Count && mipDim >= 1; i++)
        {
            BlitRgba(atlas, atlasWidth, rgbaLevels[i], mipDim, mipDim, destX: Width, destY: yOffset);
            yOffset += mipDim;
            mipDim /= 2;
        }

        return (atlas, atlasWidth, atlasHeight);
    }

    private static void BlitRgba(byte[] atlas, int atlasStride, byte[] rgba,
        int w, int h, int destX, int destY)
    {
        for (var row = 0; row < h; row++)
        {
            var srcOffset = row * w * 4;
            var dstOffset = ((destY + row) * atlasStride + destX) * 4;
            Array.Copy(rgba, srcOffset, atlas, dstOffset, w * 4);
        }
    }
}
