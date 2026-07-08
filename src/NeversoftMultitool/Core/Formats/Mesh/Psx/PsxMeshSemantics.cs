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

    internal static Vector3 GetObjectOffset(PsxMeshFile psxFile, PsxMeshObject obj)
    {
        return GetObjectOffset(obj, psxFile.TranslationDivisor);
    }

    internal static bool UsesCharacterObjectOrder(PsxMeshFile psxFile)
    {
        // Characters render positionally: RenderSuperItem draws ppModels[part]
        // with part == slot, 1:1 (decomp char_mesh_selection.md), and
        // M3dInit_ParsePSX (decomp-VERIFIED, psx_model_load_format.md §1)
        // fills ppModels contiguously with no index remap. That applies to
        // HIER-table files AND to flat stitched supers (THPS1-proto hawk,
        // Apocalypse bruce): on those, obj.MeshIndex is limb-permuted
        // relative to the part slots (hawk: hand<->bicep, 12<->14) and
        // binding through it hangs the bicep mesh on the wrist pivot — the
        // source of the proto arm/leg garble. obj.MeshIndex is an
        // object/item-table field (load-format doc §6.2) meaningful only for
        // item lookup on non-character files, which keep it via the fallback
        // (as do count-mismatched files, where positional cannot apply).
        // HIER counts only for supers: level files carry HIER chunks for
        // their placed animated objects and are not part-ordered characters.
        return (psxFile.HasHierarchy && psxFile.IsSuperModel) ||
               (psxFile.HasStitchedReferences &&
                psxFile.Objects.Count == psxFile.Meshes.Count);
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

        return GetObjectOffset(psxFile, psxFile.Objects[objectIndex]);
    }

    internal static HashSet<int> FindAlternateLeafObjectIndices(PsxMeshFile psxFile)
    {
        var alternates = new HashSet<int>();
        if (!psxFile.HasHierarchy || psxFile.Objects.Count == 0)
            return alternates;

        var hasChild = new bool[psxFile.Objects.Count];
        for (var i = 0; i < psxFile.Objects.Count; i++)
        {
            var parent = psxFile.Objects[i].ParentIndex;
            if (parent >= 0 && parent < hasChild.Length && parent != i)
                hasChild[parent] = true;
        }

        var firstLeafByPlacement = new Dictionary<AlternateLeafKey, int>();
        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            if (hasChild[objectIndex])
                continue;

            var obj = psxFile.Objects[objectIndex];
            if (obj.ParentIndex < 0)
                continue;

            var meshIndex = GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
                continue;

            var mesh = psxFile.Meshes[meshIndex];
            if (mesh.Faces.Count == 0)
                continue;

            var key = new AlternateLeafKey(obj.ParentIndex, obj.RawX, obj.RawY, obj.RawZ);
            if (firstLeafByPlacement.TryAdd(key, objectIndex))
                continue;

            alternates.Add(objectIndex);
        }

        return alternates;
    }

    internal static Vector3 ToGltfPosition(Vector3 position)
    {
        return new Vector3(position.X, -position.Y, -position.Z);
    }

    private readonly record struct AlternateLeafKey(int ParentIndex, int RawX, int RawY, int RawZ);
}
