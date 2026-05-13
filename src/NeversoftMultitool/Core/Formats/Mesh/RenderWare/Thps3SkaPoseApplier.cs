using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

internal static class Thps3SkaPoseApplier
{
    public static void ApplyAnimationChannels(
        NodeBuilder[] boneNodes,
        SkaAnimation animation,
        string animationName,
        Thps3SkaAnimationMode mode)
    {
        var count = Math.Min(boneNodes.Length, animation.BoneTracks.Length);
        for (var i = 0; i < count; i++)
        {
            var track = animation.BoneTracks[i];
            var node = boneNodes[i];

            Matrix4x4.Decompose(node.LocalMatrix, out _, out var bindRot, out var bindTrans);

            if (track.RotationKeys.Length > 0 && !IsRotationPlaceholder(track.RotationKeys))
            {
                var composed = new SkaRotationKey[track.RotationKeys.Length];
                for (var k = 0; k < track.RotationKeys.Length; k++)
                {
                    var q = ResolveRotation(bindRot, track.RotationKeys[k].Rotation, mode.RotationMode);
                    composed[k] = new SkaRotationKey(track.RotationKeys[k].Time, q);
                }

                composed = NormalizeHemispheres(composed);
                var rot = node.UseRotation(animationName);
                foreach (var k in composed)
                    rot.SetPoint(k.Time, k.Rotation);
            }

            if (track.TranslationKeys.Length > 0 && !IsTranslationPlaceholder(track.TranslationKeys))
            {
                var t0 = track.TranslationKeys[0].Translation;
                var trans = node.UseTranslation(animationName);
                foreach (var k in track.TranslationKeys)
                {
                    var resolved = ResolveTranslation(bindTrans, k.Translation, t0, mode.TranslationMode);
                    trans.SetPoint(k.Time, resolved);
                }
            }
        }
    }

    internal static Quaternion ResolveRotation(
        Quaternion bindRotation,
        Quaternion skaRotation,
        Thps3SkaRotationMode mode)
    {
        var q = Quaternion.Normalize(skaRotation);
        if (mode is Thps3SkaRotationMode.BindConjugated or Thps3SkaRotationMode.DirectConjugated)
            q = Quaternion.Conjugate(q);

        return mode switch
        {
            Thps3SkaRotationMode.BindRaw or Thps3SkaRotationMode.BindConjugated
                => Quaternion.Normalize(Quaternion.Multiply(bindRotation, q)),
            _ => q
        };
    }

    internal static Vector3 ResolveTranslation(
        Vector3 bindTranslation,
        Vector3 skaTranslation,
        Vector3 firstSkaTranslation,
        Thps3SkaTranslationMode mode)
    {
        return mode == Thps3SkaTranslationMode.Raw
            ? skaTranslation
            : bindTranslation + (skaTranslation - firstSkaTranslation);
    }

    public static Thps3HAnimBoneOrderReport AnalyzeBoneOrder(RwSkinData skin)
    {
        var ids = new int[skin.Bones.Length];
        var indices = new int[skin.Bones.Length];
        for (var i = 0; i < skin.Bones.Length; i++)
        {
            ids[i] = skin.Bones[i].Id;
            indices[i] = skin.Bones[i].Index;
        }

        var idStatus = AnalyzeOrder(ids, out var idPermutation);
        var indexStatus = AnalyzeOrder(indices, out var indexPermutation);
        return new Thps3HAnimBoneOrderReport(idStatus, idPermutation, indexStatus, indexPermutation);
    }

    private static Thps3HAnimBoneOrderStatus AnalyzeOrder(int[] values, out int[]? targetToSource)
    {
        targetToSource = null;
        var n = values.Length;
        var seen = new bool[n];
        var permutation = new int[n];

        for (var source = 0; source < n; source++)
        {
            var target = values[source];
            if (target < 0 || target >= n || seen[target])
                return Thps3HAnimBoneOrderStatus.Invalid;

            seen[target] = true;
            permutation[target] = source;
        }

        var exact = true;
        for (var i = 0; i < n; i++)
        {
            if (permutation[i] != i)
            {
                exact = false;
                break;
            }
        }

        if (exact)
            return Thps3HAnimBoneOrderStatus.Exact;

        targetToSource = permutation;
        return Thps3HAnimBoneOrderStatus.UsablePermutation;
    }

    private static bool IsRotationPlaceholder(SkaRotationKey[] keys)
    {
        return keys.Length == 1 && keys[0].Rotation == Quaternion.Identity;
    }

    private static bool IsTranslationPlaceholder(SkaTranslationKey[] keys)
    {
        return keys.Length == 1 && keys[0].Translation == Vector3.Zero;
    }

    private static SkaRotationKey[] NormalizeHemispheres(SkaRotationKey[] keys)
    {
        if (keys.Length < 2) return keys;

        var result = new SkaRotationKey[keys.Length];
        result[0] = keys[0];
        for (var i = 1; i < keys.Length; i++)
        {
            var q = keys[i].Rotation;
            var prev = result[i - 1].Rotation;
            var dot = q.X * prev.X + q.Y * prev.Y + q.Z * prev.Z + q.W * prev.W;
            if (dot < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            result[i] = new SkaRotationKey(keys[i].Time, q);
        }

        return result;
    }
}
