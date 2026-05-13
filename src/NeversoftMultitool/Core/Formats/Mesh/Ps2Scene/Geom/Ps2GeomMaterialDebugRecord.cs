using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public readonly record struct Ps2GeomMaterialDebugRecord(
    string MdlName,
    string MaterialName,
    uint TextureChecksum,
    uint GroupChecksum,
    ulong Tex0,
    ulong Tex1,
    ulong Clamp1,
    ulong Alpha1,
    ulong Test1,
    string AlphaMode,
    string ResolveMode,
    string SourceLabel,
    string EntryLabel,
    Ps2GeomRenderLayer RenderLayer,
    int Triangles,
    Vector3 Min,
    Vector3 Max,
    bool IsBillboard);
