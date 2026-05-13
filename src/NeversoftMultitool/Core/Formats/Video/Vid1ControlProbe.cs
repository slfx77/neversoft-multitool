namespace NeversoftMultitool.Core.Formats.Video;

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
