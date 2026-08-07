namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Marks a primitive as carrying animated PS1 palette pulses (tagged chunk
///     7). The glTF exporter publishes it as mesh extras
///     <c>neversoftColourPulse</c>; the in-app viewer then evaluates the
///     document's channel table each frame and rewrites the primitive's colour
///     attributes.
///     <para>
///         The per-vertex channel index rides in <c>_PSX_FLAGS_0.Y</c> rather
///         than a new custom attribute — see <see cref="ModelVertex.ColourPulseChannel" />.
///     </para>
/// </summary>
public sealed record PsxColourPulseMetadata(int ChannelCount)
    : NativeRenderMetadata("psx_colour_pulse");
