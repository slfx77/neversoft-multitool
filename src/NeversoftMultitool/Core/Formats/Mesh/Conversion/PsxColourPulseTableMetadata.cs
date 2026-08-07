namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     The document's colour-pulse channel table, published once as glTF SCENE
///     extras (<c>neversoftColourPulseChannels</c>) rather than repeated per
///     mesh.
///     <para>
///         Document scope is required, not merely cheaper: a level document
///         merges the <c>_g</c> geometry, the <c>_o</c> object bank and
///         <c>items.psx</c>, each with its own palette and its own pulses, so
///         channel indices must be unique across the whole document. It is also
///         far cheaper — a 60-pulse table is roughly 60 KB once, versus tens of
///         megabytes if replicated across every pulsed mesh in a level.
///     </para>
/// </summary>
public sealed record PsxColourPulseTableMetadata(IReadOnlyList<ModelColourPulseChannel> Channels)
    : NativeRenderMetadata("psx_colour_pulse_table");
