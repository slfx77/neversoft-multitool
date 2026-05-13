namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public readonly record struct Ps2GeomTextureResolution(
    uint Checksum,
    string ResolveMode,
    string SourceLabel,
    string EntryLabel);
