namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal readonly record struct SetupMappingInfo(
    int[] SetupEntryIndices,
    Dictionary<int, uint>? EntryTextureOverrides,
    Dictionary<int, int>? EntryAlphaRefs);

internal struct EntryRecord
{
    public uint MaterialChecksum;
    public uint MaterialFlags;
    public uint GsAlphaLow;
    public uint GsAlphaHigh;
    public uint TextureChecksum;
    public uint OwnerObjectChecksum;
    public bool HasVertexColors;
}

internal struct GsRegisters
{
    public uint Tex0Cbp;
    public ulong Alpha1;
    public byte AlphaRef;
}
