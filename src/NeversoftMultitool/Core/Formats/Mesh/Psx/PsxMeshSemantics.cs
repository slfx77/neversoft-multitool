using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal static class PsxMeshSemantics
{
    internal const ushort StitchSourceType = 0x0001;
    internal const ushort StitchedReferenceType = 0x0002;

    internal static bool IsExactStitchSource(ushort type)
    {
        return type == StitchSourceType;
    }

    internal static bool IsExactStitchedReference(ushort type)
    {
        return type == StitchedReferenceType;
    }

    internal static Vector3 GetObjectOffset(PsxMeshObject obj, float translationDivisor)
    {
        return new Vector3(
            obj.X(translationDivisor),
            obj.Y(translationDivisor),
            obj.Z(translationDivisor));
    }

    internal static bool UsesCharacterObjectOrder(PsxMeshFile psxFile)
    {
        // Hierarchical character models store the first Objects.Count mesh pointers in
        // 1:1 correspondence with objects (confirmed by Blender io_ns_psxtools). When the
        // file has more meshes than objects, the extra meshes are LOD variants — object N
        // always maps to mesh pointer N regardless of the obj.MeshIndex field.
        return psxFile.HasHierarchy || psxFile.AttachmentVertices.Count > 0;
    }

    internal static int GetCharacterMeshIndex(PsxMeshFile psxFile, int objectIndex)
    {
        if (objectIndex < 0)
            return -1;

        if (UsesCharacterObjectOrder(psxFile))
            return objectIndex < psxFile.Meshes.Count ? objectIndex : -1;

        if (objectIndex >= psxFile.Objects.Count)
            return -1;

        var meshIndex = psxFile.Objects[objectIndex].MeshIndex;
        return meshIndex < psxFile.Meshes.Count ? meshIndex : -1;
    }

    internal static int GetCharacterObjectIndex(PsxMeshFile psxFile, int meshIndex)
    {
        if (meshIndex < 0)
            return -1;

        if (UsesCharacterObjectOrder(psxFile))
            return meshIndex < psxFile.Objects.Count ? meshIndex : -1;

        return meshIndex < psxFile.MeshToObjectIndex.Count
            ? psxFile.MeshToObjectIndex[meshIndex]
            : -1;
    }

    internal static Vector3 GetCharacterObjectOffset(PsxMeshFile psxFile, int meshIndex)
    {
        var objectIndex = GetCharacterObjectIndex(psxFile, meshIndex);
        if (objectIndex < 0 || objectIndex >= psxFile.Objects.Count)
            return Vector3.Zero;

        return GetObjectOffset(psxFile.Objects[objectIndex], psxFile.TranslationDivisor);
    }

    internal static Vector3 ToGltfPosition(Vector3 position)
    {
        return new Vector3(position.X, -position.Y, -position.Z);
    }
}
