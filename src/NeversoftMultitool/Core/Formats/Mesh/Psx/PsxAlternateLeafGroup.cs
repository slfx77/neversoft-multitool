namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal readonly record struct PsxAlternateLeafGroup(
    int DefaultObjectIndex,
    int AlternateObjectIndex,
    int ParentObjectIndex);
