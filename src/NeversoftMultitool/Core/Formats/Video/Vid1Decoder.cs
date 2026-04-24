namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Native C# decoder for THAW GameCube VID1 (Factor 5 f5vid) video.
///     Ports Factor 5's <c>M4Decoder</c> pipeline — see
///     <c>tools/ghidra/thaw-gc/output/M4DECODER_INVENTORY.md</c> — with some
///     first-pass simplifications listed in the plan. Produces RGB24 frames
///     that can be piped to ffmpeg or rendered via WinUI <c>WriteableBitmap</c>.
/// </summary>
public sealed class Vid1Decoder
{
    private const int RecoveryLookaheadMacroblocks = 8;
    private const int MaxRecoveriesPerFrame = 256;

    private readonly Vid1VideoFile _container;
    private readonly Vid1FrameContext _context;
    private readonly byte[] _defaultIntraMatrix;
    private readonly byte[] _defaultInterMatrix;
    private readonly FrameDecodeSnapshot _recoverySnapshot;

    private sealed class FrameDecodeSnapshot(int maxMacroblocks)
    {
        private readonly byte[] _outputY = new byte[maxMacroblocks * 16 * 16];
        private readonly byte[] _outputCb = new byte[maxMacroblocks * 8 * 8];
        private readonly byte[] _outputCr = new byte[maxMacroblocks * 8 * 8];
        private readonly byte[] _mbState = new byte[maxMacroblocks * Vid1FrameContext.MbStateStride];

        private int _count;
        private int _flagBitPosition;
        private int _macroblockIndex;
        private int _vlcBitPosition;

        public int CurrentQuantizer { get; private set; }

        public void Capture(
            Vid1FrameContext context,
            int macroblockIndex,
            int totalMacroblocks,
            Vid1BitReader vlcReader,
            Vid1BitReader flagReader)
        {
            _macroblockIndex = macroblockIndex;
            _count = Math.Min(maxMacroblocks, Math.Max(0, totalMacroblocks - macroblockIndex));
            CurrentQuantizer = context.CurrentQuantizer;
            _vlcBitPosition = vlcReader.BitPosition;
            _flagBitPosition = flagReader.BitPosition;

            Buffer.BlockCopy(
                context.MbState,
                macroblockIndex * Vid1FrameContext.MbStateStride,
                _mbState,
                0,
                _count * Vid1FrameContext.MbStateStride);

            for (var i = 0; i < _count; i++)
            {
                var index = macroblockIndex + i;
                CopyPlaneBlockToSnapshot(
                    context.OutputY,
                    context.Width,
                    context.Height,
                    context.MbCols,
                    index,
                    16,
                    _outputY,
                    i * 16 * 16);
                CopyPlaneBlockToSnapshot(
                    context.OutputCb,
                    context.ChromaWidth,
                    context.ChromaHeight,
                    context.MbCols,
                    index,
                    8,
                    _outputCb,
                    i * 8 * 8);
                CopyPlaneBlockToSnapshot(
                    context.OutputCr,
                    context.ChromaWidth,
                    context.ChromaHeight,
                    context.MbCols,
                    index,
                    8,
                    _outputCr,
                    i * 8 * 8);
            }
        }

        public void Restore(Vid1FrameContext context, Vid1BitReader vlcReader, Vid1BitReader flagReader)
        {
            Buffer.BlockCopy(
                _mbState,
                0,
                context.MbState,
                _macroblockIndex * Vid1FrameContext.MbStateStride,
                _count * Vid1FrameContext.MbStateStride);

            for (var i = 0; i < _count; i++)
            {
                var index = _macroblockIndex + i;
                CopyPlaneBlockFromSnapshot(
                    _outputY,
                    i * 16 * 16,
                    context.OutputY,
                    context.Width,
                    context.Height,
                    context.MbCols,
                    index,
                    16);
                CopyPlaneBlockFromSnapshot(
                    _outputCb,
                    i * 8 * 8,
                    context.OutputCb,
                    context.ChromaWidth,
                    context.ChromaHeight,
                    context.MbCols,
                    index,
                    8);
                CopyPlaneBlockFromSnapshot(
                    _outputCr,
                    i * 8 * 8,
                    context.OutputCr,
                    context.ChromaWidth,
                    context.ChromaHeight,
                    context.MbCols,
                    index,
                    8);
            }

            context.CurrentQuantizer = CurrentQuantizer;
            vlcReader.SetBitPosition(_vlcBitPosition);
            flagReader.SetBitPosition(_flagBitPosition);
        }

