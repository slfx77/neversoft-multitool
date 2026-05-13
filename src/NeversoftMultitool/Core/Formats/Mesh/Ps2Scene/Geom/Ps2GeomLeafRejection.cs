using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public readonly record struct Ps2GeomLeafRejection(
    string MdlName,
    string Stage,
    string Reason,
    int LeafIndex,
    int VertexCount,
    ulong Tex0,
    Vector3 Min,
    Vector3 Max);
