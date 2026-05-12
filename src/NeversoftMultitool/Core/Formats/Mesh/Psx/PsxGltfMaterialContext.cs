using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
using SharpGLTF.Materials;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxGltfMaterialContext
{
    public PsxGltfMaterialContext(MeshChecksumTextureResolver? textureProvider)
    {
        TextureProvider = textureProvider;
        Untextured = CreateUntexturedMaterial();
    }

    public Dictionary<(uint Hash, bool SemiTrans), MaterialBuilder> Cache { get; } = new();

    public Dictionary<uint, (int Width, int Height)> TextureDimensions { get; } = new();

    public MeshChecksumTextureResolver? TextureProvider { get; }

    public MaterialBuilder Untextured { get; }

    private static MaterialBuilder CreateUntexturedMaterial()
    {
        return new MaterialBuilder("untextured")
            .WithDoubleSide(true)
            .WithUnlitShader()
            .WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1f));
    }
}
