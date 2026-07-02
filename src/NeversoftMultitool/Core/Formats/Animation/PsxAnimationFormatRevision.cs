namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     PSX character animation stream/table revision inferred from the
///     hierarchy/animation chunk.
/// </summary>
public enum PsxAnimationFormatRevision
{
    Unknown,

    /// <summary>
    ///     Chunk tag 0x2A. Animation payload stores one 24-byte PSY-Q SMatrix
    ///     per bone per frame.
    /// </summary>
    DirectMatrixV1,

    /// <summary>
    ///     Chunk tag 0x2C with the normal monolithic animation table and
    ///     DecompressStream-compressed per-channel payloads.
    /// </summary>
    CompressedV2,

    /// <summary>
    ///     Chunk tag 0x2C with more than 255 animation entries. The on-disk
    ///     stream format is still CompressedV2, but the runtime must use
    ///     halfword-sized animation slot/cache fields; Spider-Man's player
    ///     banks are the known example.
    /// </summary>
    CompressedV2ExtendedSlots,

    /// <summary>
    ///     Prototype sparse 0x2C table: only the first entry is recoverable
    ///     from the currently understood table header.
    /// </summary>
    CompressedV2PrototypeSparse
}