        private static void CopyPlaneBlockToSnapshot(
            byte[] source,
            int planeWidth,
            int planeHeight,
            int mbCols,
            int macroblockIndex,
            int blockSize,
            byte[] destination,
            int destinationOffset)
        {
            var blockX = (macroblockIndex % mbCols) * blockSize;
            var blockY = (macroblockIndex / mbCols) * blockSize;
            var copyWidth = Math.Min(blockSize, Math.Max(0, planeWidth - blockX));
            var copyHeight = Math.Min(blockSize, Math.Max(0, planeHeight - blockY));

            for (var row = 0; row < copyHeight; row++)
            {
                Buffer.BlockCopy(
                    source,
                    ((blockY + row) * planeWidth) + blockX,
                    destination,
                    destinationOffset + row * blockSize,
                    copyWidth);
            }
        }

        private static void CopyPlaneBlockFromSnapshot(
            byte[] source,
            int sourceOffset,
            byte[] destination,
            int planeWidth,
            int planeHeight,
            int mbCols,
            int macroblockIndex,
            int blockSize)
        {
            var blockX = (macroblockIndex % mbCols) * blockSize;
            var blockY = (macroblockIndex / mbCols) * blockSize;
            var copyWidth = Math.Min(blockSize, Math.Max(0, planeWidth - blockX));
            var copyHeight = Math.Min(blockSize, Math.Max(0, planeHeight - blockY));

            for (var row = 0; row < copyHeight; row++)
            {
                Buffer.BlockCopy(
                    source,
                    sourceOffset + row * blockSize,
                    destination,
                    ((blockY + row) * planeWidth) + blockX,
                    copyWidth);
            }
        }
    }

    public Vid1Decoder(Vid1VideoFile container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;

        _defaultIntraMatrix = BuildDefaultIntraMatrix();
        _defaultInterMatrix = BuildDefaultInterMatrix();

        _context = new Vid1FrameContext(container.Width, container.Height, _defaultIntraMatrix, _defaultInterMatrix);
        _recoverySnapshot = new FrameDecodeSnapshot(RecoveryLookaheadMacroblocks);
        Reset();
    }

    public int Width => _container.Width;

    public int Height => _container.Height;

    public int FrameCount => _container.FrameCount;

    internal Vid1FrameDecodeStats LastFrameStats { get; private set; }

    /// <summary>
    ///     Forget any accumulated reference frame; the next DecodeFrame call
    ///     is treated as the first frame (useful for seeking to an I-frame).
    /// </summary>
    // MPEG-4 §7.8 S-VOP: before the sprite is established, the reference planes
    // are initialized to neutral gray (Y=128, Cb=128, Cr=128) — not zero. For
    // THAW GC's intro.vid (100% S-VOP, no explicit sprite I-VOP), this gives
    // GMC-warped skipped MBs a neutral starting point instead of green/black.
    public void Reset()
    {
        Array.Fill(_context.ReferenceY, (byte)128);
        Array.Fill(_context.ReferenceCb, (byte)128);
        Array.Fill(_context.ReferenceCr, (byte)128);
        Array.Fill(_context.PreviousReferenceY, (byte)128);
        Array.Fill(_context.PreviousReferenceCb, (byte)128);
        Array.Fill(_context.PreviousReferenceCr, (byte)128);
        Array.Clear(_context.ReferenceMbState);
        Array.Clear(_context.PreviousReferenceMbState);
        _context.PreviousReferenceStateWord = 0;
        _context.ReferenceStateWord = 0;
    }

