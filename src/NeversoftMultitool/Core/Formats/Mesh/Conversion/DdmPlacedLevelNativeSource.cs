namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record DdmPlacedLevelNativeSource(
    string LevelDdmPath,
    string LevelPsxPath,
    string? ObjectsDdmPath,
    string? ObjectsPsxPath,
    string LevelName,
    string? DdxPath)
    : ModelNativeSource(ModelSourceKind.DdmPlacedLevel);
