namespace NeversoftMultitool.Core.Formats.XbxScene;

internal static class XbxSkinVertexCodec
{
    public static void ReadSkinningData(BinaryReader r, ref XbxVertex vertex)
    {
        var packedWeights = r.ReadUInt32();
        var boneIndex0 = r.ReadByte();
        var boneIndex1 = r.ReadByte();
        var boneIndex2 = r.ReadByte();
        var boneIndex3 = r.ReadByte();

        var weight0 = (packedWeights & 0x7FF) / 2047f;
        var weight1 = ((packedWeights >> 11) & 0x7FF) / 2047f;
        var weight2 = ((packedWeights >> 22) & 0x3FF) / 1023f;
        var weight3 = MathF.Max(0f, 1f - weight0 - weight1 - weight2);

        var sum = weight0 + weight1 + weight2 + weight3;
        if (sum > 0)
        {
            var invSum = 1f / sum;
            weight0 *= invSum;
            weight1 *= invSum;
            weight2 *= invSum;
            weight3 *= invSum;
        }

        vertex.BoneIndex0 = boneIndex0;
        vertex.BoneIndex1 = boneIndex1;
        vertex.BoneIndex2 = boneIndex2;
        vertex.BoneIndex3 = boneIndex3;
        vertex.BoneWeight0 = weight0;
        vertex.BoneWeight1 = weight1;
        vertex.BoneWeight2 = weight2;
        vertex.BoneWeight3 = weight3;
        vertex.HasSkinData = packedWeights != 0 || boneIndex0 != 0 || boneIndex1 != 0 || boneIndex2 != 0 || boneIndex3 != 0;
    }
}