    public Vid1DecodedFrame DecodeFrame(Vid1VideoFrame frame)
    {
        DecodeFrameToPlanes(frame);
        var rgb = new byte[Width * Height * 3];
        Vid1YuvToRgb.ConvertToRgb(_context.OutputY, _context.OutputCb, _context.OutputCr, Width, Height, rgb);
        return new Vid1DecodedFrame(frame.Index, Width, Height, rgb);
    }

    internal Vid1DecodedBgraFrame DecodeFrameToBgraFrame(Vid1VideoFrame frame)
    {
        var bgra = new byte[Width * Height * 4];
        DecodeFrameToBgra(frame, bgra);
        return new Vid1DecodedBgraFrame(frame.Index, Width, Height, bgra);
    }

    internal void DecodeFrameToBgra(Vid1VideoFrame frame, Span<byte> destination)
    {
        DecodeFrameToPlanes(frame);
        Vid1YuvToRgb.ConvertToBgra(_context.OutputY, _context.OutputCb, _context.OutputCr, Width, Height, destination);
    }

    internal void DecodeFrameToRgb(Vid1VideoFrame frame, Span<byte> destination)
    {
        DecodeFrameToPlanes(frame);
        Vid1YuvToRgb.ConvertToRgb(_context.OutputY, _context.OutputCb, _context.OutputCr, Width, Height, destination);
    }

