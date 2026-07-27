namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

internal static class XbxSkinVertexCodec
{
    public static void ReadSkinningData(BinaryReader r, ref XbxVertex vertex)
    {
        var packedWeights = r.ReadUInt32();
        // Bone indices are 4×u16, stored pre-multiplied by 3 (the engine's
        // matrix-row stride; nxtools fmt_thscene_import.py divides by 3). The
        // old 4×u8 read under-consumed the record by 4 bytes and shifted every
        // later field: the packed normal was read from bone indices 2/3
        // (cardinal-axis garbage), the vertex colour from the packed-normal
        // bits (per-vertex rainbow noise), and UV0.u from the colour dword —
        // confirmed against ped_boone_full.skin.wpc, whose sMesh stride (48)
        // only fits the u16 layout.
        var boneIndex0 = ReadBoneIndex(r);
        var boneIndex1 = ReadBoneIndex(r);
        var boneIndex2 = ReadBoneIndex(r);
        var boneIndex3 = ReadBoneIndex(r);

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
        vertex.HasSkinData = packedWeights != 0 || boneIndex0 != 0 || boneIndex1 != 0 || boneIndex2 != 0 ||
                             boneIndex3 != 0;
    }

    private static byte ReadBoneIndex(BinaryReader r)
    {
        return (byte)Math.Min(byte.MaxValue, r.ReadUInt16() / 3);
    }
}
