using System.Numerics;
using SharpGLTF.Materials;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxGltfMaterialContext
{
    public PsxGltfMaterialContext(PsxGltfWriter.TextureProvider? textureProvider)
    {
        TextureProvider = textureProvider;
        Untextured = CreateUntexturedMaterial();
    }

    public Dictionary<(uint Hash, bool SemiTrans), MaterialBuilder> Cache { get; } = new();

    public Dictionary<uint, (int Width, int Height)> TextureDimensions { get; } = new();

    public PsxGltfWriter.TextureProvider? TextureProvider { get; }

    public MaterialBuilder Untextured { get; }

    private static MaterialBuilder CreateUntexturedMaterial()
    {
        return new MaterialBuilder("untextured")
            .WithDoubleSide(true)
            .WithUnlitShader()
            .WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1f));
    }
}
