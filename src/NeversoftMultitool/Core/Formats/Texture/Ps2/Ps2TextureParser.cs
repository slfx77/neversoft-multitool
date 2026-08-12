using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

/// <summary>
///     Dispatches PS2 texture data across the standard TEX/IMG formats and
///     THAW's version-6 scene TEX format.
/// </summary>
public static class Ps2TextureParser
{
    public static Ps2TexResult Parse(byte[] data)
    {
        var result = Ps2TexFile.Parse(data);
        return result.Success ? result : ThawSceneTexFile.Parse(data);
    }
}
