namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomDebugCollector(string mdlName)
{
    public string MdlName { get; } = mdlName;

    public Func<ulong, uint, Ps2GeomTextureResolution>? TextureResolver { get; init; }

    public List<Ps2GeomMaterialDebugRecord> Materials { get; } = [];

    public List<Ps2GeomLeafRejection> LeafRejections { get; } = [];

    public void AddMaterial(Ps2GeomMaterialDebugRecord record)
    {
        Materials.Add(record);
    }

    public void AddRejection(Ps2GeomLeafRejection record)
    {
        LeafRejections.Add(record);
    }
}
