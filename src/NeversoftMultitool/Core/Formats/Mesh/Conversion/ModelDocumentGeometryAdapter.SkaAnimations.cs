using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    public static void PopulateSkaAnimations(
        ModelDocument document,
        int skeletonIndex,
        IReadOnlyList<(string Name, SkaAnimation Animation)> animations,
        SkaCompositionMode composition = SkaCompositionMode.Raw,
        IReadOnlyList<int>? boneIndexMap = null)
    {
        if ((uint)skeletonIndex >= (uint)document.Skeletons.Count)
            return;

        var skeleton = document.Skeletons[skeletonIndex];
        foreach (var (name, animation) in animations)
        {
            if (animation.BoneTracks.Length == 0)
                continue;

            var modelAnimation = new ModelAnimation { Name = name };
            for (var trackIndex = 0; trackIndex < animation.BoneTracks.Length; trackIndex++)
            {
                var track = animation.BoneTracks[trackIndex];
                var boneIndex = ResolveBoneIndex(boneIndexMap, trackIndex);
                if ((uint)boneIndex >= (uint)skeleton.Bones.Count)
                    continue;

                AddRotationChannel(modelAnimation, skeletonIndex, boneIndex,
                    skeleton.Bones[boneIndex], track.RotationKeys, composition);
                AddTranslationChannel(modelAnimation, skeletonIndex, boneIndex,
                    skeleton.Bones[boneIndex], track.TranslationKeys, composition);
            }

            if (modelAnimation.Channels.Count > 0)
                document.Animations.Add(modelAnimation);
        }
    }

    private static int ResolveBoneIndex(IReadOnlyList<int>? boneIndexMap, int trackIndex)
    {
        if (boneIndexMap == null)
            return trackIndex;
        return (uint)trackIndex < (uint)boneIndexMap.Count ? boneIndexMap[trackIndex] : -1;
    }

    private static void AddRotationChannel(
        ModelAnimation animation,
        int skeletonIndex,
        int boneIndex,
        ModelBone bone,
        SkaRotationKey[] keys,
        SkaCompositionMode composition)
    {
        if (keys.Length == 0 || IsRotationPlaceholder(keys))
            return;

        var bindRotation = ExtractBindRotation(bone);
        var times = new float[keys.Length];
        var values = new float[keys.Length * 4];
        var previous = Quaternion.Identity;
        for (var i = 0; i < keys.Length; i++)
        {
            var q = Quaternion.Normalize(keys[i].Rotation);
            if (composition == SkaCompositionMode.BindComposed)
                q = Quaternion.Normalize(Quaternion.Multiply(bindRotation, q));

            if (i > 0)
            {
                var dot = q.X * previous.X + q.Y * previous.Y + q.Z * previous.Z + q.W * previous.W;
                if (dot < 0f)
                    q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            }

            times[i] = keys[i].Time;
            var offset = i * 4;
            values[offset] = q.X;
            values[offset + 1] = q.Y;
            values[offset + 2] = q.Z;
            values[offset + 3] = q.W;
            previous = q;
        }

        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = skeletonIndex,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Rotation,
            Times = times,
            Values = values
        });
    }

    private static void AddTranslationChannel(
        ModelAnimation animation,
        int skeletonIndex,
        int boneIndex,
        ModelBone bone,
        SkaTranslationKey[] keys,
        SkaCompositionMode composition)
    {
        if (keys.Length == 0 || IsTranslationPlaceholder(keys))
            return;

        var bindTranslation = ExtractBindTranslation(bone);
        var anchor = keys[0].Translation;
        var times = new float[keys.Length];
        var values = new float[keys.Length * 3];
        for (var i = 0; i < keys.Length; i++)
        {
            var t = composition == SkaCompositionMode.BindComposed
                ? bindTranslation + (keys[i].Translation - anchor)
                : keys[i].Translation;
            times[i] = keys[i].Time;
            var offset = i * 3;
            values[offset] = t.X;
            values[offset + 1] = t.Y;
            values[offset + 2] = t.Z;
        }

        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = skeletonIndex,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Translation,
            Times = times,
            Values = values
        });
    }

    private static bool IsRotationPlaceholder(SkaRotationKey[] keys)
    {
        return keys.Length == 1 && keys[0].Rotation == Quaternion.Identity;
    }

    private static bool IsTranslationPlaceholder(SkaTranslationKey[] keys)
    {
        return keys.Length == 1 && keys[0].Translation == Vector3.Zero;
    }

    private static Quaternion ExtractBindRotation(ModelBone bone)
    {
        return Matrix4x4.Decompose(bone.LocalTransform, out _, out var rotation, out _)
            ? rotation
            : Quaternion.Identity;
    }

    private static Vector3 ExtractBindTranslation(ModelBone bone)
    {
        return Matrix4x4.Decompose(bone.LocalTransform, out _, out _, out var translation)
            ? translation
            : Vector3.Zero;
    }

    public static IReadOnlyList<int>? BuildRwDffBoneIndexMap(RwSkinData? skin)
    {
        if (skin == null)
            return null;

        // RW DFF stores Skin PLG bones in a HAnim-traversal order that may not
        // match the parent-chain order used by the rest of the IR. Build a
        // target→source permutation by reading the skin's per-bone Index field
        // (where each bone "wants" to land); if every slot is consumed exactly
        // once and the result is non-identity, we return it as the index map.
        var n = skin.Bones.Length;
        var permutation = new int[n];
        var seen = new bool[n];
        for (var source = 0; source < n; source++)
        {
            var target = skin.Bones[source].Index;
            if (target < 0 || target >= n || seen[target])
                return null;
            seen[target] = true;
            permutation[target] = source;
        }

        for (var i = 0; i < n; i++)
        {
            if (permutation[i] != i)
                return permutation;
        }

        return null;
    }
}

internal enum SkaCompositionMode
{
    Raw,
    BindComposed
}
