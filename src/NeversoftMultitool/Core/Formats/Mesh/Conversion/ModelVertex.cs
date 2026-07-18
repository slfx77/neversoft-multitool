using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public readonly record struct ModelVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Color,
    Vector2 TexCoord)
{
    /// <summary>
    ///     Optional PS1 UV-scroll/sine parameters. <see cref="TexCoord" /> is
    ///     always the portable frame-zero fallback used by consumers that do
    ///     not understand this application-specific animation metadata.
    /// </summary>
    public ModelTextureWibble? TextureWibble { get; init; }
}
