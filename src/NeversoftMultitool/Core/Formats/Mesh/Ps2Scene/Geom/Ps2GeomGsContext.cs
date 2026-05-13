namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal struct Ps2GeomGsContext
{
    public ulong Tex0 { get; set; }
    public ulong Tex1 { get; set; }
    public ulong MipTbp1 { get; set; }
    public ulong MipTbp2 { get; set; }
    public ulong Clamp1 { get; set; }
    public ulong Alpha1 { get; set; }
    public ulong Test1 { get; set; }
    public ulong Frame1 { get; set; }

    /// <summary>
    ///     PS2 GS TEXA register (0x3B). Drives alpha expansion for non-32-bit
    ///     pixel formats: PSMCT16 (per-texel bit15), PSMCT24 (no bit15), and
    ///     paletted formats with PSMCT16 CLUTs (CLUT-entry bit15). Each draw
    ///     can use a different TEXA, so it must be tracked per leaf rather
    ///     than baked once into the texture cache.
    ///     Layout: TA0(7:0), AEM(15), TA1(39:32). TA0/TA1 ∈ [0,128] = effective
    ///     opacity; AEM forces alpha=0 when RGB=0,0,0 (16-bit only).
    /// </summary>
    public ulong Texa { get; set; }
}
