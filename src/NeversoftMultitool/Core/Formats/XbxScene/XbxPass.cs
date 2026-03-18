using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

public sealed class XbxPass
{
    public uint TextureChecksum { get; init; }
    public uint Flags { get; init; }
    public bool HasColor { get; init; }
    public Vector3 Color { get; init; }
    public uint BlendMode { get; init; }
    public uint FixedAlpha { get; init; }
    public uint UAddressing { get; init; }
    public uint VAddressing { get; init; }
    public Vector2 EnvmapTiling { get; init; }
    public uint FilteringMode { get; init; }
}
