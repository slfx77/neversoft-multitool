namespace NeversoftMultitool.Core.Formats.Vid1;

public sealed record Vid1VideoFrame(
    int Index,
    ushort Tag16,
    int PreambleClass,
    bool UsesCustomQuantMatrices,
    bool IsPartial,
    byte[] CodedPayload,
    byte[] Bitstream,
    int IntraDcThresholdIndex,
    int Quantizer,
    int? ForwardCode,
    int? BackwardCode,
    uint? CurrentFrameStateWord,
    uint? AlternateFrameStateWord,
    bool HasSpecialCallerGate,
    byte[]? CustomIntraMatrix,
    byte[]? CustomInterMatrix)
{
    internal int FlagBitOffset { get; init; }
    internal int VlcBitOffset { get; init; }

    internal int? SpritePointCount { get; init; }

    internal int? SpriteWarpAccuracy { get; init; }

    internal int[]? SpriteTrajectoryDeltas { get; init; }

    internal int GetFallbackVopType()
    {
        return PreambleClass switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            _ => 0
        };
    }
}
