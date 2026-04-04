namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

/// <summary>
///     Skinning data from RW Skin PLG (0x0116).
///     Per-vertex bone indices (4×u8) and weights (4×f32), plus per-bone inverse bind matrices.
///     THPS3 PS2 layout: header(8B) + boneIndices(N×4) + boneWeights(N×16) + bones(B×76).
/// </summary>
public sealed class RwSkinData
{
    public required int NumBones { get; init; }
    public required int NumVertices { get; init; }
    public required byte[] BoneIndices { get; init; }
    public required float[] BoneWeights { get; init; }
    public required RwSkinBone[] Bones { get; init; }
}
