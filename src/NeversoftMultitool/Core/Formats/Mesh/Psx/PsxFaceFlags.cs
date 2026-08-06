namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     The Neversoft PS1 face flag word, shared by the PS1 mesh reader and the
///     N64 render bank (whose per-triangle side table stores the same word in
///     its low half, in the same DISC form — measured 2026-08-06: applying the
///     loader rewrite leaves 8.8% of THPS2 N64 faces invisible, whereas reading
///     the raw word as runtime state would discard 89.6% of the corpus).
/// </summary>
public static class PsxFaceFlags
{
    public const ushort TexturePayload = 0x0003;
    public const ushort TriangleBit = 0x0010;
    public const ushort SemiTransparent = 0x0040;

    /// <summary>Loader draw-enable for opaque faces; part of the ABR rate when semi-transparent.</summary>
    public const ushort DrawEnable = 0x0080;

    public const ushort BlendRateMask = 0x0180;
    public const ushort DoubleSided = 0x0200;
    public const ushort Gouraud = 0x0800;

    private const ushort DrawMask = 0x00C0;

    /// <summary>
    ///     Applies the loader's rewrite of a DISC flag word.
    ///     <c>M3dInit_ParsePSX</c> (decomp-verified @0x80093BE4) XORs the
    ///     draw-enable bit into every face whose semi-transparent bit is clear,
    ///     arming the GPU opaque path for solid faces.
    /// </summary>
    public static ushort ApplyLoaderRewrite(ushort discFlags)
    {
        return (discFlags & SemiTransparent) != 0
            ? discFlags
            : (ushort)(discFlags ^ DrawEnable);
    }

    /// <summary>
    ///     True when a DISC flag word denotes a face the engine never draws —
    ///     collision blockers, trigger volumes, camera zones and marker boards.
    ///     <c>M3dAsm_ProcessPolys</c> (@0x800999F0) skips any face whose
    ///     draw bits are both clear after the loader rewrite.
    /// </summary>
    public static bool IsInvisible(ushort discFlags)
    {
        return (ApplyLoaderRewrite(discFlags) & DrawMask) == 0;
    }
}
