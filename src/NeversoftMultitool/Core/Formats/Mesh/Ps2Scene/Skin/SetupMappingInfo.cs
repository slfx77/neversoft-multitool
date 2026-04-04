namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

internal readonly record struct SetupMappingInfo(
    int[] SetupEntryIndices,
    Dictionary<int, uint>? EntryTextureOverrides,
    Dictionary<int, int>? EntryAlphaRefs);
