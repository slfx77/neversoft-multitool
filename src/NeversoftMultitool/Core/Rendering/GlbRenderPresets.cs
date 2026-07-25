namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Shared camera presets for <see cref="GlbRenderer" /> — the single source
///     of truth for the CLI (`glb-render --preset object-review`) and the Mesh
///     Converter tab's render section.
/// </summary>
public static class GlbRenderPresets
{
    /// <summary>Five fixed views for object placement review.</summary>
    public static readonly IReadOnlyList<RenderView> ObjectReview =
    [
        new("front_left", -45f, 20f),
        new("front_right", 45f, 20f),
        new("rear_right", 135f, 20f),
        new("rear_left", -135f, 20f),
        new("top", -45f, 75f)
    ];
}
