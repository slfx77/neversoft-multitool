namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public readonly record struct ModelBoneInfluences(
    int Joint0,
    int Joint1,
    int Joint2,
    int Joint3,
    float Weight0,
    float Weight1,
    float Weight2,
    float Weight3)
{
    public static ModelBoneInfluences Single(int joint)
    {
        return new ModelBoneInfluences(joint, 0, 0, 0, 1f, 0f, 0f, 0f);
    }
}
