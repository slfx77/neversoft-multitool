namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Macroblock "stage" decoded by the control-prefix probe.
///     Maps to the Python <c>CallerControlProbe.stage</c> strings.
/// </summary>
internal enum Vid1ControlStage
{
    Special,
    SpriteWarp,
    Motion,
    A878,
    Other,
}

/// <summary>
///     Result of parsing a per-macroblock control prefix. Mirrors the Python
///     <c>CallerControlProbe</c> dataclass in <c>dump_vid1_coeffs.py</c>.
///     Nullable int fields use -1 as "not present."
/// </summary>
internal readonly record struct Vid1ControlProbe(
    Vid1ControlStage Stage,
    int GateBit,
    int RawCode,
    int MacroblockType,
    int ControlPrefix,
    int Selector,
    int ControlWord,
    int FeatureBit,
    int PreCbpFlag,
    int QdeltaIndex,
    int Qdelta,
    int Quantizer,
    int BlockFlags);

/// <summary>
///     Per-macroblock control-prefix parsers for Factor 5 M4Decoder.
///     Ports <c>probe_caller_control_99a38_from_reader</c> and
///     <c>probe_caller_control_998f8_from_reader</c> from the validated
///     Python probes in <c>tools/diagnostics/dump_vid1_coeffs.py</c>.
/// </summary>
internal static class Vid1ControlPrefix
{
    // QP_DELTA_TABLE from dump_vid1_coeffs.py line 349
    private static readonly int[] QpDeltaTable = [-1, -2, 1, 2];

    private const int NoValue = -1;

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
                Stage: callerCr4 == 0 ? Vid1ControlStage.Special : Vid1ControlStage.SpriteWarp,
                GateBit: gateBit,
                RawCode: NoValue,
                MacroblockType: callerCr4 == 0 ? 0x10 : 0x11,
                ControlPrefix: NoValue,
                Selector: NoValue,
                ControlWord: NoValue,
                FeatureBit: NoValue,
                PreCbpFlag: NoValue,
                QdeltaIndex: NoValue,
                Qdelta: NoValue,
                Quantizer: currentQuantizer,
                BlockFlags: 0);
        }

        var rawCode = Vid1VlcDecoder.DecodeRawCodeB(vlcReader);
        var macroblockType = rawCode & 0x7;
        var controlPrefix = rawCode >> 4;

        if (macroblockType is 3 or 4)
        {
            var featureBit = flagReader.ReadBits(1);
            var selector = Vid1VlcDecoder.DecodeSelector(vlcReader, invert: true);
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
                Stage: Vid1ControlStage.A878,
                GateBit: gateBit,
                RawCode: rawCode,
                MacroblockType: macroblockType,
                ControlPrefix: controlPrefix,
                Selector: selector,
                ControlWord: controlPrefix | (selector << 2),
                FeatureBit: featureBit,
                PreCbpFlag: NoValue,
                QdeltaIndex: qdeltaIndex,
                Qdelta: qdelta,
                Quantizer: quantizer,
                BlockFlags: 0);
        }

        var preCbpFlag = NoValue;
        if (callerCr4 != 0 && (controlPrefix & 0x10) == 0)
        {
            preCbpFlag = flagReader.ReadBits(1);
        }
        var motionSelector = Vid1VlcDecoder.DecodeSelector(vlcReader, invert: false);
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
            Stage: stage,
            GateBit: gateBit,
            RawCode: rawCode,
            MacroblockType: macroblockType,
            ControlPrefix: controlPrefix,
            Selector: motionSelector,
            ControlWord: controlWord,
            FeatureBit: NoValue,
            PreCbpFlag: preCbpFlag,
            QdeltaIndex: motionQdeltaIndex,
            Qdelta: motionQdelta,
            Quantizer: motionQuantizer,
            BlockFlags: blockFlags);
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
        var selector = Vid1VlcDecoder.DecodeSelector(vlcReader, invert: true);

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
            Stage: Vid1ControlStage.A878,
            GateBit: gateBit,
            RawCode: rawCode,
            MacroblockType: macroblockType,
            ControlPrefix: controlPrefix,
            Selector: selector,
            ControlWord: controlPrefix | (selector << 2),
            FeatureBit: gateBit,
            PreCbpFlag: NoValue,
            QdeltaIndex: qdeltaIndex,
            Qdelta: qdelta,
            Quantizer: quantizer,
            BlockFlags: 0);
    }
}
