namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Native render metadata for THUG2/THAW Xbox/PC/GC scene materials.
///     <paramref name="Pass0BlendMode" /> is the engine vBLEND_MODE (1-6 =
///     ADD..BLEND_FIXED) deciding the framebuffer blend;
///     <paramref name="BakedRecipe" /> is set by the geometry writer when a
///     pass-0 ADD/SUBTRACT blend was baked into the document texture
///     ("additive" / "subtractive") so the Blender importer can pick the
///     matching shader recipe.
/// </summary>
public sealed record XbxMaterialRenderMetadata(
    uint Checksum,
    uint NameChecksum,
    int AlphaCutoff,
    bool Sorted,
    float DrawOrder,
    int ZBias,
    uint? FirstTextureChecksum,
    uint Pass0BlendMode = 0,
    uint Pass0FixedAlpha = 0,
    int PassCount = 0,
    string? BakedRecipe = null)
    : NativeRenderMetadata("xbx_material");
