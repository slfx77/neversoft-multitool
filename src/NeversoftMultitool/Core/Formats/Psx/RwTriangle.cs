namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Triangle with three vertex indices and a material index.
///     RW binary stores as (v2, v1, materialId, v3); parsed to (v0, v1, v2, materialIndex).
/// </summary>
public readonly struct RwTriangle(int v0, int v1, int v2, int materialIndex)
{
    public int V0 { get; } = v0;
    public int V1 { get; } = v1;
    public int V2 { get; } = v2;
    public int MaterialIndex { get; } = materialIndex;
}
