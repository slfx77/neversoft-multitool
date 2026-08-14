namespace NeversoftMultitool.Core.Formats.Texture;

internal static class TexturePreviewSelector
{
    public static (byte[] Rgba, int Width, int Height)? Select(Ps2TexResult result, int textureIndex)
    {
        if (!result.Success || textureIndex < 0 || textureIndex >= result.Textures.Count)
            return null;

        var texture = result.Textures[textureIndex];
        if (texture.Pixels is not { } pixels || texture.Width <= 0 || texture.Height <= 0)
            return null;

        var pixelCount = (long)texture.Width * texture.Height;
        if (pixelCount > Array.MaxLength / 4L || pixels.LongLength != pixelCount * 4)
            return null;

        return (pixels, texture.Width, texture.Height);
    }
}
