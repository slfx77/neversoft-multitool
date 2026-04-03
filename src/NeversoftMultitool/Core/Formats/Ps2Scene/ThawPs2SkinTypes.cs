namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal readonly record struct SetupMappingInfo(
    int[] SetupEntryIndices,
    Dictionary<int, uint>? EntryTextureOverrides,
    Dictionary<int, int>? EntryAlphaRefs);

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

internal struct GsRegisters
{
    public uint Tex0Cbp { get; set; }
    public ulong Alpha1 { get; set; }
    public byte AlphaRef { get; set; }
}
