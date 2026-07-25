using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed record PsxSplineControllerChain(
    IReadOnlyList<int> ObjectIndices,
    IReadOnlyList<Vector3> Centers);
