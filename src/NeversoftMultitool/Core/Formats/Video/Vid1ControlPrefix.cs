namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Per-macroblock control-prefix parsers for Factor 5 M4Decoder.
///     Ports <c>probe_caller_control_99a38_from_reader</c> and
///     <c>probe_caller_control_998f8_from_reader</c> from the validated
///     Python probes in <c>tools/diagnostics/dump_vid1_coeffs.py</c>.
/// </summary>
internal static class Vid1ControlPrefix
{
    private const int NoValue = -1;

    // QP_DELTA_TABLE from dump_vid1_coeffs.py line 349
    private static readonly int[] QpDeltaTable = [-1, -2, 1, 2];

    private static int ClampQuantizer(int value)
    {
        if (value < 1) return 1;
        if (value > 0x1F) return 0x1F;
        return value;
    }

    /// <summary>
    ///     Port of <c>probe_caller_control_99a38_from_reader</c>
    ///     (dump_vid1_coeffs.py:955). The DOL selects this parser for
    ///     preamble classes 1 and 3.
    /// </summary>
    public static Vid1ControlProbe Probe99A38(
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        int currentQuantizer,
        int callerCr4,
        bool gmcEnabled)
    {
        var gateBit = flagReader.ReadBits(1);

        if (gateBit != 0)
        {
            return new Vid1ControlProbe(
                callerCr4 == 0 ? Vid1ControlStage.Special : Vid1ControlStage.SpriteWarp,
                gateBit,
                NoValue,
                callerCr4 == 0 ? 0x10 : 0x11,
                NoValue,
                NoValue,
                NoValue,
                NoValue,
                NoValue,
                NoValue,
                NoValue,
                currentQuantizer,
                0);
        }

        var rawCode = Vid1VlcDecoder.DecodeRawCodeB(vlcReader);
        var macroblockType = rawCode & 0x7;
        var controlPrefix = rawCode >> 4;

        if (macroblockType is 3 or 4)
        {
            var featureBit = flagReader.ReadBits(1);
            var selector = Vid1VlcDecoder.DecodeSelector(vlcReader, true);
            var qdeltaIndex = NoValue;
            var qdelta = NoValue;
            var quantizer = currentQuantizer;
            if (macroblockType == 4)
            {
                qdeltaIndex = flagReader.ReadBits(2);
                qdelta = QpDeltaTable[qdeltaIndex];
                quantizer = ClampQuantizer(currentQuantizer + qdelta);
            }

            return new Vid1ControlProbe(
                Vid1ControlStage.A878,
                gateBit,
                rawCode,
                macroblockType,
                controlPrefix,
                selector,
                controlPrefix | (selector << 2),
                featureBit,
                NoValue,
                qdeltaIndex,
                qdelta,
                quantizer,
                0);
        }

        var preCbpFlag = NoValue;
        if (callerCr4 != 0 && (controlPrefix & 0x10) == 0)
        {
            preCbpFlag = flagReader.ReadBits(1);
        }

        var motionSelector = Vid1VlcDecoder.DecodeSelector(vlcReader, false);
        var motionQdeltaIndex = NoValue;
        var motionQdelta = NoValue;
        var motionQuantizer = currentQuantizer;
        if (macroblockType == 1)
        {
            motionQdeltaIndex = flagReader.ReadBits(2);
            motionQdelta = QpDeltaTable[motionQdeltaIndex];
            motionQuantizer = ClampQuantizer(currentQuantizer + motionQdelta);
        }

        var controlWord = controlPrefix | (motionSelector << 2);
        var blockFlags = 0;
        if (gmcEnabled && controlWord != 0 && flagReader.ReadBits(1) != 0)
            blockFlags |= 0x04;

        var stage = callerCr4 != 0 && preCbpFlag != 0
            ? Vid1ControlStage.SpriteWarp
            : Vid1ControlStage.Motion;

        if (stage == Vid1ControlStage.Motion && gmcEnabled && flagReader.ReadBits(1) != 0)
        {
            blockFlags |= 0x08;
            blockFlags |= flagReader.ReadBits(2) << 4;
        }

        return new Vid1ControlProbe(
            stage,
            gateBit,
            rawCode,
            macroblockType,
            controlPrefix,
            motionSelector,
            controlWord,
            NoValue,
            preCbpFlag,
            motionQdeltaIndex,
            motionQdelta,
            motionQuantizer,
            blockFlags);
    }

    /// <summary>
    ///     Port of <c>probe_caller_control_998f8_from_reader</c>
    ///     (dump_vid1_coeffs.py:1046). The DOL selects this parser for
    ///     preamble class 0.
    /// </summary>
    public static Vid1ControlProbe Probe998F8(Vid1BitReader vlcReader, Vid1BitReader flagReader, int currentQuantizer)
    {
        var rawCode = Vid1VlcDecoder.DecodeRawCodeA(vlcReader);
        var macroblockType = rawCode & 0x7;
        var controlPrefix = rawCode >> 4;
        var gateBit = flagReader.ReadBits(1);
        var selector = Vid1VlcDecoder.DecodeSelector(vlcReader, true);

        var qdeltaIndex = NoValue;
        var qdelta = NoValue;
        var quantizer = currentQuantizer;
        if (macroblockType == 4)
        {
            qdeltaIndex = flagReader.ReadBits(2);
            qdelta = QpDeltaTable[qdeltaIndex];
            quantizer = ClampQuantizer(currentQuantizer + qdelta);
        }

        return new Vid1ControlProbe(
            // FUN_802998F8 always dispatches through FUN_8029A878 for
            // preamble-class-0 macroblocks. The gate bit is a feature/scantable
            // control for that path, not a separate stage selector.
            Vid1ControlStage.A878,
            gateBit,
            rawCode,
            macroblockType,
            controlPrefix,
            selector,
            controlPrefix | (selector << 2),
            gateBit,
            NoValue,
            qdeltaIndex,
            qdelta,
            quantizer,
            0);
    }
}
