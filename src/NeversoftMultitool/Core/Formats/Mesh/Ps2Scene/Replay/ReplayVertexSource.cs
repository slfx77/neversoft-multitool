using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal readonly record struct ReplayVertexSource(
    Vector3 Position,
    Vector3 Normal,
    bool HasNormal,
    float U,
    float V,
    bool HasUv,
    int OutputFullAddress,
    int DuplicateFullAddress,
    byte OutputAddress,
    byte DuplicateAddress,
    bool OutputNoKick,
    bool DuplicateNoKick);
