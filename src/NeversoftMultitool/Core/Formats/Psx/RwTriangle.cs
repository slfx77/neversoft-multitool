namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Triangle with three vertex indices and a material index.
///     RW binary stores as (v2, v1, materialId, v3); parsed to (v0, v1, v2, materialIndex).
/// </summary>
public readonly struct RwTriangle(int v0, int v1, int v2, int materialIndex)
{
    public readonly int V0 = v0, V1 = v1, V2 = v2;
    public readonly int MaterialIndex = materialIndex;
}
