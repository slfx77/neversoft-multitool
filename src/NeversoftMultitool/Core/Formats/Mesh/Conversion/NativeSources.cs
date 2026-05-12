using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.XbxScene;

using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public abstract record ModelNativeSource(ModelSourceKind Kind);

public sealed record CollisionNativeSource(ColScene Scene)
    : ModelNativeSource(ModelSourceKind.Collision);

public sealed record DdmNativeSource(
    DdmFile File,
    string Name,
    Dictionary<string, byte[]>? DdxTextures,
    List<LitLight>? Lights)
    : ModelNativeSource(ModelSourceKind.Ddm);

public sealed record DdmPlacedLevelNativeSource(
    string LevelDdmPath,
    string LevelPsxPath,
    string? ObjectsDdmPath,
    string? ObjectsPsxPath,
    string LevelName,
    string? DdxPath)
    : ModelNativeSource(ModelSourceKind.DdmPlacedLevel);

public sealed record PsxNativeSource(
    PsxMeshFile File,
    MeshChecksumTextureResolver TextureProvider,
    PshFile? PshFile)
    : ModelNativeSource(ModelSourceKind.Psx);

public sealed record Ps2SceneNativeSource(
    ParsedPs2Scene Scene,
    Ps2Skeleton? Skeleton,
    MeshChecksumTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.Ps2Scene);

public sealed record Ps2GeomNativeSource(
    Ps2GeomScene Scene,
    MeshChecksumTextureResolver? TextureProvider,
    Ps2Tex0ChecksumResolver? Tex0Resolver)
    : ModelNativeSource(ModelSourceKind.Ps2Geom);

public sealed record Ps2WorldzoneNativeSource(AssetSource Source)
    : ModelNativeSource(ModelSourceKind.Ps2Worldzone);

public sealed record XbxSceneNativeSource(
    ParsedXbxScene Scene,
    MeshChecksumTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.XbxScene);

public sealed record RenderWareDffNativeSource(
    RwDffClump Clump,
    MeshNamedTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.RenderWareDff);

public sealed record RenderWareBspNativeSource(
    RwBspWorld World,
    MeshNamedTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.RenderWareBsp);
