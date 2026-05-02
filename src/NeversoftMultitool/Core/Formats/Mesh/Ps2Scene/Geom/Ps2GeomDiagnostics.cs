using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public readonly record struct Ps2GeomTextureResolution(
    uint Checksum,
    string ResolveMode,
    string SourceLabel,
    string EntryLabel);

public enum Ps2GeomRenderLayer
{
    Base,
    NightOverlay
}

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

public readonly record struct Ps2GeomLeafRejection(
    string MdlName,
    string Stage,
    string Reason,
    int LeafIndex,
    int VertexCount,
    ulong Tex0,
    Vector3 Min,
    Vector3 Max);

public readonly record struct Ps2GeomLeafIdDebugRecord(
    string MdlName,
    int LeafIndex,
    int Id,
    string ColorHex,
    string MaterialName,
    uint TextureChecksum,
    uint GroupChecksum,
    ulong Tex0,
    ulong Tex1,
    ulong Clamp1,
    ulong Alpha1,
    ulong Test1,
    string ResolveMode,
    string SourceLabel,
    string EntryLabel,
    Ps2GeomRenderLayer RenderLayer,
    int Triangles,
    int PlacementCount,
    Vector3 Min,
    Vector3 Max,
    bool IsBillboard,
    bool IsLocalSpace);

public sealed class Ps2GeomDebugCollector(string mdlName)
{
    public string MdlName { get; } = mdlName;

    public Func<ulong, uint, Ps2GeomTextureResolution>? TextureResolver { get; init; }

    public List<Ps2GeomMaterialDebugRecord> Materials { get; } = [];

    public List<Ps2GeomLeafRejection> LeafRejections { get; } = [];

    public void AddMaterial(Ps2GeomMaterialDebugRecord record) => Materials.Add(record);

    public void AddRejection(Ps2GeomLeafRejection record) => LeafRejections.Add(record);
}
