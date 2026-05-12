namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public abstract record NativeRenderMetadata(string Kind);

public sealed record Ps2GsRenderMetadata(
    ulong? Alpha,
    ulong? Test,
    ulong? Tex0,
    ulong? Tex1,
    ulong? Texa,
    ulong? Clamp,
    uint? TextureChecksum,
    uint? GroupChecksum,
    int? AlphaRef,
    string? Source,
    ulong? Frame = null)
    : NativeRenderMetadata("ps2_gs");

public sealed record DdmBlendRenderMetadata(
    uint BlendMode,
    uint DrawOrder,
    string TextureName,
    byte DiffuseR,
    byte DiffuseG,
    byte DiffuseB,
    byte DiffuseA)
    : NativeRenderMetadata("ddm_blend");

public sealed record RwGsAlphaRenderMetadata(
    byte GsAlpha,
    byte GsAlphaFix,
    bool IsAdditive,
    bool IsSubtractive,
    bool IsBlend,
    string? TextureName)
    : NativeRenderMetadata("rw_gs_alpha");

public sealed record XbxMaterialRenderMetadata(
    uint Checksum,
    uint NameChecksum,
    int AlphaCutoff,
    bool Sorted,
    float DrawOrder,
    int ZBias,
    uint? FirstTextureChecksum)
    : NativeRenderMetadata("xbx_material");

public sealed record CollisionRenderMetadata(int ObjectCount)
    : NativeRenderMetadata("collision");

public sealed record Ps2WorldzoneRenderMetadata(
    string SourceName,
    int MdlCount,
    string TimeOfDay,
    float CoordinateScale)
    : NativeRenderMetadata("ps2_worldzone");

public sealed record Ps2WorldzoneLeafRenderMetadata(
    string MdlName,
    int LeafIndex,
    string Space,
    string RenderLayer,
    uint RenderOrder,
    bool IsBillboard,
    bool IsLocalSpace,
    uint NodeColour = 0x80808080,
    uint NodeFlags = 0)
    : NativeRenderMetadata("ps2_worldzone_leaf");

/// <summary>
///     Per-primitive descriptor for a THAW worldzone Format-B billboard. Emitted alongside
///     <see cref="Ps2WorldzoneLeafRenderMetadata"/> when the leaf is a billboard. The Blender
///     importer reads this to attach a Track-To constraint that orients the quad toward the
///     active camera at render time; glTF consumers ignore it and use the static fallback
///     geometry instead. Coordinates are in PS2 source-space; the importer applies the
///     Y_UP_TO_Z_UP transform at scene load.
///     <c>BillboardKind</c> is the variant name ("ScreenAligned" / "LongAxis" / "ShortAxis"),
///     distinct from the base <c>Kind</c> field which discriminates metadata types.
/// </summary>
public sealed record Ps2WorldzoneBillboardMetadata(
    string BillboardKind,
    float AnchorX, float AnchorY, float AnchorZ,
    float Width, float Height,
    float PivotX, float PivotY, float PivotZ,
    float AxisX, float AxisY, float AxisZ)
    : NativeRenderMetadata("ps2_worldzone_billboard");
