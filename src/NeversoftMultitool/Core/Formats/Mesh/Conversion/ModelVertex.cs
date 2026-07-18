using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public readonly record struct ModelVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Color,
    Vector2 TexCoord)
{
    /// <summary>
    ///     Optional raw PS1 GPU packet colour, normalized from byte RGB to
    ///     0..1 without a transfer-function conversion. <see cref="Color" />
    ///     remains the linear, portable glTF fallback; the live viewer uses
    ///     this value to reproduce display-domain Gouraud interpolation and
    ///     128-neutral texture modulation.
    /// </summary>
    public Vector4? PsxPacketColor { get; init; }

    /// <summary>
    ///     Per-primitive PS1 colour flags transported with
    ///     <see cref="PsxPacketColor" />. X marks authored textured geometry,
    ///     Y marks Gouraud shading, and Z marks packet data as valid. The zero
    ///     default deliberately keeps synthetic and non-PS1 vertices on the
    ///     portable <see cref="Color" /> path.
    /// </summary>
    public Vector3 PsxPrimitiveFlags { get; init; }

    /// <summary>
    ///     Optional PS1 UV-scroll/sine parameters. <see cref="TexCoord" /> is
    ///     always the portable frame-zero fallback used by consumers that do
    ///     not understand this application-specific animation metadata.
    /// </summary>
    public ModelTextureWibble? TextureWibble { get; init; }
}
