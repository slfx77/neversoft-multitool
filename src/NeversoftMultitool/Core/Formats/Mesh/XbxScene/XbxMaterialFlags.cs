namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Material/pass flag bits shared by the THUG2/THAW Xbox, PC, and GC scene
///     formats, transcribed from the engine source
///     (<c>Sample/thug/Code/Gfx/XBox/NX/material.h:13-28</c>).
/// </summary>
internal static class XbxMaterialFlags
{
    public const uint UvWibble = 1u << 0;
    public const uint VcWibble = 1u << 1;
    public const uint Textured = 1u << 2;
    public const uint Environment = 1u << 3;
    public const uint Decal = 1u << 4;
    public const uint Smooth = 1u << 5;
    public const uint Transparent = 1u << 6;
    public const uint PassColorLocked = 1u << 7;
    public const uint Specular = 1u << 8;
    public const uint PassTextureAnimates = 1u << 11;
    public const uint PassIgnoreVertexAlpha = 1u << 12;
    public const uint ExplicitUvWibble = 1u << 14;
    public const uint WaterEffect = 1u << 27;
    public const uint NoMatColMod = 1u << 28;

    /// <summary>
    ///     Flags that disqualify a pass-k (k ≥ 1) overlay from static
    ///     compositing: environment passes use generated (camera-dependent) UVs,
    ///     wibbled/animated passes are frame-dependent, and water-effect passes
    ///     are post-processed by the engine.
    /// </summary>
    public const uint OverlayCompositingSkipMask =
        UvWibble | Environment | PassTextureAnimates | ExplicitUvWibble | WaterEffect;
}
