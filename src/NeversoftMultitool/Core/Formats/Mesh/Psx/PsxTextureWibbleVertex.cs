namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     One vertex in a texture-wibble record. The upper nibble of each
///     amplitude/phase byte is the sine amplitude and the lower nibble is its
///     phase.
/// </summary>
public readonly record struct PsxTextureWibbleVertex(
    byte U,
    byte V,
    byte UAmplitudePhase,
    byte VAmplitudePhase);
