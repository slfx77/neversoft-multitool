using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

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
