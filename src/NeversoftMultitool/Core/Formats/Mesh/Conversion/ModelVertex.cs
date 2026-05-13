using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public readonly record struct ModelVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Color,
    Vector2 TexCoord);
