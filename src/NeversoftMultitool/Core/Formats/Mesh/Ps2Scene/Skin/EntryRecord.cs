namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

internal struct EntryRecord
{
    public uint MaterialChecksum { get; set; }
    public uint MaterialFlags { get; set; }
    public uint GsAlphaLow { get; set; }
    public uint GsAlphaHigh { get; set; }
    public uint TextureChecksum { get; set; }
    public uint OwnerObjectChecksum { get; set; }
    public bool HasVertexColors { get; set; }
}
