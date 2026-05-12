namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public enum MeshOutputFormat
{
    Glb,
    Blend,
    Both
}

public enum ModelPrimitiveTopology
{
    Triangles
}

public enum ModelAlphaMode
{
    Opaque,
    Mask,
    Blend
}

public enum ModelTextureWrap
{
    Repeat,
    ClampToEdge
}

public enum ModelSourceKind
{
    Generic,
    Collision,
    Ddm,
    DdmPlacedLevel,
    Psx,
    Ps2Scene,
    Ps2Geom,
    Ps2Worldzone,
    XbxScene,
    RenderWareDff,
    RenderWareBsp
}
