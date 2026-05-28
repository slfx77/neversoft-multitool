namespace NeversoftMultitool.Core.Formats.Vid1;

internal readonly record struct Vid1FrameDecodeStats(
    int FrameIndex,
    int PreambleClass,
    int DecodedMacroblocks,
    int FailedMacroblocks,
    int ImplicitSkippedMacroblocks,
    int RecoveryCount,
    int TotalMacroblocks,
    int UnsupportedClass2Branches,
    int Class2FieldOrGmcBranches,
    int IntraMacroblocks,
    int MotionMacroblocks,
    int FourMotionMacroblocks,
    int FieldPredictionMacroblocks,
    int SpriteWarpMacroblocks);
