using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     One animated colour ramp, already transformed into the domains the
///     consumers write into.
///     <para>
///         A PS1 colour pulse animates a PALETTE ENTRY, but the exported colour of
///         a vertex is not the palette colour — it passes through
///         <c>ResolvePaletteColor</c>, <c>ApplyPsxUntexturedBlend</c>,
///         <c>ToPsxPacketColor</c> and <c>DisplayRgbToLinear</c>, in three
///         different output domains. For additive and subtractive faces the
///         animation even moves out of RGB and into ALPHA. So rather than
///         publishing raw palette keys and asking every consumer to re-derive
///         that chain, the exporter runs each endpoint through the same helpers
///         the static bake uses and publishes the result. Consumers then lerp in
///         their chosen output domain.
///     </para>
///     <para>
///         Packet keys preserve the native byte-domain interpolation and are
///         validated against the emitted PS1 packet at frame zero. Portable
///         keys are transformed endpoints interpolated in linear output space;
///         a mid-interval playhead need not equal a static bake that interpolated
///         bytes before a nonlinear display-to-linear transform. Direct Blender
///         export therefore keeps its authored <c>Color</c> at frame 1, then uses
///         these portable interpolation semantics on later timeline frames.
///     </para>
/// </summary>
/// <param name="PacketKeys">Keys in the <c>_PSX_COLOR_0</c> (raw PS1 packet) domain.</param>
/// <param name="PortableKeys">Keys in the <c>COLOR_0</c> (linear glTF) domain.</param>
/// <param name="Intervals">Frames to hold each key before reaching the next, parallel to the key arrays.</param>
/// <param name="InitialKeyIndex">The serialized pre-tick key index.</param>
/// <param name="InitialAccumulator">The serialized pre-tick time accumulator, in frames.</param>
public sealed record ModelColourPulseChannel(
    IReadOnlyList<Vector4> PacketKeys,
    IReadOnlyList<Vector4> PortableKeys,
    IReadOnlyList<byte> Intervals,
    byte InitialKeyIndex,
    byte InitialAccumulator);
