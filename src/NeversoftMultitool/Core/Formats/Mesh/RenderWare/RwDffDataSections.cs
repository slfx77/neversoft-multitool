using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

using static RwChunkReader;

internal static class RwDffDataSections
{
    public static RwMaterial[] ParseMaterialList(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return [];

        var numMaterials = BitConverter.ToInt32(data, offset);
        if (numMaterials <= 0 || numMaterials > 1000)
            return [];

        offset += (int)structSize;

        var materials = new List<RwMaterial>(numMaterials);
        for (var i = 0; i < numMaterials && offset < endOffset; i++)
        {
            if (offset + 12 <= data.Length && BitConverter.ToUInt32(data, offset) != RW_MATERIAL)
            {
                var found = false;
                for (var scan = offset + 1; scan + 12 <= endOffset && scan + 12 <= data.Length; scan++)
                {
                    if (BitConverter.ToUInt32(data, scan) == RW_MATERIAL)
                    {
                        offset = scan;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    break;
            }

            if (!TryReadAnyChunk(data, ref offset, endOffset, out var materialType, out var materialSize))
                break;

            var materialEnd = offset + (int)materialSize;
            if (materialEnd > data.Length)
                break;

            if (materialType == RW_MATERIAL)
                materials.Add(ParseMaterial(data, ref offset, materialEnd));

            offset = materialEnd;
        }

        return materials.ToArray();
    }

    public static RwSkinData? ParseSkinPlg(byte[] data, int offset, int size)
    {
        if (size < 8)
            return null;

        var numBones = BitConverter.ToInt32(data, offset);
        var numVerts = BitConverter.ToInt32(data, offset + 4);
        var expectedSize = 8 + numVerts * 4 + numVerts * 16 + numBones * 76;
        if (numBones <= 0 || numBones > 256 || numVerts <= 0 || size < expectedSize)
            return null;

        var pos = offset + 8;
        var boneIndices = new byte[numVerts * 4];
        Buffer.BlockCopy(data, pos, boneIndices, 0, numVerts * 4);
        pos += numVerts * 4;

        var boneWeights = new float[numVerts * 4];
        Buffer.BlockCopy(data, pos, boneWeights, 0, numVerts * 16);
        pos += numVerts * 16;

        var bones = new RwSkinBone[numBones];
        for (var i = 0; i < numBones; i++)
        {
            var id = BitConverter.ToInt32(data, pos);
            var index = BitConverter.ToInt32(data, pos + 4);
            var boneFlags = BitConverter.ToInt32(data, pos + 8);
            var transform = new Matrix4x4(
                BitConverter.ToSingle(data, pos + 12), BitConverter.ToSingle(data, pos + 16),
                BitConverter.ToSingle(data, pos + 20), 0f,
                BitConverter.ToSingle(data, pos + 28), BitConverter.ToSingle(data, pos + 32),
                BitConverter.ToSingle(data, pos + 36), 0f,
                BitConverter.ToSingle(data, pos + 44), BitConverter.ToSingle(data, pos + 48),
                BitConverter.ToSingle(data, pos + 52), 0f,
                BitConverter.ToSingle(data, pos + 60), BitConverter.ToSingle(data, pos + 64),
                BitConverter.ToSingle(data, pos + 68), 1f);

            bones[i] = new RwSkinBone(id, index, boneFlags, transform);
            pos += 76;
        }

        return new RwSkinData
        {
            NumBones = numBones,
            NumVertices = numVerts,
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
            Bones = bones
        };
    }

    private static RwMaterial ParseMaterial(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return DefaultMaterial();

        var pos = offset + 4;
        var red = data[pos];
        var green = data[pos + 1];
        var blue = data[pos + 2];
        var alpha = data[pos + 3];
        pos += 8;
        var isTextured = BitConverter.ToInt32(data, pos) != 0;
        pos += 4;
        var ambient = BitConverter.ToSingle(data, pos);
        pos += 4;
        var specular = BitConverter.ToSingle(data, pos);
        pos += 4;
        var diffuse = BitConverter.ToSingle(data, pos);

        offset += (int)structSize;

        string? textureName = null;
        string? maskName = null;
        if (isTextured)
        {
            while (offset < endOffset && offset + 12 <= data.Length)
            {
                if (!TryReadAnyChunk(data, ref offset, endOffset, out var childType, out var childSize))
                    break;

                var childEnd = offset + (int)childSize;
                if (childType == RW_TEXTURE)
                {
                    (textureName, maskName) = ParseTexture(data, ref offset, childEnd);
                    offset = childEnd;
                    break;
                }

                offset = childEnd;
            }
        }

        return new RwMaterial
        {
            R = red,
            G = green,
            B = blue,
            A = alpha,
            TextureName = textureName,
            MaskName = maskName,
            Ambient = ambient,
            Specular = specular,
            Diffuse = diffuse
        };
    }

    private static (string? name, string? mask) ParseTexture(byte[] data, ref int offset, int endOffset)
    {
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var structSize))
            return (null, null);

        offset += (int)structSize;

        string? name = null;
        if (TryReadChunk(data, ref offset, endOffset, RW_STRING, out var nameSize))
        {
            name = ReadNullTerminatedString(data, offset, (int)nameSize);
            offset += (int)nameSize;
        }

        string? mask = null;
        if (TryReadChunk(data, ref offset, endOffset, RW_STRING, out var maskSize))
        {
            mask = ReadNullTerminatedString(data, offset, (int)maskSize);
            if (string.IsNullOrEmpty(mask))
                mask = null;
            offset += (int)maskSize;
        }

        return (name, mask);
    }

    private static RwMaterial DefaultMaterial()
    {
        return new RwMaterial
        {
            R = 255,
            G = 255,
            B = 255,
            A = 255,
            TextureName = null,
            MaskName = null
        };
    }
}
