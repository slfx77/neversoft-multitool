namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

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
    Test1 = 1 << 6,
    Texa = 1 << 7,
    Frame1 = 1 << 8
}
