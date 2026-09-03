using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendPackageManifest
{
    // Project sets PublishTrimmed=true, which disables reflection-based JSON
    // serialization globally. The Blender package manifest is a small helper
    // payload consumed only by BlenderExporter/import_package.py, so attach an
    // explicit resolver for this bounded use.
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public int PackageVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string SourceKind { get; init; }
    public required string BlendPath { get; init; }
    public required List<BlendTextureManifest> Textures { get; init; }
    public required List<BlendMaterialManifest> Materials { get; init; }
    public required List<BlendMeshManifest> Meshes { get; init; }
    public required List<BlendNodeManifest> Nodes { get; init; }
    public required List<BlendSkeletonManifest> Skeletons { get; init; }
    public required List<BlendPerspectiveCameraManifest> PerspectiveCameras { get; init; }
    public required List<BlendAnimationManifest> Animations { get; init; }
    public required List<BlendColourPulseChannelManifest> ColourPulseChannels { get; init; }
    public required List<Dictionary<string, object?>> NativeMetadata { get; init; }

    public static BlendPackageManifest FromDocument(
        ModelDocument document,
        string blendPath,
        List<BlendTextureManifest>? textures = null,
        List<BlendMeshManifest>? meshes = null,
        List<BlendNodeManifest>? nodes = null,
        List<BlendSkeletonManifest>? skeletons = null,
        List<BlendAnimationManifest>? animations = null,
        List<BlendColourPulseChannelManifest>? colourPulseChannels = null)
    {
        return new BlendPackageManifest
        {
            Name = document.Name,
            SourceKind = document.SourceKind.ToString(),
            BlendPath = blendPath,
            Textures = textures ?? document.Textures.Select(static texture => new BlendTextureManifest
            {
                Name = texture.Name,
                PngPath = null,
                WrapU = texture.WrapU.ToString(),
                WrapV = texture.WrapV.ToString(),
                NativeChecksum = texture.NativeChecksum
            }).ToList(),
            Materials = document.Materials.Select(static material => new BlendMaterialManifest
            {
                Name = material.Name,
                BaseColor = ToArray(material.BaseColor),
                TextureIndex = material.TextureIndex,
                AlphaMode = material.AlphaMode.ToString(),
                AlphaCutoff = material.AlphaCutoff,
                DoubleSided = material.DoubleSided,
                Unlit = material.Unlit,
                NativeMetadata = material.NativeMetadata.Select(ToDictionary).ToList()
            }).ToList(),
            Meshes = meshes ?? [],
            Nodes = nodes ?? [],
            Skeletons = skeletons ?? [],
            PerspectiveCameras = document.PerspectiveCameras
                .Where(camera => ModelPerspectiveCameraValidation.IsValid(document, camera))
                .Select(static camera => new BlendPerspectiveCameraManifest
                {
                    Name = camera.Name,
                    SkeletonIndex = camera.SkeletonIndex,
                    BoneIndex = camera.BoneIndex,
                    AspectRatio = camera.AspectRatio,
                    VerticalFieldOfViewRadians = camera.VerticalFieldOfViewRadians,
                    ZNear = camera.ZNear,
                    ZFar = camera.ZFar
                }).ToList(),
            Animations = animations ?? [],
            ColourPulseChannels = colourPulseChannels ?? [],
            NativeMetadata = document.NativeMetadata.Select(ToDictionary).ToList()
        };
    }

    internal static Dictionary<string, object?> ToDictionary(NativeRenderMetadata metadata)
    {
        var result = new Dictionary<string, object?>
        {
            ["kind"] = metadata.Kind
        };

        switch (metadata)
        {
            case Ps2GsRenderMetadata ps2:
                result["alpha"] = ps2.Alpha;
                result["test"] = ps2.Test;
                result["frame"] = ps2.Frame;
                result["tex0"] = ps2.Tex0;
                result["tex1"] = ps2.Tex1;
                result["texa"] = ps2.Texa;
                result["clamp"] = ps2.Clamp;
                result["textureChecksum"] = ps2.TextureChecksum;
                result["groupChecksum"] = ps2.GroupChecksum;
                result["alphaRef"] = ps2.AlphaRef;
                result["source"] = ps2.Source;
                break;
            case DdmBlendRenderMetadata ddm:
                result["blendMode"] = ddm.BlendMode;
                result["drawOrder"] = ddm.DrawOrder;
                result["textureName"] = ddm.TextureName;
                result["diffuseR"] = ddm.DiffuseR;
                result["diffuseG"] = ddm.DiffuseG;
                result["diffuseB"] = ddm.DiffuseB;
                result["diffuseA"] = ddm.DiffuseA;
                break;
            case RwGsAlphaRenderMetadata rw:
                result["gsAlpha"] = rw.GsAlpha;
                result["gsAlphaFix"] = rw.GsAlphaFix;
                result["isAdditive"] = rw.IsAdditive;
                result["isSubtractive"] = rw.IsSubtractive;
                result["isBlend"] = rw.IsBlend;
                result["textureName"] = rw.TextureName;
                break;
            case XbxMaterialRenderMetadata xbx:
                result["checksum"] = xbx.Checksum;
                result["nameChecksum"] = xbx.NameChecksum;
                result["alphaCutoff"] = xbx.AlphaCutoff;
                result["sorted"] = xbx.Sorted;
                result["drawOrder"] = xbx.DrawOrder;
                result["zBias"] = xbx.ZBias;
                result["firstTextureChecksum"] = xbx.FirstTextureChecksum;
                result["pass0BlendMode"] = xbx.Pass0BlendMode;
                result["pass0FixedAlpha"] = xbx.Pass0FixedAlpha;
                result["passCount"] = xbx.PassCount;
                if (xbx.BakedRecipe != null)
                    result["bakedRecipe"] = xbx.BakedRecipe;
                break;
            case CollisionRenderMetadata collision:
                result["objectCount"] = collision.ObjectCount;
                break;
            case CollisionOverlayRenderMetadata collisionOverlay:
                result["companionName"] = collisionOverlay.CompanionName;
                result["objectCount"] = collisionOverlay.ObjectCount;
                result["triangleCount"] = collisionOverlay.TriangleCount;
                break;
            case RwBspCollisionFlagsRenderMetadata rwBspCollisionFlags:
                result["collisionFlags"] = rwBspCollisionFlags.CollisionFlags;
                break;
            case PsxCollisionFlagsRenderMetadata psxCollisionFlags:
                result["collisionFlags"] = psxCollisionFlags.CollisionFlags;
                result["loaderInvisible"] = psxCollisionFlags.LoaderInvisible;
                break;
            case NgcCollisionRenderMetadata ngcCollision:
                result["companionName"] = ngcCollision.CompanionName;
                result["positionPoolKind"] = ngcCollision.PositionPoolKind;
                result["objectCount"] = ngcCollision.ObjectCount;
                result["triangleCount"] = ngcCollision.TriangleCount;
                break;
            case PspLevelCompositionMetadata pspLevel:
                result["game"] = pspLevel.Game;
                result["structureName"] = pspLevel.StructureName;
                result["levelSceneName"] = pspLevel.LevelSceneName;
                result["skySceneName"] = pspLevel.SkySceneName;
                result["outerShellSceneName"] = pspLevel.OuterShellSceneName;
                result["isNetworkVariant"] = pspLevel.IsNetworkVariant;
                break;
            case Ps2WorldzoneRenderMetadata worldzone:
                result["sourceName"] = worldzone.SourceName;
                result["mdlCount"] = worldzone.MdlCount;
                result["timeOfDay"] = worldzone.TimeOfDay;
                result["coordinateScale"] = worldzone.CoordinateScale;
                break;
            case Ps2WorldzoneLeafRenderMetadata worldzoneLeaf:
                result["mdlName"] = worldzoneLeaf.MdlName;
                result["leafIndex"] = worldzoneLeaf.LeafIndex;
                result["space"] = worldzoneLeaf.Space;
                result["renderLayer"] = worldzoneLeaf.RenderLayer;
                result["renderOrder"] = worldzoneLeaf.RenderOrder;
                result["isBillboard"] = worldzoneLeaf.IsBillboard;
                result["isLocalSpace"] = worldzoneLeaf.IsLocalSpace;
                result["nodeColour"] = worldzoneLeaf.NodeColour;
                result["nodeFlags"] = worldzoneLeaf.NodeFlags;
                result["drawIndex"] = worldzoneLeaf.DrawIndex;
                result["passIndex"] = worldzoneLeaf.PassIndex;
                result["overlapGroup"] = worldzoneLeaf.OverlapGroup;
                result["blendOffset"] = new[]
                {
                    worldzoneLeaf.BlendOffsetX, worldzoneLeaf.BlendOffsetY, worldzoneLeaf.BlendOffsetZ
                };
                break;
            case MeshDrawOrderMetadata drawOrder:
                result["drawIndex"] = drawOrder.DrawIndex;
                result["passIndex"] = drawOrder.PassIndex;
                result["overlapGroup"] = drawOrder.OverlapGroup;
                result["blendOffset"] = new[]
                {
                    drawOrder.BlendOffsetX, drawOrder.BlendOffsetY, drawOrder.BlendOffsetZ
                };
                break;
            case PsxSkyBackdropMetadata backdrop:
                // The engine's framebuffer clear colour (TRG SetSkyColor),
                // recorded at document scope; a region can name one while
                // owning no sky mesh. R<<16 | G<<8 | B.
                result["skyColor"] = backdrop.SkyColor;
                break;
            case PsxSemiTransparentLiftMetadata semiLift:
                // Informational round-trip record of the already-BAKED vertex
                // lift. Deliberately not "blendOffset" — the importer would
                // re-apply that key at object level and double the lift.
                result["steps"] = semiLift.Steps;
                result["bakedLiftDirection"] = new[]
                {
                    semiLift.DirectionX, semiLift.DirectionY, semiLift.DirectionZ
                };
                break;
            case Ps2WorldzoneBillboardMetadata billboard:
                result["billboardKind"] = billboard.BillboardKind;
                result["anchor"] = new[] { billboard.AnchorX, billboard.AnchorY, billboard.AnchorZ };
                result["size"] = new[] { billboard.Width, billboard.Height };
                result["pivot"] = new[] { billboard.PivotX, billboard.PivotY, billboard.PivotZ };
                result["axis"] = new[] { billboard.AxisX, billboard.AxisY, billboard.AxisZ };
                break;
            case PsxAxialBillboardMetadata axialBillboard:
                // PSX sprite-vertex quad: mesh-local glTF units; the importer
                // attaches a Locked Track constraint about the axis.
                result["anchor"] = new[]
                {
                    axialBillboard.AnchorX, axialBillboard.AnchorY, axialBillboard.AnchorZ
                };
                result["axis"] = new[]
                {
                    axialBillboard.AxisX, axialBillboard.AxisY, axialBillboard.AxisZ
                };
                break;
        }

        return result;
    }

    private static float[] ToArray(Vector4 value)
    {
        return [value.X, value.Y, value.Z, value.W];
    }
}
