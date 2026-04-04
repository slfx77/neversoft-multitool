using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

public sealed class XbxMaterial
{
    public uint Checksum { get; init; }
    public uint NameChecksum { get; init; }
    public int NumPasses { get; init; }
    public int AlphaCutoff { get; init; }
    public bool Sorted { get; init; }
    public float DrawOrder { get; init; }
    public bool SingleSided { get; init; }
    public bool NoBfc { get; init; }
    public int ZBias { get; init; }
    public bool Grassify { get; init; }
    public float GrassHeight { get; init; }
    public int GrassLayers { get; init; }
    public float SpecularPower { get; init; }
    public Vector3 SpecularColor { get; init; }
    public required XbxPass[] Passes { get; init; }
}
