namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

public readonly record struct ZoneTextureCatalogEntry(
    uint Checksum,
    ulong Tex0,
    string SourceLabel,
    string EntryLabel);
