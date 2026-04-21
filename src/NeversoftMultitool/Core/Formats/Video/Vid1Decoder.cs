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

    private sealed class FrameDecodeSnapshot(
        byte[] outputY,
        byte[] outputCb,
        byte[] outputCr,
        byte[] mbState,
        int currentQuantizer,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader)
    {
        public byte[] OutputY { get; } = outputY;
        public byte[] OutputCb { get; } = outputCb;
        public byte[] OutputCr { get; } = outputCr;
        public byte[] MbState { get; } = mbState;
        public int CurrentQuantizer { get; } = currentQuantizer;
        public Vid1BitReader VlcReader { get; } = vlcReader;
        public Vid1BitReader FlagReader { get; } = flagReader;
    }

    public Vid1Decoder(Vid1VideoFile container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;

        _defaultIntraMatrix = BuildDefaultIntraMatrix();
        _defaultInterMatrix = BuildDefaultInterMatrix();

        _context = new Vid1FrameContext(container.Width, container.Height, _defaultIntraMatrix, _defaultInterMatrix);
        Reset();
    }

    public int Width => _container.Width;

    public int Height => _container.Height;

    public int FrameCount => _container.FrameCount;

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
    }

    public Vid1DecodedFrame DecodeFrame(Vid1VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // Reset output planes to neutral YUV so that macroblocks the decoder
        // bails on fall back to mid-gray rather than black or green.
        Array.Fill(_context.OutputY, (byte)128);
        Array.Fill(_context.OutputCb, (byte)128);
        Array.Fill(_context.OutputCr, (byte)128);

        if (frame.IsPartial || frame.CodedPayload.Length == 0)
            return BuildFrame(frame.Index);

        // Current validated wiring keeps macroblock control bits and VLC tokens
        // on the post-header payload reader. VID1_READER_MODE is diagnostic;
        // forcing a separate bitstream flag reader is not score-correct yet.
        var readerMode = Environment.GetEnvironmentVariable("VID1_READER_MODE");
        var legacyHeaderPayload = frame.Bitstream.Length > 8
            ? frame.Bitstream.AsSpan(8).ToArray()
            : frame.CodedPayload;
        var vlcReader = readerMode switch
        {
            "bitstream" => new Vid1BitReader(frame.Bitstream),
            "header" or "legacy-header" or "header-split" => new Vid1BitReader(legacyHeaderPayload),
            _ => new Vid1BitReader(frame.CodedPayload),
        };
        var flagReader = readerMode switch
        {
            null or "" => vlcReader,
            "coded-flags" => new Vid1BitReader(frame.CodedPayload),
            "shared-coded" => vlcReader,
            "bitstream" => new Vid1BitReader(frame.Bitstream),
            "header" or "legacy-header" => new Vid1BitReader(legacyHeaderPayload),
            _ => new Vid1BitReader(frame.Bitstream),
        };
        if (frame.FlagBitOffset > 0)
            flagReader.SkipBits(frame.FlagBitOffset);

        _context.CurrentQuantizer = frame.Quantizer;
        _context.ForwardFCode = Math.Max(frame.ForwardCode ?? 1, 1);
        _context.GmcEnabled = frame.PreambleClass == 3;
        _context.SubpixelRoundingBias = frame.HasSpecialCallerGate ? 1 : 0;
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

        var mbCols = (Width + 15) / 16;
        var mbRows = (Height + 15) / 16;
        var totalMacroblocks = mbCols * mbRows;

        var mbOk = 0;
        var mbFail = 0;
        var implicitSkips = 0;
        var recoveryCount = 0;
        for (var mbIndex = 0; mbIndex < totalMacroblocks;)
        {
            var mbX = mbIndex % mbCols;
            var mbY = mbIndex / mbCols;
            var snapshot = CaptureSnapshot(vlcReader, flagReader);

            try
            {
                DecodeMacroblock(frame, vlcReader, flagReader, mbX, mbY);
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
                        snapshot,
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

                RestoreSnapshot(snapshot, vlcReader, flagReader);
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
        if (ShouldLogFrame(frame.Index))
            Console.Error.WriteLine(
                $"Vid1Decoder frame {frame.Index}: {mbOk} ok, {mbFail} fail, implicitSkips={implicitSkips}, recoveries={recoveryCount}, total={totalMacroblocks}, vlc@{vlcReader.BitPosition}/{frame.CodedPayload.Length * 8}, flag@{flagReader.BitPosition}/{frame.Bitstream.Length * 8}");

        var result = BuildFrame(frame.Index);
        _context.PromoteOutputToReference();
        return result;
    }

    private void DecodeMacroblock(Vid1VideoFrame frame, Vid1BitReader vlcReader, Vid1BitReader flagReader, int mbX, int mbY)
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
            // B-VOP/class-2 dispatch is still unported (FUN_80299DC0).
            // Keep the last known class-1 style probe as a fallback
            // instead of silently rerouting through the old gate bit.
            _ => Vid1ControlPrefix.Probe99A38(vlcReader, flagReader, _context.CurrentQuantizer, callerCr4: 0, gmcEnabled: false),
        };

        Vid1MacroblockDecoder.Decode(vlcReader, flagReader, control, _context, mbX, mbY);
    }

    private bool TryRecoverMacroblocks(
        Vid1VideoFrame frame,
        int macroblockIndex,
        int mbCols,
        int totalMacroblocks,
        FrameDecodeSnapshot snapshot,
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
            RestoreSnapshot(snapshot, vlcReader, flagReader);
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
            RestoreSnapshot(snapshot, vlcReader, flagReader);
            recoveredCount = 0;
            bestVlcDelta = 0;
            bestFlagDelta = 0;
            return false;
        }

        RestoreSnapshot(snapshot, vlcReader, flagReader);
        if (!TryApplyReaderDelta(vlcReader, flagReader, bestVlcDelta, bestFlagDelta))
        {
            RestoreSnapshot(snapshot, vlcReader, flagReader);
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
            RestoreSnapshot(snapshot, vlcReader, flagReader);
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
                DecodeMacroblock(frame, vlcReader, flagReader, index % mbCols, index / mbCols);
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

    private FrameDecodeSnapshot CaptureSnapshot(Vid1BitReader vlcReader, Vid1BitReader flagReader)
    {
        return new FrameDecodeSnapshot(
            _context.OutputY.ToArray(),
            _context.OutputCb.ToArray(),
            _context.OutputCr.ToArray(),
            _context.MbState.ToArray(),
            _context.CurrentQuantizer,
            vlcReader.Clone(),
            flagReader.Clone());
    }

    private void RestoreSnapshot(FrameDecodeSnapshot snapshot, Vid1BitReader vlcReader, Vid1BitReader flagReader)
    {
        Buffer.BlockCopy(snapshot.OutputY, 0, _context.OutputY, 0, snapshot.OutputY.Length);
        Buffer.BlockCopy(snapshot.OutputCb, 0, _context.OutputCb, 0, snapshot.OutputCb.Length);
        Buffer.BlockCopy(snapshot.OutputCr, 0, _context.OutputCr, 0, snapshot.OutputCr.Length);
        Buffer.BlockCopy(snapshot.MbState, 0, _context.MbState, 0, snapshot.MbState.Length);
        _context.CurrentQuantizer = snapshot.CurrentQuantizer;
        vlcReader.Restore(snapshot.VlcReader);
        flagReader.Restore(snapshot.FlagReader);
    }

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

    private Vid1DecodedFrame BuildFrame(int frameIndex)
    {
        var rgb = Vid1YuvToRgb.Convert(_context.OutputY, _context.OutputCb, _context.OutputCr, Width, Height);
        return new Vid1DecodedFrame(frameIndex, Width, Height, rgb);
    }

    private static bool ShouldLogFrame(int frameIndex)
    {
        var value = Environment.GetEnvironmentVariable("VID1_LOG_FRAMES");
        if (int.TryParse(value, out var frameLimit))
            return frameIndex < frameLimit;

        return frameIndex < 3;
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
