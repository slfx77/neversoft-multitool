namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public enum ModelTextureWrap
{
    Repeat,
    ClampToEdge,

    /// <summary>
    ///     glTF <c>MIRRORED_REPEAT</c>. Only the N64 ports author it (the RDP
    ///     <c>G_TX_MIRROR</c> bit); the PS2/Xbox material formats expose a
    ///     clamp flag with no mirror state, so those writers never emit it.
    /// </summary>
    MirroredRepeat
}
