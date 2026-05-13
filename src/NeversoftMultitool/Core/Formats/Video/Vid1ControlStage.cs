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
    Other
}
