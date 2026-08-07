namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Where the fields of one 16-byte SFX cue record sit, and in which byte
///     order. Both shipped variants are the decomp-verified
///     <c>SFX_ParseSFXFile</c> grammar; the N64 port re-encoded it.
///     <para>
///         This is the "declared, not inferred" half of the shared
///         container-grammar layer. The N64 copy differs by more than byte
///         order — its alias field is WIDENED from u16 to u32 — so an endian
///         flag alone cannot describe it, and a whole-file byte swap certainly
///         cannot.
///     </para>
///     <para>
///         The widening was measured across all 55 carved Spider-Man banks
///         (1,929 records): bytes +8 and +9 are zero in every single record
///         while +11 is populated in every one and +10 in 25%, which is a u32
///         holding u16-range values. Bytes +12..15 are zero throughout — the
///         pad, four bytes rather than the PS1's six.
///     </para>
/// </summary>
internal sealed record SfxCueLayout(
    bool BigEndian,
    int PitchOffset,
    int VolumeOffset,
    int AliasOffset,
    int AliasWidth)
{
    /// <summary>
    ///     PS1 / Dreamcast / PC: little-endian, u16 alias at +8, six pad bytes.
    /// </summary>
    public static readonly SfxCueLayout LittleEndian = new(
        BigEndian: false, PitchOffset: 4, VolumeOffset: 6, AliasOffset: 8, AliasWidth: 2);

    /// <summary>
    ///     N64: big-endian, alias widened to a u32 at +8, four pad bytes.
    /// </summary>
    public static readonly SfxCueLayout N64 = new(
        BigEndian: true, PitchOffset: 4, VolumeOffset: 6, AliasOffset: 8, AliasWidth: 4);

    /// <summary>First byte after the last field — everything beyond is pad.</summary>
    public int PadOffset => AliasOffset + AliasWidth;
}
