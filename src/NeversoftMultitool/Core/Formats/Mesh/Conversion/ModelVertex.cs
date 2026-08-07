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

    /// <summary>
    ///     1-based index into the document's colour-pulse channel table, or 0
    ///     when this corner's colour is static. <see cref="Color" /> and
    ///     <see cref="PsxPacketColor" /> always hold the frame-zero value, so a
    ///     consumer that ignores this renders exactly what it renders today.
    ///     <para>
    ///         At the GLB boundary this is folded into <c>_PSX_FLAGS_0.Y</c> as
    ///         <c>1 + channel</c> rather than getting its own custom attribute:
    ///         Blender's glTF importer mis-zips a hash-randomized set of custom
    ///         attribute names against append-ordered arrays, so every extra
    ///         attribute makes that documented crash more likely. Y is the
    ///         Gouraud flag and a pulsed corner is Gouraud by definition, so
    ///         every shader test of the form <c>y &gt;= 0.5</c> stays correct.
    ///     </para>
    ///     <para>
    ///         RULE: this lane is CPU-side data, read only when setting up the
    ///         per-frame update. It must never be read by a shader. Y is a
    ///         varying, and a triangle mixing pulsed and unpulsed corners
    ///         interpolates it to meaningless intermediate values.
    ///     </para>
    /// </summary>
    public int ColourPulseChannel { get; init; }
}
