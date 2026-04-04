namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     A PSX layout object entry (36 bytes). Contains world-space position and mesh index.
///     Positions are raw 20.12 fixed-point values, converted to float via raw/4096.
/// </summary>
public sealed class PsxLayoutObject
{
    public uint Flags { get; init; }
    public int RawX { get; init; }
    public int RawY { get; init; }
    public int RawZ { get; init; }
    public ushort MeshIndex { get; init; }

    /// <summary>Raw 20.12 fixed-point → float. No additional scaling.</summary>
    public float X => RawX / 4096f;

    public float Y => RawY / 4096f;
    public float Z => RawZ / 4096f;
}
