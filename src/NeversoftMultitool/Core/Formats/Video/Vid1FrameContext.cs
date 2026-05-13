namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Shared mutable decoding state carried from frame to frame, and across
///     macroblocks within a frame. Created once per <see cref="Vid1Decoder" />
///     instance and reused for each <c>DecodeFrame</c> call.
/// </summary>
internal sealed class Vid1FrameContext
{
    public const int MbStateStride = 0x100; // bytes per MB in state buffer
    public const int MbBlockOffsetBase = 0x28; // first block's prediction data offset
    public const int MbBlockStride = 0x1E; // 30 bytes per block (DC + 7 top + 7 left)

    public Vid1FrameContext(int width, int height, byte[] intraMatrix, byte[] interMatrix)
    {
        ArgumentNullException.ThrowIfNull(intraMatrix);
        ArgumentNullException.ThrowIfNull(interMatrix);
        if (intraMatrix.Length != 64)
            throw new ArgumentException("intra quant matrix must have 64 entries", nameof(intraMatrix));
        if (interMatrix.Length != 64)
            throw new ArgumentException("inter quant matrix must have 64 entries", nameof(interMatrix));

        Width = width;
        Height = height;
        ChromaWidth = width / 2;
        ChromaHeight = height / 2;

        var lumaSize = width * height;
        var chromaSize = ChromaWidth * ChromaHeight;

        OutputY = new byte[lumaSize];
        OutputCb = new byte[chromaSize];
        OutputCr = new byte[chromaSize];

        ReferenceY = new byte[lumaSize];
        ReferenceCb = new byte[chromaSize];
        ReferenceCr = new byte[chromaSize];
        PreviousReferenceY = new byte[lumaSize];
        PreviousReferenceCb = new byte[chromaSize];
        PreviousReferenceCr = new byte[chromaSize];

        IntraMatrix = intraMatrix;
        InterMatrix = interMatrix;

        MbCols = (width + 15) / 16;
        MbRows = (height + 15) / 16;
        MbState = new byte[MbCols * MbRows * MbStateStride];
        ReferenceMbState = new byte[MbState.Length];
        PreviousReferenceMbState = new byte[MbState.Length];

        SpriteAnchor0X = 0;
        SpriteAnchor0Y = 0;
        SpriteAnchor1X = width;
        SpriteAnchor1Y = 0;
        SpriteAnchor2X = 0;
        SpriteAnchor2Y = height;
        SpriteAnchor3X = width;
        SpriteAnchor3Y = height;
        ResetSpriteWarp();
    }

    public int MbCols { get; }
    public int MbRows { get; }

    /// <summary>
    ///     Per-MB state buffer mirroring FUN_802A044C's neighbor storage.
    ///     Each MB at offset (mbY * mbCols + mbX) * 0x100 with layout:
    ///     [0]: mb_type (0 = inter/unknown, 3/4 = intra A878)
    ///     [1]: quantizer
    ///     [2..7]: scan table index for blocks 0-5 (0=zigzag, 1=top-pred, 2=left-pred)
    ///     [0x28 + block*0x1E ..]: 30 bytes per block as 15 ushorts (DC + 7 top row + 7 left col)
    /// </summary>
    public byte[] MbState { get; }

    public int Width { get; }

    public int Height { get; }

    public int ChromaWidth { get; }

    public int ChromaHeight { get; }

    public byte[] OutputY { get; }

    public byte[] OutputCb { get; }

    public byte[] OutputCr { get; }

    public byte[] ReferenceY { get; }

    public byte[] ReferenceCb { get; }

    public byte[] ReferenceCr { get; }

    public byte[] PreviousReferenceY { get; }

    public byte[] PreviousReferenceCb { get; }

    public byte[] PreviousReferenceCr { get; }

    public byte[] ReferenceMbState { get; }

    public byte[] PreviousReferenceMbState { get; }

    public uint PreviousReferenceStateWord { get; set; }

    public uint ReferenceStateWord { get; set; }

    public byte[] IntraMatrix { get; set; }

    public byte[] InterMatrix { get; set; }

    public int CurrentQuantizer { get; set; } = 16;

    public int ForwardFCode { get; set; } = 1;

    public bool GmcEnabled { get; set; }

    public int IntraDcThreshold { get; set; }

    public bool UseIntraDequant { get; set; }

    public int SubpixelRoundingBias { get; set; }

    public int SpritePointCount { get; set; }

    public int SpriteWarpAccuracy { get; set; }

    public int SpriteLumaX { get; set; }

    public int SpriteLumaY { get; set; }

    public int SpriteLumaScaleX { get; set; }

    public int SpriteLumaCrossX { get; set; }

    public int SpriteLumaCrossY { get; set; }

    public int SpriteLumaScaleY { get; set; }

    public int SpriteLumaTransformShift { get; set; }

    public int SpriteChromaX { get; set; }

    public int SpriteChromaY { get; set; }

    public int SpriteChromaScaleX { get; set; }

    public int SpriteChromaCrossX { get; set; }

    public int SpriteChromaCrossY { get; set; }

    public int SpriteChromaScaleY { get; set; }

    public int SpriteChromaTransformShift { get; set; }

    public int SpriteAnchor0X { get; }

    public int SpriteAnchor0Y { get; }

    public int SpriteAnchor1X { get; }

    public int SpriteAnchor1Y { get; }

    public int SpriteAnchor2X { get; }

    public int SpriteAnchor2Y { get; }

    public int SpriteAnchor3X { get; }

    public int SpriteAnchor3Y { get; }

    public void ClearMbState()
    {
        Array.Clear(MbState);
    }

    public void ResetSpriteWarp()
    {
        SpritePointCount = 0;
        SpriteWarpAccuracy = 0;
        SpriteLumaX = 0;
        SpriteLumaY = 0;
        SpriteLumaScaleX = 0;
        SpriteLumaCrossX = 0;
        SpriteLumaCrossY = 0;
        SpriteLumaScaleY = 0;
        SpriteLumaTransformShift = 0;
        SpriteChromaX = 0;
        SpriteChromaY = 0;
        SpriteChromaScaleX = 0;
        SpriteChromaCrossX = 0;
        SpriteChromaCrossY = 0;
        SpriteChromaScaleY = 0;
        SpriteChromaTransformShift = 0;
    }

    /// <summary>
    ///     Copy the just-decoded output planes into the reference buffers
    ///     so the next frame can use them as prediction source.
    /// </summary>
    public void PromoteOutputToReference(uint? stateWord)
    {
        Buffer.BlockCopy(ReferenceY, 0, PreviousReferenceY, 0, ReferenceY.Length);
        Buffer.BlockCopy(ReferenceCb, 0, PreviousReferenceCb, 0, ReferenceCb.Length);
        Buffer.BlockCopy(ReferenceCr, 0, PreviousReferenceCr, 0, ReferenceCr.Length);
        Buffer.BlockCopy(ReferenceMbState, 0, PreviousReferenceMbState, 0, ReferenceMbState.Length);
        PreviousReferenceStateWord = ReferenceStateWord;

        Buffer.BlockCopy(OutputY, 0, ReferenceY, 0, OutputY.Length);
        Buffer.BlockCopy(OutputCb, 0, ReferenceCb, 0, OutputCb.Length);
        Buffer.BlockCopy(OutputCr, 0, ReferenceCr, 0, OutputCr.Length);
        Buffer.BlockCopy(MbState, 0, ReferenceMbState, 0, MbState.Length);
        if (stateWord.HasValue)
            ReferenceStateWord = stateWord.Value;
    }
}
