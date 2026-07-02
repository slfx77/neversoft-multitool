using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    internal static ModelSkeleton BuildPs2Skeleton(Ps2Skeleton skeleton)
    {
        // PS2 .ske bones store local rotation+translation in glTF-friendly coordinates
        // (no Z-up→Y-up swap needed). The IR's LocalTransform mirrors what SharpGLTF
        // would flatten an AffineTransform to: row-vector M = R * T.
        var result = new ModelSkeleton { Name = "skeleton" };
        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var local = Matrix4x4.CreateFromQuaternion(bone.LocalRotation)
                        * Matrix4x4.CreateTranslation(bone.LocalTranslation);
            result.Bones.Add(new ModelBone
            {
                Name = QbKey.QbKey.TryResolve(bone.NameChecksum) ?? $"bone_{bone.NameChecksum:X8}",
                ParentIndex = bone.ParentIndex,
                LocalTransform = local,
                InverseBindMatrix = bone.InverseBindMatrix,
                NativeChecksum = bone.NameChecksum
            });
        }

        return result;
    }
}
