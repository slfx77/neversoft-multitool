namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal readonly record struct Ps2DestinationAlphaMaskCandidate(
    Ps2DestinationAlphaLeafGeometryKey Geometry,
    uint TextureChecksum,
    Ps2GeomLeaf Leaf);
