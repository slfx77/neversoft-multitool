namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Shared validation for perspective-camera records before an exporter
///     publishes them. Invalid records are omitted rather than allowing one
///     backend to accept projection data another backend rejects.
/// </summary>
internal static class ModelPerspectiveCameraValidation
{
    public static bool IsValid(ModelDocument document, ModelPerspectiveCamera camera)
    {
        if ((uint)camera.SkeletonIndex >= (uint)document.Skeletons.Count)
            return false;

        var skeleton = document.Skeletons[camera.SkeletonIndex];
        return (uint)camera.BoneIndex < (uint)skeleton.Bones.Count && IsValidProjection(camera);
    }

    private static bool IsValidProjection(ModelPerspectiveCamera camera)
    {
        var validAspect = !camera.AspectRatio.HasValue ||
                          float.IsFinite(camera.AspectRatio.Value) && camera.AspectRatio.Value > 0f;
        var validFar = float.IsFinite(camera.ZFar) && camera.ZFar > camera.ZNear;
        return validAspect &&
               float.IsFinite(camera.VerticalFieldOfViewRadians) &&
               camera.VerticalFieldOfViewRadians is > 0f and < MathF.PI &&
               float.IsFinite(camera.ZNear) && camera.ZNear > 0f &&
               validFar;
    }
}
