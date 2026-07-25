using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal readonly record struct PsxLevelObjectPlacement(
    int TriggerNodeIndex,
    Matrix4x4 Transform);
