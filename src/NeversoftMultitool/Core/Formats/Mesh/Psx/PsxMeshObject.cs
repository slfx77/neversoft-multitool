namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     A PSX object entry (36 bytes). Contains world-space position and mesh index.
/// </summary>
public sealed class PsxMeshObject
{
    public uint Flags { get; init; }
    public int RawX { get; init; }
    public int RawY { get; init; }
    public int RawZ { get; init; }
    public ushort MeshIndex { get; init; }
    public int ParentIndex { get; set; } = -1;

    /// <summary>
    ///     Item flag bit 1 (0x02) = character ("Super"). The game uses this to select
    ///     M3dAsm_TransformAndOutcodeSuperVertices (which divides vertices by 16)
    ///     vs M3dAsm_TransformAndOutcodeItemVertices (no division).
    /// </summary>
    public bool IsCharacter => (Flags & 0x02) != 0;

    public float X(float translationDivisor)
    {
        return RawX / (4096f * translationDivisor);
    }

    public float Y(float translationDivisor)
    {
        return RawY / (4096f * translationDivisor);
    }

    public float Z(float translationDivisor)
    {
        return RawZ / (4096f * translationDivisor);
    }
}
