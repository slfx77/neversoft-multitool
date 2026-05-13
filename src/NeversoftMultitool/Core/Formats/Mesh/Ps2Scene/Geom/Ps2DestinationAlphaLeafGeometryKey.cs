using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal readonly record struct Ps2DestinationAlphaLeafGeometryKey(
    int VertexCount,
    Vector3 Min,
    Vector3 Max);
