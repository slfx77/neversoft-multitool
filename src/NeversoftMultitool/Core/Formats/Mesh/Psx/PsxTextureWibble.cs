namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Per-face PSX/PC texture motion from tagged chunk 6. The engine evaluates
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

    /// <summary>
    ///     True for the widened Spider-Man PC/DC v6 geometry path, whose
    ///     renderer keeps the base UVs stored on the face. Its legacy byte
    ///     slots are non-authoritative placeholders or redundant copies beside
    ///     the amplitude/phase data.
    /// </summary>
    public bool UsesFaceTextureCoordinates { get; init; }
}
