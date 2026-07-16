namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     Deep copy of the decoder's cross-frame prediction state — exactly the
///     fields <see cref="Vid1Decoder.Reset" /> touches: both reference plane
///     sets, both macroblock-state mirrors, and the two state words. Everything
///     else in <see cref="Vid1FrameContext" /> is (re)assigned from each
///     frame's own header at the top of DecodeFrameToPlanes, so restoring this
///     snapshot and decoding forward reproduces the original stream state.
///     ~1 MB at 512×384; used as a seek anchor.
/// </summary>
internal sealed class Vid1ReferenceSnapshot
{
    public required byte[] ReferenceY { get; init; }
    public required byte[] ReferenceCb { get; init; }
    public required byte[] ReferenceCr { get; init; }
    public required byte[] PreviousReferenceY { get; init; }
    public required byte[] PreviousReferenceCb { get; init; }
    public required byte[] PreviousReferenceCr { get; init; }
    public required byte[] ReferenceMbState { get; init; }
    public required byte[] PreviousReferenceMbState { get; init; }
    public required uint ReferenceStateWord { get; init; }
    public required uint PreviousReferenceStateWord { get; init; }
}
