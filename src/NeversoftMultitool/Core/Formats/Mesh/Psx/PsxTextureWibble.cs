namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Per-face PS1 texture motion from tagged chunk 6. The engine evaluates
///     these values against its frame counter; the parser also applies each
///     vertex's base U/V as the deterministic static frame used by glTF.
/// </summary>
public sealed class PsxTextureWibble
{
    public required short UVelocity { get; init; }
    public required short VVelocity { get; init; }
    public required int Frequency { get; init; }
    public required bool ZeroUAmplitudes { get; init; }
    public required bool ZeroVAmplitudes { get; init; }
    public required PsxTextureWibbleVertex[] Vertices { get; init; }
}

/// <summary>
///     One vertex in a PS1 texture-wibble record. The upper nibble of each
///     amplitude/phase byte is the sine amplitude and the lower nibble is its
///     phase.
/// </summary>
public readonly record struct PsxTextureWibbleVertex(
    byte U,
    byte V,
    byte UAmplitudePhase,
    byte VAmplitudePhase);
