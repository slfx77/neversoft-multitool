using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Builds a <see cref="ModelDocument" /> that carries a skeleton plus one or more
///     SKA animation tracks but no mesh data. Used by the <c>ska</c> CLI when a skeleton
///     is provided without a companion skin mesh: the resulting glTF contains the joint
///     hierarchy and animation channels only.
/// </summary>
public static class SkaModelDocumentBuilder
{
    internal const float ThawCameraAspectRatio = 4f / 3f;

    // Projection clipping is not authored in a THAW SKA. Preserve source-space
    // translations and use a deliberately broad PS2-derived export policy so a
    // camera rig is useful without pretending these values came from the file.
    internal const float ThawCameraZNear = 1f;
    internal const float ThawCameraZFar = 100_000f;

    public static ModelDocument BuildSkeletonOnly(
        Ps2Skeleton skeleton,
        IReadOnlyList<(string Name, SkaAnimation Animation)> animations,
        string? name = null,
        SkaQbKeyBoneMap? boneIndexMap = null)
    {
        var document = new ModelDocument { Name = name ?? "skeleton" };
        var skeletonIndex = document.Skeletons.Count;
        document.Skeletons.Add(Ps2SceneGeometryWriter.BuildPs2Skeleton(skeleton));
        SkaAnimationWriter.PopulateSkaAnimations(
            document, skeletonIndex, animations, boneIndexMap: boneIndexMap);
        // A glTF node can reference only one camera. The CLI's supported camera
        // route supplies one master at a time; keep a multi-clip skeleton
        // document camera-free rather than choosing an ambiguous projection.
        if (animations.Count == 1)
        {
            var (animationName, animation) = animations[0];
            TryAddThawPerspectiveCamera(document, skeletonIndex, animationName, animation);
        }
        return document;
    }

    /// <summary>
    ///     Adds the static frame-zero projection proven for one-track THAW
    ///     camera masters. Later FOV custom events remain available in the JSON
    ///     sidecar; glTF has no portable camera-lens animation channel.
    /// </summary>
    internal static bool TryAddThawPerspectiveCamera(
        ModelDocument document,
        int skeletonIndex,
        string animationName,
        SkaAnimation animation)
    {
        if (!animation.IsThawFormat ||
            !animation.IsPlatformFormat ||
            !animation.IsCameraData ||
            animation.BoneTracks.Length != 1 ||
            (uint)skeletonIndex >= (uint)document.Skeletons.Count ||
            document.Skeletons[skeletonIndex].Bones.Count != 1 ||
            document.PerspectiveCameras.Any(camera =>
                camera.SkeletonIndex == skeletonIndex && camera.BoneIndex == 0))
        {
            return false;
        }

        // The engine consumes type 1 as a horizontal FOV in radians. If a
        // malformed file repeats frame zero, the last serialized record is
        // authoritative. Validate only after selecting it: an invalid later
        // event must not silently fall back to an earlier valid projection.
        SkaCustomKey? frameZeroFov = null;
        foreach (var key in animation.CustomKeys)
        {
            if (key.Type == 1 && key.Timestamp == 0)
                frameZeroFov = key;
        }

        if (frameZeroFov?.Fov is not { } horizontalFov ||
            !float.IsFinite(horizontalFov) || horizontalFov <= 0f || horizontalFov >= MathF.PI)
            return false;

        var verticalFov = HorizontalToVerticalFov(horizontalFov, ThawCameraAspectRatio);
        if (!float.IsFinite(verticalFov) || verticalFov <= 0f || verticalFov >= MathF.PI)
            return false;

        var boneName = document.Skeletons[skeletonIndex].Bones[0].Name;
        document.PerspectiveCameras.Add(new ModelPerspectiveCamera
        {
            Name = string.IsNullOrWhiteSpace(boneName) ? $"{animationName}_camera" : boneName,
            SkeletonIndex = skeletonIndex,
            BoneIndex = 0,
            AspectRatio = ThawCameraAspectRatio,
            VerticalFieldOfViewRadians = verticalFov,
            ZNear = ThawCameraZNear,
            ZFar = ThawCameraZFar
        });
        return true;
    }

    internal static float HorizontalToVerticalFov(float horizontalFovRadians, float aspectRatio) =>
        2f * MathF.Atan(MathF.Tan(horizontalFovRadians / 2f) / aspectRatio);
}