    private void DecodeFrameToPlanes(Vid1VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // Reset output planes to neutral YUV so that macroblocks the decoder
        // bails on fall back to mid-gray rather than black or green.
        Array.Fill(_context.OutputY, (byte)128);
        Array.Fill(_context.OutputCb, (byte)128);
        Array.Fill(_context.OutputCr, (byte)128);

        var mbCols = (Width + 15) / 16;
        var mbRows = (Height + 15) / 16;
        var totalMacroblocks = mbCols * mbRows;

        if (frame.IsPartial || frame.CodedPayload.Length == 0)
        {
            LastFrameStats = new Vid1FrameDecodeStats(
                frame.Index,
                frame.PreambleClass,
                0,
                0,
                0,
                0,
                totalMacroblocks,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
            return;
        }

        // Current validated wiring keeps macroblock control bits and VLC tokens
        // on the post-header payload reader. VID1_READER_MODE is diagnostic;
        // forcing a separate bitstream flag reader is not score-correct yet.
        var readerMode = Environment.GetEnvironmentVariable("VID1_READER_MODE");
        var skipFlagBitOffset = true;
        byte[]? legacyHeaderPayload = null;
        byte[] GetLegacyHeaderPayload()
        {
            return legacyHeaderPayload ??= frame.Bitstream.Length > 8
                ? frame.Bitstream.AsSpan(8).ToArray()
                : frame.CodedPayload;
        }

        var vlcReader = readerMode switch
        {
            "bitstream" => new Vid1BitReader(frame.Bitstream),
            "header" or "legacy-header" or "header-split" => new Vid1BitReader(GetLegacyHeaderPayload()),
            _ => new Vid1BitReader(frame.CodedPayload),
        };
        var flagReader = readerMode switch
        {
            null or "" => vlcReader,
            "coded-flags" => new Vid1BitReader(frame.CodedPayload),
            "shared-coded" => vlcReader,
            "split-no-flagskip" => new Vid1BitReader(frame.Bitstream),
            "bitstream" => new Vid1BitReader(frame.Bitstream),
            "header" or "legacy-header" => new Vid1BitReader(GetLegacyHeaderPayload()),
            _ => new Vid1BitReader(frame.Bitstream),
        };
        if (string.Equals(readerMode, "split-no-flagskip", StringComparison.OrdinalIgnoreCase))
            skipFlagBitOffset = false;

        if (!ReferenceEquals(vlcReader, flagReader) && skipFlagBitOffset && frame.FlagBitOffset > 0)
            flagReader.SkipBits(frame.FlagBitOffset);

        _context.CurrentQuantizer = frame.Quantizer;
        _context.ForwardFCode = Math.Max(frame.ForwardCode ?? 1, 1);
        _context.GmcEnabled = frame.PreambleClass == 3;
        _context.SubpixelRoundingBias = GetRoundingBias(frame);
        _context.IntraDcThreshold = IntraDcThresholdTable[Math.Clamp(frame.IntraDcThresholdIndex, 0, 7)];
        _context.UseIntraDequant = frame.UsesCustomQuantMatrices;
        // Keep matrix capture conservative: when quant_type is set but custom
        // payload parsing is still incomplete, fall back to the DOL defaults.
        _context.IntraMatrix = frame.CustomIntraMatrix ?? _defaultIntraMatrix;
        _context.InterMatrix = frame.CustomInterMatrix ?? _defaultInterMatrix;
        Vid1SpriteWarp.ApplyFrame(_context, frame);
        // Reset per-MB prediction state at frame boundary so neighbors
        // from the previous frame don't leak into this frame's predictions.
        _context.ClearMbState();

        var mbOk = 0;
        var mbFail = 0;
        var implicitSkips = 0;
        var recoveryCount = 0;
        var unsupportedClass2Branches = 0;
        var class2FieldOrGmcBranches = 0;
        var intraMacroblocks = 0;
        var motionMacroblocks = 0;
        var fourMotionMacroblocks = 0;
        var fieldPredictionMacroblocks = 0;
        var spriteWarpMacroblocks = 0;
        if (frame.PreambleClass == 2)
        {
            var stats = Vid1BFrameDecoder.DecodeFrame(frame, vlcReader, flagReader, _context);
            mbOk = stats.DecodedMacroblocks;
            implicitSkips = stats.FallbackMacroblocks;
            unsupportedClass2Branches = stats.UnsupportedBranches;
            class2FieldOrGmcBranches = stats.FieldOrGmcBranches;
            goto done;
        }

        for (var mbIndex = 0; mbIndex < totalMacroblocks;)
        {
            var mbX = mbIndex % mbCols;
            var mbY = mbIndex / mbCols;
            CaptureSnapshot(mbIndex, totalMacroblocks, vlcReader, flagReader);

            try
            {
                DecodeMacroblock(
                    frame,
                    vlcReader,
                    flagReader,
                    mbX,
                    mbY,
                    collectStats: true,
                    ref intraMacroblocks,
                    ref motionMacroblocks,
                    ref fourMotionMacroblocks,
                    ref fieldPredictionMacroblocks,
                    ref spriteWarpMacroblocks);
                mbOk++;
                mbIndex++;
            }
            catch (EndOfStreamException)
            {
                if (ShouldTreatEndOfStreamAsImplicitSkips(frame))
                {
                    implicitSkips += CopyRemainingMacroblocksFromReference(mbIndex, totalMacroblocks, mbCols);
                    mbOk = totalMacroblocks;
                }
                goto done;
            }
            catch (InvalidDataException ex)
            {
                if (recoveryCount < MaxRecoveriesPerFrame &&
                    TryRecoverMacroblocks(
                        frame,
                        mbIndex,
                        mbCols,
                        totalMacroblocks,
                        vlcReader,
                        flagReader,
                        out var recoveredCount,
                        out var vlcDelta,
                        out var flagDelta))
                {
                    recoveryCount++;
                    mbOk += recoveredCount;
                    mbIndex += recoveredCount;
                    if (ShouldLogFrame(frame.Index) && recoveryCount <= 8)
                        Console.Error.WriteLine(
                $"  RECOVER MB({mbX},{mbY}) count={recoveredCount} vlcDelta={vlcDelta:+#;-#;0} flagDelta={flagDelta:+#;-#;0} vlc@{vlcReader.BitPosition} flag@{flagReader.BitPosition}");
                    continue;
                }

                RestoreSnapshot(vlcReader, flagReader);
                mbFail++;
                if (ShouldLogFrame(frame.Index) && mbFail <= 5)
                    Console.Error.WriteLine(
                $"  FAIL MB({mbX},{mbY}) #{mbOk + mbFail}: {ex.Message} vlc@{vlcReader.BitPosition} flag@{flagReader.BitPosition}");

                try
                {
                    vlcReader.SkipBits(1);
                }
                catch (EndOfStreamException)
                {
                    if (ShouldTreatEndOfStreamAsImplicitSkips(frame))
                    {
                        implicitSkips += CopyRemainingMacroblocksFromReference(mbIndex + 1, totalMacroblocks, mbCols);
                        mbOk = Math.Max(mbOk, totalMacroblocks - mbFail);
                    }
                    goto done;
                }

                mbIndex++;
            }
        }
    done:
        LastFrameStats = new Vid1FrameDecodeStats(
            frame.Index,
            frame.PreambleClass,
            mbOk,
            mbFail,
            implicitSkips,
            recoveryCount,
            totalMacroblocks,
            unsupportedClass2Branches,
            class2FieldOrGmcBranches,
            intraMacroblocks,
            motionMacroblocks,
            fourMotionMacroblocks,
            fieldPredictionMacroblocks,
            spriteWarpMacroblocks);

        if (ShouldLogFrame(frame.Index))
            Console.Error.WriteLine(
                $"Vid1Decoder frame {frame.Index}: {mbOk} ok, {mbFail} fail, implicitSkips={implicitSkips}, recoveries={recoveryCount}, total={totalMacroblocks}, vlc@{vlcReader.BitPosition}/{frame.CodedPayload.Length * 8}, flag@{flagReader.BitPosition}/{frame.Bitstream.Length * 8}");

        if (ShouldPromoteOutputToReference(frame))
            _context.PromoteOutputToReference(frame.CurrentFrameStateWord);
    }

    private void DecodeMacroblock(
        Vid1VideoFrame frame,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        int mbX,
        int mbY,
        bool collectStats,
        ref int intraMacroblocks,
        ref int motionMacroblocks,
        ref int fourMotionMacroblocks,
        ref int fieldPredictionMacroblocks,
        ref int spriteWarpMacroblocks)
    {
        Vid1ControlProbe control = frame.PreambleClass switch
        {
            0 => Vid1ControlPrefix.Probe998F8(vlcReader, flagReader, _context.CurrentQuantizer),
            1 => Vid1ControlPrefix.Probe99A38(vlcReader, flagReader, _context.CurrentQuantizer, callerCr4: 0, gmcEnabled: false),
            3 => Vid1ControlPrefix.Probe99A38(
                vlcReader,
                flagReader,
                _context.CurrentQuantizer,
                callerCr4: 1,
                gmcEnabled: (frame.SpritePointCount ?? 0) > 0),
            // Unknown preamble classes retain the last known class-1 style
            // probe instead of silently rerouting through the old gate bit.
            _ => Vid1ControlPrefix.Probe99A38(vlcReader, flagReader, _context.CurrentQuantizer, callerCr4: 0, gmcEnabled: false),
        };

        if (collectStats)
            AccumulateControlStats(
                control,
                ref intraMacroblocks,
                ref motionMacroblocks,
                ref fourMotionMacroblocks,
                ref fieldPredictionMacroblocks,
                ref spriteWarpMacroblocks);

        Vid1MacroblockDecoder.Decode(vlcReader, flagReader, control, _context, mbX, mbY);
    }

    private static void AccumulateControlStats(
        Vid1ControlProbe control,
        ref int intraMacroblocks,
        ref int motionMacroblocks,
        ref int fourMotionMacroblocks,
        ref int fieldPredictionMacroblocks,
        ref int spriteWarpMacroblocks)
    {
        switch (control.Stage)
        {
            case Vid1ControlStage.A878:
                intraMacroblocks++;
                break;
            case Vid1ControlStage.Motion:
                motionMacroblocks++;
                if (control.MacroblockType == 2)
                    fourMotionMacroblocks++;
                if ((control.BlockFlags & 0x08) != 0)
                    fieldPredictionMacroblocks++;
                break;
            case Vid1ControlStage.SpriteWarp:
                spriteWarpMacroblocks++;
                break;
        }
    }

    private bool TryRecoverMacroblocks(
        Vid1VideoFrame frame,
        int macroblockIndex,
        int mbCols,
        int totalMacroblocks,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        out int recoveredCount,
        out int bestVlcDelta,
        out int bestFlagDelta)
    {
        recoveredCount = 0;
        bestVlcDelta = 0;
        bestFlagDelta = 0;
        var bestCost = int.MaxValue;

        foreach (var candidate in EnumerateRecoveryCandidates())
        {
            RestoreSnapshot(vlcReader, flagReader);
            if (!TryApplyReaderDelta(vlcReader, flagReader, candidate.VlcDelta, candidate.FlagDelta))
                continue;

            var successCount = SimulateMacroblockRun(
                frame,
                macroblockIndex,
                mbCols,
                totalMacroblocks,
                vlcReader,
                flagReader,
                RecoveryLookaheadMacroblocks);

            var candidateCost = Math.Abs(candidate.VlcDelta) + Math.Abs(candidate.FlagDelta);
            if (successCount <= 0)
                continue;

            if (successCount > recoveredCount || (successCount == recoveredCount && candidateCost < bestCost))
            {
                recoveredCount = successCount;
                bestVlcDelta = candidate.VlcDelta;
                bestFlagDelta = candidate.FlagDelta;
                bestCost = candidateCost;
            }
        }

        if (recoveredCount < 2)
        {
            RestoreSnapshot(vlcReader, flagReader);
            recoveredCount = 0;
            bestVlcDelta = 0;
            bestFlagDelta = 0;
            return false;
        }

        RestoreSnapshot(vlcReader, flagReader);
        if (!TryApplyReaderDelta(vlcReader, flagReader, bestVlcDelta, bestFlagDelta))
        {
            RestoreSnapshot(vlcReader, flagReader);
            recoveredCount = 0;
            bestVlcDelta = 0;
            bestFlagDelta = 0;
            return false;
        }

        var appliedCount = SimulateMacroblockRun(
            frame,
            macroblockIndex,
            mbCols,
            totalMacroblocks,
            vlcReader,
            flagReader,
            recoveredCount);

        if (appliedCount != recoveredCount)
        {
            RestoreSnapshot(vlcReader, flagReader);
            recoveredCount = 0;
            bestVlcDelta = 0;
            bestFlagDelta = 0;
            return false;
        }

        return true;
    }

    private int SimulateMacroblockRun(
        Vid1VideoFrame frame,
        int macroblockIndex,
        int mbCols,
        int totalMacroblocks,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        int maxMacroblocks)
    {
        var successCount = 0;
        var limit = Math.Min(totalMacroblocks, macroblockIndex + maxMacroblocks);

        for (var index = macroblockIndex; index < limit; index++)
        {
            try
            {
                var intraMacroblocks = 0;
                var motionMacroblocks = 0;
                var fourMotionMacroblocks = 0;
                var fieldPredictionMacroblocks = 0;
                var spriteWarpMacroblocks = 0;
                DecodeMacroblock(
                    frame,
                    vlcReader,
                    flagReader,
                    index % mbCols,
                    index / mbCols,
                    collectStats: false,
                    ref intraMacroblocks,
                    ref motionMacroblocks,
                    ref fourMotionMacroblocks,
                    ref fieldPredictionMacroblocks,
                    ref spriteWarpMacroblocks);
                successCount++;
            }
            catch (InvalidDataException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                break;
            }
        }

        return successCount;
    }

    private void CaptureSnapshot(
        int macroblockIndex,
        int totalMacroblocks,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader)
        => _recoverySnapshot.Capture(_context, macroblockIndex, totalMacroblocks, vlcReader, flagReader);

    private void RestoreSnapshot(Vid1BitReader vlcReader, Vid1BitReader flagReader)
        => _recoverySnapshot.Restore(_context, vlcReader, flagReader);

    private static bool TryApplyReaderDelta(Vid1BitReader vlcReader, Vid1BitReader flagReader, int vlcDelta, int flagDelta)
    {
        if (ReferenceEquals(vlcReader, flagReader))
        {
            var combinedDelta = vlcDelta + flagDelta;
            if (vlcReader.BitPosition + combinedDelta < 0)
                return false;

            vlcReader.SetBitPosition(vlcReader.BitPosition + combinedDelta);
            return true;
        }

        if (vlcReader.BitPosition + vlcDelta < 0 || flagReader.BitPosition + flagDelta < 0)
            return false;

        vlcReader.SetBitPosition(vlcReader.BitPosition + vlcDelta);
        flagReader.SetBitPosition(flagReader.BitPosition + flagDelta);
        return true;
    }

    private static IEnumerable<(int VlcDelta, int FlagDelta)> EnumerateRecoveryCandidates()
    {
        var deltas = new (int VlcDelta, int FlagDelta)[]
        {
            (-1, 0),
            (-2, 0),
            (-3, 0),
            (1, 0),
            (2, 0),
            (3, 0),
            (4, 0),
            (0, -1),
            (0, 1),
            (0, -2),
            (0, 2),
            (0, -3),
            (0, 3),
            (0, -4),
            (0, 4),
            (-1, -1),
            (-1, 1),
            (1, -1),
            (1, 1),
            (-2, -1),
            (-2, 1),
            (2, -1),
            (2, 1),
            (-1, -2),
            (-1, 2),
            (1, -2),
            (1, 2),
        };

        return deltas;
    }

    private int CopyRemainingMacroblocksFromReference(int startMacroblock, int totalMacroblocks, int mbCols)
    {
        var copied = 0;
        for (var index = Math.Clamp(startMacroblock, 0, totalMacroblocks); index < totalMacroblocks; index++)
        {
            Vid1MacroblockDecoder.CopyMacroblockFromReference(_context, index % mbCols, index / mbCols);
            copied++;
        }

        return copied;
    }

    private static bool ShouldTreatEndOfStreamAsImplicitSkips(Vid1VideoFrame frame)
        => !frame.IsPartial && frame.PreambleClass != 0;

    private static bool ShouldPromoteOutputToReference(Vid1VideoFrame frame)
        // FUN_8029978C routes class-2 frames through the B-frame display path
        // without making that output the future prediction reference.
        => frame.PreambleClass != 2;

    private static int GetRoundingBias(Vid1VideoFrame frame)
    {
        var overrideValue = Environment.GetEnvironmentVariable("VID1_ROUNDING_BIAS");
        if (int.TryParse(overrideValue, out var bias))
            return bias & 1;

        return frame.HasSpecialCallerGate ? 1 : 0;
    }

    private static bool ShouldLogFrame(int frameIndex)
    {
        var value = Environment.GetEnvironmentVariable("VID1_LOG_FRAMES");
        if (int.TryParse(value, out var frameLimit))
            return frameIndex < frameLimit;

        return false;
    }

    // MPEG-4 intra_dc_vlc_thr table (ISO/IEC 14496-2 Table 6-21)
    // Maps IntraDcThresholdIndex (0-7) to quantiser_scale threshold.
    // When QP < threshold, DC is pre-decoded via separate VLC; otherwise
    // DC is embedded in the AC coefficient stream at index 0.
    private static readonly int[] IntraDcThresholdTable = [32, 13, 15, 17, 19, 21, 23, 1];

    // MPEG-4 default intra quant matrix (ISO/IEC 14496-2 Annex A)
    private static byte[] BuildDefaultIntraMatrix() =>
    [
        8, 17, 18, 19, 21, 23, 25, 27,
        17, 18, 19, 21, 23, 25, 27, 28,
        20, 21, 22, 23, 24, 26, 28, 30,
        21, 22, 23, 24, 26, 28, 30, 32,
        22, 23, 24, 26, 28, 30, 32, 35,
        23, 24, 26, 28, 30, 32, 35, 38,
        25, 26, 28, 30, 32, 35, 38, 41,
        27, 28, 30, 32, 35, 38, 41, 45,
    ];

    // MPEG-4 default inter quant matrix (all 16s — flat scaling)
    private static byte[] BuildDefaultInterMatrix()
    {
        var matrix = new byte[64];
        Array.Fill(matrix, (byte)16);
        return matrix;
    }
}
