namespace NeversoftMultitool.Core.Formats.Texture;

internal static class TexturePreviewSelector
{
    public static (byte[] Rgba, int Width, int Height)? Select(Ps2TexResult result, int textureIndex)
    {
        if (!result.Success || textureIndex < 0 || textureIndex >= result.Textures.Count)
            return null;

        var texture = result.Textures[textureIndex];
        return texture.Pixels is { } pixels
            ? (pixels, texture.Width, texture.Height)
            : null;
    }
}
