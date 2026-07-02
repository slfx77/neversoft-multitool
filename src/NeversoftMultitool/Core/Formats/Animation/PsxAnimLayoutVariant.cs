namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Per-game variant of the PSX character animation entry table.
/// </summary>
public enum PsxAnimLayoutVariant
{
    /// <summary>
    ///     Monolithic table used by THPS1, THPS2, Spider-Man, and SM2:EE
    ///     and Apocalypse:
    ///     <c>+0x00 u32 numStreams; +0x04 + i*8: (u32 dataOffset, u16 frameCount, u16 tweenFlag)</c>;
    ///     frame payload usually begins at <c>+0x04 + numStreams*8</c>,
    ///     while entry offsets are chunk-data-relative. The same on-disk layout
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
    PrototypeSparse,

    /// <summary>
    ///     v1 hier/anim chunk (tag <c>0x2A</c>): same entry table as
    ///     <see cref="Monolithic" /> but the per-frame payload is uncompressed —
    ///     <c>numBones × 24</c> bytes per frame interpreted as
    ///     <c>SMatrix { short m[3][3]; short t[3]; }</c> (PSY-Q 4096 = 1.0
    ///     fixed-point rotation matrix + 3-vector translation). Used by
    ///     Apocalypse, Spider-Man PSX prototype, and many THPS / THPS2 / THPS2X
    ///     character files.
    /// </summary>
    DirectMatrix
}
