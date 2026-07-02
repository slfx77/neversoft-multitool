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
    public static ModelDocument BuildSkeletonOnly(
        Ps2Skeleton skeleton,
        IReadOnlyList<(string Name, SkaAnimation Animation)> animations,
        string? name = null)
    {
        var document = new ModelDocument { Name = name ?? "skeleton" };
        var skeletonIndex = document.Skeletons.Count;
        document.Skeletons.Add(ModelDocumentGeometryAdapter.BuildPs2Skeleton(skeleton));
        ModelDocumentGeometryAdapter.PopulateSkaAnimations(document, skeletonIndex, animations);
        return document;
    }
}
