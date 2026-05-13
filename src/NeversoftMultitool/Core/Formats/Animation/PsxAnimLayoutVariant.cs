namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Per-game variant of the PSX character animation entry table.
/// </summary>
public enum PsxAnimLayoutVariant
{
    /// <summary>
    ///     Monolithic table used by THPS1 final, THPS2 final / Spider-Man / SM2:EE
    ///     and Apocalypse:
    ///     <c>+0x00 u32 numStreams; +0x04 + i*8: (u32 poolOffset, u32 frameCount)</c>;
    ///     pool starts at <c>+0x04 + numStreams*8</c>. The same on-disk layout
    ///     appears in every shipped game we have surveyed; the file's PSX
    ///     <c>Version</c> field (0x03 vs 0x04) is reported separately by
    ///     <see cref="Mesh.Psx.PsxMeshFile" />.
    /// </summary>
    Monolithic,

    /// <summary>
    ///     Sparse table used by THPS2 PSX prototype (and possibly THPS1
    ///     prototype): only one entry header fits before the compressed pool.
    ///     <c>
    ///         +0x00 u32 numStreams; +0x04 u32 poolByteSize; +0x08 (u32 frameCount, u32 poolOffset);
    ///         +0x10 pool data
    ///     </c>
    ///     . Subsequent entries are not yet recoverable from
    ///     the table alone — decomp research outstanding.
    /// </summary>
    PrototypeSparse
}
