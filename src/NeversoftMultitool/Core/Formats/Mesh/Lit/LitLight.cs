using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Lit;

public sealed class LitLight
{
    public required string Name { get; init; }
    public required LitLightType Type { get; init; }
    public Vector3 Position { get; set; }
    public Vector3? Direction { get; set; }
    public Vector3 Color { get; set; } = Vector3.One;
    public float Ambient { get; set; }
    public float Atten1 { get; set; } = -1;
    public float Atten2 { get; set; } = -1;
    public float Radius { get; set; } = -1;
    public float Hotspot { get; set; } = -1;
    public float HeightAspect { get; set; } = 1;
}
