namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record XbxSceneNativeSource(
    XbxScene.XbxScene Scene,
    MeshChecksumTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.XbxScene);
