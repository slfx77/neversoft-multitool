using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class MeshModelParser : IModelParser
{
    private static readonly string[] Ps2TexExtensions = [".tex.ps2", ".tex", ".img.ps2"];
    private static readonly string[] Ps2TexSubdirs = ["TEX", "Textures", "IMG"];
    private static readonly string[] XbxTexExtensions = [".tex.xbx", ".tex.wpc"];
    private static readonly string[] XbxTexSubdirs = ["TEX", "Textures"];
    private static readonly string[] RwTexExtensions = [".tex"];
    private static readonly string[] RwTexSubdirs = ["TEX", "Textures"];
    private static readonly string[] PcSkinExtensions = [".skin.wpc", ".skin.xbx"];
    private static readonly string[] PcSkinSubdirs = ["SKIN", "Models"];

    public ModelDocument Parse(MeshImportRequest request)
    {
        return request.SourceKind switch
        {
            ModelSourceKind.Collision => ParseCollision(request),
            ModelSourceKind.Ddm => request.HasPlacedPsxCompanion
                ? ParsePlacedDdm(request)
                : ParseDdm(request),
            ModelSourceKind.Psx => ParsePsx(request),
            ModelSourceKind.Ps2Scene => ParsePs2Scene(request),
            ModelSourceKind.Ps2Geom => ParsePs2Geom(request),
            ModelSourceKind.Ps2Worldzone => ParsePs2Worldzone(request),
            ModelSourceKind.XbxScene => ParseXbxScene(request),
            ModelSourceKind.RenderWareDff => ParseRwDff(request),
            ModelSourceKind.RenderWareBsp => ParseRwBsp(request),
            _ => throw new NotSupportedException($"Unsupported mesh source kind: {request.SourceKind}")
        };
    }

    private static ModelDocument ParseCollision(MeshImportRequest request)
    {
        var scene = ColFile.Parse(request.Source.ReadBytes());
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.Collision,
            new CollisionNativeSource(scene),
            scene.Objects.Sum(static obj => obj.Faces.Length));
        document.NativeMetadata.Add(new CollisionRenderMetadata(scene.Objects.Length));
        ModelDocumentGeometryAdapter.PopulateCollision(document, scene);
        return document;
    }

    private static ModelDocument ParseDdm(MeshImportRequest request)
    {
        var ddm = DdmFile.Parse(request.Source.ReadBytes());
        var ddxTextures = LoadDdxCompanion(request.Source, request.OutputStem, request.DdxPath);
        var lights = LoadLitCompanion(request.Source, request.OutputStem);
        var textureDirs = MeshTextureHelper.BuildTextureSearchPaths(request.DdmTexturePath, request.OutputStem);
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.Ddm,
            new DdmNativeSource(ddm, request.OutputStem, ddxTextures, lights));

        foreach (var material in ddm.Objects.SelectMany(static obj => obj.Materials))
        {
            var renderMaterial = new RenderMaterial { Name = material.Name };
            renderMaterial.NativeMetadata.Add(new DdmBlendRenderMetadata(
                material.BlendMode,
                material.DrawOrder,
                material.TextureName,
                material.DiffuseR,
                material.DiffuseG,
                material.DiffuseB,
                material.DiffuseA));
            document.Materials.Add(renderMaterial);
        }

        ModelDocumentGeometryAdapter.PopulateDdm(document, ddm, ddxTextures, textureDirs);
        return document;
    }

    private static ModelDocument ParsePlacedDdm(MeshImportRequest request)
    {
        var ddmPath = request.Source.FileSystemPath;
        if (ddmPath == null)
            return ParseDdm(request);

        var companionPsx = ResolveCompanionPath(
            request.Source,
            request.OutputStem,
            ".psx",
            request.PsxPath);
        if (companionPsx == null)
            return ParseDdm(request);

        var objectsDdm = request.Source.TryResolveCompanionPath(request.OutputStem + "_o.ddm");
        var objectsPsx = objectsDdm != null
            ? ResolveCompanionPath(request.Source, request.OutputStem + "_o", ".psx", request.PsxPath)
            : null;

        var source = new DdmPlacedLevelNativeSource(
            ddmPath,
            companionPsx,
            objectsDdm,
            objectsPsx,
            request.OutputStem,
            Path.GetDirectoryName(ddmPath));
        var document = ModelDocument.CreateNative(request.OutputStem, ModelSourceKind.DdmPlacedLevel, source);
        var levelDdm = DdmFile.Parse(ddmPath);
        var levelPsx = PsxLayoutFile.Parse(companionPsx);
        var objectDdm = objectsDdm != null ? DdmFile.Parse(objectsDdm) : null;
        var objectPsx = objectsPsx != null ? PsxLayoutFile.Parse(objectsPsx) : null;
        var ddxTextures = LoadDdxCompanion(request.Source, request.OutputStem, request.DdxPath);
        var textureDirs = MeshTextureHelper.BuildTextureSearchPaths(request.DdmTexturePath, request.OutputStem);
        ModelDocumentGeometryAdapter.PopulateDdmPlacedLevel(
            document,
            levelDdm,
            levelPsx,
            objectDdm,
            objectPsx,
            ddxTextures,
            textureDirs);
        return document;
    }

    private static ModelDocument ParsePsx(MeshImportRequest request)
    {
        var psxData = request.Source.ReadBytes();
        var psxFile = PsxMeshFile.Parse(psxData)
                      ?? throw new InvalidOperationException("No mesh data");

        var textureProvider = BuildPsxTextureProvider(request.Source, request.FileName, psxData);
        PshFile? pshFile = null;
        if (psxFile.HasHierarchy)
        {
            var stem = Path.GetFileNameWithoutExtension(request.FileName);
            var pshBytes = request.Source.TryReadCompanion(stem + ".psh");
            pshFile = pshBytes != null ? PshFile.Parse(pshBytes) : null;
        }

        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.Psx,
            new PsxNativeSource(psxFile, textureProvider, pshFile));
        ModelDocumentGeometryAdapter.PopulatePsx(
            document, psxFile, textureProvider, pshFile,
            request.PsxFlatSkeleton, request.PsxFlatBoneIndices);

        if (request.PsxAnimationOptions is { } animationOptions
            && document.Skeletons.Count > 0)
        {
            var clips = request.PsxAnimationClips;
            if (clips is { Count: > 0 })
            {
                ModelDocumentGeometryAdapter.PopulatePsxAnimationClips(
                    document, psxFile, skeletonIndex: 0, clips, animationOptions);
            }
            else if (request.PsxDecodedAnimations is { Count: > 0 } animations)
            {
                ModelDocumentGeometryAdapter.PopulatePsxAnimations(
                    document, psxFile, skeletonIndex: 0, animations, animationOptions);
            }
        }

        return document;
    }

    private static ModelDocument ParsePs2Scene(MeshImportRequest request)
    {
        var data = request.Source.ReadBytes();
        var companionTexData = ReadTextureCompanion(
            request.Source,
            request.OutputStem,
            Ps2TexExtensions,
            Ps2TexSubdirs,
            request.TexturePath);
        var textureProvider = BuildPs2TextureProvider(companionTexData);

        if (request.Ps2SubFormat == Ps2SceneSubFormat.PakMdl)
        {
            var geomScene = Ps2GeomFile.ParsePakMdl(data);
            return BuildPs2GeomDocument(request.OutputStem, geomScene, textureProvider, null);
        }

        var scene = request.Ps2SubFormat switch
        {
            Ps2SceneSubFormat.ThawSkin => ThawPs2SkinFile.Parse(data, companionTexData),
            Ps2SceneSubFormat.PakSkin => ThawPs2SkinFile.ParsePakSkin(data),
            _ => Ps2SceneFile.Parse(data)
        };

        var skeleton = request.PreparedSkeleton ?? TryLoadPs2Skeleton(
            request.Source,
            request.OutputStem,
            request.Ps2SubFormat,
            request.SkeletonPath);
        if (skeleton != null && request.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin)
        {
            var pcBytes = request.Source.TryReadCompanion(request.OutputStem, PcSkinExtensions, PcSkinSubdirs);
            var transferred = pcBytes != null
                ? ThawPs2SkinningTransfer.TryApplyFromBytes(scene, pcBytes, skeleton)
                : null;
            if (transferred is { SkinnedVertexCount: > 0 })
                scene = transferred.Scene;
            else
                skeleton = null;
        }

        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.Ps2Scene,
            new Ps2SceneNativeSource(scene, skeleton, textureProvider));

        foreach (var material in scene.Materials)
        {
            var renderMaterial = new RenderMaterial
            {
                Name = QbKey.QbKey.TryResolve(material.Checksum) ?? $"mat_{material.Checksum:X8}"
            };
            renderMaterial.NativeMetadata.Add(new Ps2GsRenderMetadata(
                material.RegAlpha,
                null,
                null,
                null,
                null,
                material.ClampUMode | ((ulong)material.ClampVMode << 2),
                material.TextureChecksum,
                material.GroupChecksum,
                material.AlphaRef,
                "ps2_scene_material"));
            document.Materials.Add(renderMaterial);
        }

        ModelDocumentGeometryAdapter.PopulatePs2Scene(document, scene, textureProvider, skeleton);

        if (request.SkaAnimations is { Count: > 0 } ps2Animations && document.Skeletons.Count > 0)
        {
            ModelDocumentGeometryAdapter.PopulateSkaAnimations(
                document, skeletonIndex: 0, ps2Animations);
        }

        return document;
    }

    private static ModelDocument ParsePs2Geom(MeshImportRequest request)
    {
        var scene = Ps2GeomFile.Parse(request.Source.ReadBytes());
        var companionTexData = ReadTextureCompanion(
            request.Source,
            request.OutputStem,
            Ps2TexExtensions,
            Ps2TexSubdirs,
            request.TexturePath);
        var textureProvider = BuildPs2TextureProvider(companionTexData);
        return BuildPs2GeomDocument(request.OutputStem, scene, textureProvider, null);
    }

    private static ModelDocument BuildPs2GeomDocument(
        string name,
        Ps2GeomScene scene,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        var document = ModelDocument.CreateNative(
            name,
            ModelSourceKind.Ps2Geom,
            new Ps2GeomNativeSource(scene, textureProvider, tex0Resolver));

        foreach (var leaf in scene.Leaves)
        {
            var textureChecksum = leaf.TextureChecksum != 0
                ? leaf.TextureChecksum
                : tex0Resolver?.Invoke(leaf.DmaTex0, leaf.GroupChecksum);
            var materialName = textureChecksum is > 0
                ? QbKey.QbKey.TryResolve(textureChecksum.Value) ?? $"tex_{textureChecksum.Value:X8}"
                : "default";

            var renderMaterial = new RenderMaterial { Name = materialName };
            renderMaterial.NativeMetadata.Add(new Ps2GsRenderMetadata(
                leaf.DmaAlpha1,
                leaf.DmaTest1,
                leaf.DmaTex0,
                leaf.DmaTex1,
                leaf.DmaTexa,
                leaf.DmaClamp1,
                textureChecksum,
                leaf.GroupChecksum,
                (int)((leaf.DmaTest1 >> 4) & 0xFF),
                "ps2_geom_leaf",
                leaf.DmaFrame1));
            document.Materials.Add(renderMaterial);
        }

        ModelDocumentGeometryAdapter.PopulatePs2Geom(document, scene, textureProvider, tex0Resolver);
        return document;
    }

    private static ModelDocument ParsePs2Worldzone(MeshImportRequest request)
    {
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.Ps2Worldzone,
            new Ps2WorldzoneNativeSource(request.Source));

        var pakBytes = request.Source.ReadBytes();
        var texPath = request.TexturePath ?? request.Source.FileSystemPath;
        var textureSourceHint = request.TexturePath ?? request.Source.FileSystemPath;
        MeshChecksumTextureResolver? textureProvider = null;
        Ps2TexaTextureResolver? texaTextureProvider = null;
        Ps2Tex0ChecksumResolver? tex0Resolver = null;
        ZoneTextureCatalog? textureCatalog = null;

        if (ZoneTextureCatalog.TryBuild(texPath, out textureCatalog) && textureCatalog != null)
        {
            textureProvider = textureCatalog.CreateTextureResolver();
            texaTextureProvider = textureCatalog.CreateTexaAwareTextureResolver();
            tex0Resolver = textureCatalog.CreateTex0ChecksumResolver(textureSourceHint);
        }

        ModelDocumentGeometryAdapter.PopulatePs2Worldzone(
            document,
            pakBytes,
            request.Source.EntryName,
            textureProvider,
            texaTextureProvider,
            tex0Resolver,
            textureCatalog,
            textureSourceHint,
            request.WorldzoneTimeOfDay,
            request.WorldzoneScale);
        return document;
    }

    private static ModelDocument ParseXbxScene(MeshImportRequest request)
    {
        var data = request.Source.ReadBytes();
        var scene = ThawSceneFile.IsThawScene(data)
            ? ThawSceneFile.Parse(data)
            : XbxSceneFile.Parse(data);
        var textureProvider = BuildXbxSceneTextureProvider(request.Source, request.OutputStem, request.TexturePath);
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.XbxScene,
            new XbxSceneNativeSource(scene, textureProvider));

        foreach (var material in scene.Materials)
        {
            var firstTexture = material.Passes.Length > 0 ? material.Passes[0].TextureChecksum : (uint?)null;
            var renderMaterial = new RenderMaterial
            {
                Name = QbKey.QbKey.TryResolve(material.Checksum) ?? $"mat_{material.Checksum:X8}"
            };
            renderMaterial.NativeMetadata.Add(new XbxMaterialRenderMetadata(
                material.Checksum,
                material.NameChecksum,
                material.AlphaCutoff,
                material.Sorted,
                material.DrawOrder,
                material.ZBias,
                firstTexture));
            document.Materials.Add(renderMaterial);
        }

        ModelDocumentGeometryAdapter.PopulateXbxScene(document, scene, textureProvider, request.WorldzoneScale);
        return document;
    }

    private static ModelDocument ParseRwDff(MeshImportRequest request)
    {
        var clump = RwDffFile.Parse(request.Source.ReadBytes());
        var textureProvider = BuildRwTxdTextureProvider(
            request.Source,
            request.FileName,
            request.TexturePath);
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.RenderWareDff,
            new RenderWareDffNativeSource(clump, textureProvider));
        ModelDocumentGeometryAdapter.PopulateRwDff(document, clump, textureProvider);

        if (request.SkaAnimations is { Count: > 0 } rwAnimations && document.Skeletons.Count > 0)
        {
            var skin = clump.Atomics
                .Select(static a => a.SkinData)
                .FirstOrDefault(static s => s != null);
            var boneMap = ModelDocumentGeometryAdapter.BuildRwDffBoneIndexMap(skin);
            ModelDocumentGeometryAdapter.PopulateSkaAnimations(
                document, skeletonIndex: 0, rwAnimations, SkaCompositionMode.BindComposed, boneMap);
        }

        return document;
    }

    private static ModelDocument ParseRwBsp(MeshImportRequest request)
    {
        var world = RwBspFile.Parse(request.Source.ReadBytes());
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.RenderWareBsp,
            new RenderWareBspNativeSource(
                world,
                BuildRwTxdTextureProvider(request.Source, request.FileName, request.TexturePath)));

        foreach (var material in world.Materials)
        {
            var renderMaterial = new RenderMaterial
            {
                Name = material.TextureName ?? "rw_material"
            };
            renderMaterial.NativeMetadata.Add(new RwGsAlphaRenderMetadata(
                material.GsAlpha,
                material.GsAlphaFix,
                material.IsAdditive,
                material.IsSubtractive,
                material.IsBlend,
                material.TextureName));
            document.Materials.Add(renderMaterial);
        }

        ModelDocumentGeometryAdapter.PopulateRwBsp(
            document,
            world,
            ((RenderWareBspNativeSource)document.NativeSource!).TextureProvider);
        return document;
    }

    private static Dictionary<string, byte[]>? LoadDdxCompanion(
        AssetSource source,
        string stem,
        string? explicitPath = null)
    {
        var ddxPath = ResolveExplicitPath(explicitPath, stem, [".ddx"], []);
        if (ddxPath != null)
            return DdxArchive.ReadAllEntries(ddxPath);

        var ddxBytes = source.TryReadCompanion(stem + ".ddx");
        return ddxBytes != null ? DdxArchive.ReadAllEntries(ddxBytes) : null;
    }

    private static List<LitLight>? LoadLitCompanion(AssetSource source, string stem)
    {
        var litBytes = source.TryReadCompanion(stem + ".lit");
        if (litBytes == null) return null;
        try
        {
            return LitFile.Parse(litBytes);
        }
        catch
        {
            return null;
        }
    }

    private static MeshNamedTextureResolver? BuildRwTxdTextureProvider(
        AssetSource source,
        string fileName,
        string? explicitTexturePath = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var texBytes = ReadTextureCompanion(source, stem, RwTexExtensions, RwTexSubdirs, explicitTexturePath);
        if (texBytes == null) return null;
        var txdResult = RwTxdFile.Parse(texBytes);
        if (!txdResult.Success) return null;

        var lookup = new Dictionary<string, Ps2Texture>(StringComparer.OrdinalIgnoreCase);
        foreach (var tex in txdResult.Textures)
            if (tex.Pixels != null && tex.Name != null)
                lookup.TryAdd(tex.Name, tex);

        return textureName =>
        {
            if (!lookup.TryGetValue(textureName, out var tex))
            {
                var extIdx = textureName.LastIndexOf('.');
                if (extIdx <= 0 || !lookup.TryGetValue(textureName[..extIdx], out tex))
                    return null;
            }

            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels!);
        };
    }

    private static MeshChecksumTextureResolver? BuildXbxSceneTextureProvider(
        AssetSource source,
        string stem,
        string? explicitTexturePath = null)
    {
        var texBytes = ReadTextureCompanion(source, stem, XbxTexExtensions, XbxTexSubdirs, explicitTexturePath);
        if (texBytes == null) return null;

        var texResult = XbxTexFile.Parse(texBytes);
        if (!texResult.Success)
            texResult = ThawTexFile.Parse(texBytes);
        if (!texResult.Success) return null;

        var cache = new Dictionary<uint, Ps2Texture>();
        foreach (var tex in texResult.Textures)
            if (tex.Pixels != null)
                cache.TryAdd(tex.Checksum, tex);

        return checksum =>
        {
            if (!cache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                return null;
            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
        };
    }

    private static MeshChecksumTextureResolver BuildPsxTextureProvider(
        AssetSource source,
        string fileName,
        byte[] psxData)
    {
        var meshLabel = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);

        byte[]? libraryBytes = null;
        var libraryLabel = "";
        if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
        {
            var libraryName = stem[..^2] + "_l.psx";
            libraryBytes = source.TryReadCompanion(libraryName);
            libraryLabel = libraryName;
        }

        return hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(psxData, hash, meshLabel);
            if (result == null && libraryBytes != null)
                result = PsxLibrary.ExtractTextureByHash(libraryBytes, hash, libraryLabel);
            if (result == null)
                return null;
            var (rgba, width, height) = result.Value;
            return ImageWriter.WritePngToMemory(width, height, rgba);
        };
    }

    private static MeshChecksumTextureResolver? BuildPs2TextureProvider(byte[]? textureBytes)
    {
        if (textureBytes == null) return null;

        var texResult = Ps2TexFile.Parse(textureBytes);
        if (!texResult.Success)
            texResult = ThawSceneTexFile.Parse(textureBytes);
        if (!texResult.Success)
            return null;

        var cache = new Dictionary<uint, Ps2Texture>();
        foreach (var tex in texResult.Textures)
            if (tex.Pixels != null)
                cache.TryAdd(tex.Checksum, tex);

        return checksum =>
        {
            if (!cache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                return null;
            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
        };
    }

    private static Ps2Skeleton? TryLoadPs2Skeleton(
        AssetSource source,
        string stem,
        Ps2SceneSubFormat subFormat,
        string? explicitSkeletonPath = null)
    {
        var explicitPath = ResolveExplicitPath(
            explicitSkeletonPath,
            stem,
            [".ske.ps2", ".ske"],
            ["SKE", "Skeletons"]);
        if (explicitPath != null)
        {
            try
            {
                return explicitPath.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                    ? Ps2SkeletonFile.Parse(explicitPath)
                    : SkeletonFile.Parse(explicitPath);
            }
            catch
            {
                /* fall through to automatic discovery */
            }
        }

        var ps2Bytes = source.TryReadCompanion(stem + ".ske.ps2");
        if (ps2Bytes != null)
        {
            try
            {
                return Ps2SkeletonFile.Parse(ps2Bytes);
            }
            catch
            {
                /* fall through */
            }
        }

        var skeBytes = source.TryReadCompanion(stem + ".ske");
        if (skeBytes != null)
        {
            try
            {
                return SkeletonFile.Parse(skeBytes);
            }
            catch
            {
                /* fall through */
            }
        }

        if (subFormat == Ps2SceneSubFormat.ThawSkin && source.FileSystemPath != null)
        {
            var skeletonPath = ThawSkeletonDiscovery.FindSkeletonPath(
                source.FileSystemPath, stem, true);
            if (skeletonPath != null)
            {
                try
                {
                    return skeletonPath.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                        ? Ps2SkeletonFile.Parse(skeletonPath)
                        : SkeletonFile.Parse(skeletonPath);
                }
                catch
                {
                    /* proceed without skeleton */
                }
            }
        }

        if (subFormat == Ps2SceneSubFormat.ThawSkin && source is ArchiveAssetSource archiveSource)
        {
            var archiveResult = ThawSkeletonDiscovery.FindInArchive(
                archiveSource.Backend.Entries, archiveSource.Backend, stem, true);
            if (archiveResult is { } result)
            {
                try
                {
                    return result.EntryName.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                        ? Ps2SkeletonFile.Parse(result.Bytes)
                        : SkeletonFile.Parse(result.Bytes);
                }
                catch
                {
                    /* proceed without skeleton */
                }
            }
        }

        return null;
    }

    private static byte[]? ReadTextureCompanion(
        AssetSource source,
        string stem,
        string[] extensions,
        string[] subdirs,
        string? explicitPath = null)
    {
        var path = ResolveExplicitPath(explicitPath, stem, extensions, subdirs);
        if (path != null)
            return File.ReadAllBytes(path);

        return source.TryReadCompanion(stem, extensions, subdirs);
    }

    private static string? ResolveCompanionPath(
        AssetSource source,
        string stem,
        string extension,
        string? explicitPath)
    {
        var path = ResolveExplicitPath(explicitPath, stem, [extension], []);
        return path ?? source.TryResolveCompanionPath(stem + extension);
    }

    private static string? ResolveExplicitPath(
        string? explicitPath,
        string stem,
        string[] extensions,
        string[] subdirs)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
            return null;

        if (File.Exists(explicitPath))
            return explicitPath;

        if (!Directory.Exists(explicitPath))
            return null;

        return CompanionSearch.FindCompanion(explicitPath, stem, extensions, subdirs);
    }
}
