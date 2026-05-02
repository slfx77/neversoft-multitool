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
}

[Flags]
internal enum GsRegisterMask
{
    None = 0,
    Tex0 = 1 << 0,
    Tex1 = 1 << 1,
    MipTbp1 = 1 << 2,
    MipTbp2 = 1 << 3,
    Clamp1 = 1 << 4,
    Alpha1 = 1 << 5,
    Test1 = 1 << 6
}

internal readonly record struct Ps2GeomGsContextScan(Ps2GeomGsContext Context, GsRegisterMask Present)
{
    public bool HasRegisters => Present != GsRegisterMask.None;

    public Ps2GeomGsContext MergeWith(Ps2GeomGsContext inherited)
    {
        var merged = inherited;

        if ((Present & GsRegisterMask.Tex0) != 0) merged.Tex0 = Context.Tex0;
        if ((Present & GsRegisterMask.Tex1) != 0) merged.Tex1 = Context.Tex1;
        if ((Present & GsRegisterMask.MipTbp1) != 0) merged.MipTbp1 = Context.MipTbp1;
        if ((Present & GsRegisterMask.MipTbp2) != 0) merged.MipTbp2 = Context.MipTbp2;
        if ((Present & GsRegisterMask.Clamp1) != 0) merged.Clamp1 = Context.Clamp1;
        if ((Present & GsRegisterMask.Alpha1) != 0) merged.Alpha1 = Context.Alpha1;
        if ((Present & GsRegisterMask.Test1) != 0) merged.Test1 = Context.Test1;

        return merged;
    }
}
