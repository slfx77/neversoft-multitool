namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

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
        if ((Present & GsRegisterMask.Texa) != 0) merged.Texa = Context.Texa;
        if ((Present & GsRegisterMask.Frame1) != 0) merged.Frame1 = Context.Frame1;

        return merged;
    }
}
