using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Animation;
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
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class MeshModelParser : IModelParser
{
    private static readonly string[] Ps2TexExtensions = [".tex.ps2", ".tex", ".img.ps2", ".stex", ".img"];
    private static readonly string[] Ps2TexSubdirs = ["TEX", "Textures", "IMG"];
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
        CollisionGeometryWriter.PopulateCollision(document, scene);
        return document;
    }

    private static ModelDocument ParseDdm(MeshImportRequest request)
    {
        var ddm = DdmFile.Parse(request.Source.ReadBytes());
        var ddxTextures = MeshCompanionResolver.LoadDdxCompanion(request.Source, request.OutputStem, request.DdxPath);
        var lights = MeshCompanionResolver.LoadLitCompanion(request.Source, request.OutputStem);
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

        DdmGeometryWriter.PopulateDdm(document, ddm, ddxTextures, textureDirs);
        return document;
    }

    private static ModelDocument ParsePlacedDdm(MeshImportRequest request)
    {
        var ddmPath = request.Source.FileSystemPath;
        if (ddmPath == null)
            return ParseDdm(request);

        var companionPsx = MeshCompanionResolver.ResolveCompanionPath(
            request.Source,
            request.OutputStem,
            ".psx",
            request.PsxPath);
        if (companionPsx == null)
            return ParseDdm(request);

        var objectsDdm = request.Source.TryResolveCompanionPath(request.OutputStem + "_o.ddm");
        var objectsPsx = objectsDdm != null
            ? MeshCompanionResolver.ResolveCompanionPath(request.Source, request.OutputStem + "_o", ".psx", request.PsxPath)
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
        var ddxTextures = MeshCompanionResolver.LoadDdxCompanion(request.Source, request.OutputStem, request.DdxPath);
        var textureDirs = MeshTextureHelper.BuildTextureSearchPaths(request.DdmTexturePath, request.OutputStem);
        DdmGeometryWriter.PopulateDdmPlacedLevel(
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
        var psxFile = PsxMeshFile.Parse(
                          psxData,
                          bakeColourPulses: !PsxMeshSemantics.IsLevelObjectBankName(request.FileName))
                      ?? throw new InvalidOperationException("No mesh data");

        var textureProvider = MeshCompanionResolver.BuildPsxTextureProvider(request.Source, request.FileName, psxData);
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

        // v1 DirectMatrix clips render flat-absolute per part with NO
        // hierarchy composition (decomp RenderSuperItem 0x800968D8):
        // worldVert = R_rec·v_local + T_rec, both read straight from the
        // record. A HIER character (mullen, CARNAGE) whose driving anim is v1
        // must therefore bind its mesh to a FLAT skeleton — a parented one
        // re-composes each part through its ancestors and tears the stitch
        // seams (measured: mullen frame-1 seam 27→0.4, CARNAGE 26→2 once flat).
        // Flat supers (bruce/hawk) are already flat via all-root object tables,
        // so this only changes HIER+v1 characters; v2-compressed clips keep the
        // parented skeleton (they chain translations through the hierarchy).
        var forceFlatSkeleton = request.PsxFlatSkeleton || DrivingAnimationsAreV1Absolute(request);
        var visibility = PsxVisibilityResolver.Resolve(
            request.Source,
            request.FileName,
            psxFile,
            request.VisibilityOverrides);
        document.VisibilityGroups.AddRange(visibility.Groups);
        var reconstructSplineAppendages = request.PsxAnimationOptions != null
            && (request.PsxAnimationClips is { Count: > 0 }
                || request.PsxDecodedAnimations is { Count: > 0 });
        PsxMeshFile? splineClawFile = null;
        MeshChecksumTextureResolver? splineClawTextureProvider = null;
        if (reconstructSplineAppendages
            && PsxSplineAppendageGeometry.FindControllerChains(psxFile).Count == 4)
        {
            try
            {
                // Retail sibling claw.psx first, then the claw mesh shipped
                // inside a sibling level-object bank (the February prototypes
                // carry it in l8a4_o.psx instead of a standalone file).
                var claw = PsxSplineClawLocator.Locate(request.Source);
                if (claw != null)
                {
                    splineClawFile = claw.File;
                    splineClawTextureProvider = claw.TextureProvider;
                }
            }
            catch (Exception ex)
            {
                // The controller tubes remain useful when an archive lacks or
                // truncates the optional runtime claw model.
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to resolve optional PSX spline claw companion: {ex.Message}");
            }
        }

        var geometryContext = new PsxGeometryWriter.PsxGeometryWriterContext();
        PsxGeometryWriter.PopulatePsx(
            document, psxFile, textureProvider, pshFile,
            forceFlatSkeleton, request.PsxFlatBoneIndices, splineClawFile,
            splineClawTextureProvider, visibility.HiddenObjectIndices,
            reconstructSplineAppendages, context: geometryContext);

        if (request.IncludeLevelObjects
            && !PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile))
            PopulatePsxLevelObjectCompanion(
                document, request, geometryContext, psxFile.TranslationDivisor);

        if (request.PsxAnimationOptions is { } animationOptions
            && document.Skeletons.Count > 0)
        {
            var clips = request.PsxAnimationClips;
            if (clips is { Count: > 0 })
            {
                PsxAnimationChannelWriter.PopulatePsxAnimationClips(
                    document, psxFile, skeletonIndex: 0, clips, animationOptions);
                if (reconstructSplineAppendages)
                {
                    PsxSplineAppendageGeometry.ApplyGeneratedFrameRotations(
                        document, skeletonIndex: 0,
                        PsxSplineAppendageGeometry.FindControllerChains(psxFile),
                        clips);
                }
            }
            else if (request.PsxDecodedAnimations is { Count: > 0 } animations)
            {
                PsxAnimationChannelWriter.PopulatePsxAnimations(
                    document, psxFile, skeletonIndex: 0, animations, animationOptions);
                if (reconstructSplineAppendages)
                {
                    var clipsForFrames = animations
                        .Select(static entry => new PsxAnimationClip(entry.Name, entry.Animation))
                        .ToArray();
                    PsxSplineAppendageGeometry.ApplyGeneratedFrameRotations(
                        document, skeletonIndex: 0,
                        PsxSplineAppendageGeometry.FindControllerChains(psxFile),
                        clipsForFrames);
                }
            }
        }

        return document;
    }

    /// <summary>
    ///     Spider-Man levels attach <c>*_g.psx</c> as environment geometry and
    ///     add two placement layers, both drawn from the sibling
    ///     <c>items.psx</c>/<c>*_o.psx</c> banks:
    ///     <list type="bullet">
    ///       <item>the <c>*_o.psx</c> model bank, whose object table is itself a
    ///       placed layer (stored positions are authored world instances — the
    ///       DDM layout convention), with sibling TRG PLATFORM/MANIPOB nodes
    ///       overlaying scripted re-instances; bank meshes shared with
    ///       items.psx render from the items copy;</item>
    ///       <item>TRG POWERUP nodes, rendered as items.psx pickups keyed by
    ///       <c>pickupType</c> (<see cref="PsxPowerupPlacementResolver" />).</item>
    ///     </list>
    ///     Both merge into ONE items geometry pass. The POWERUP layer works even
    ///     when no <c>*_o.psx</c> companion exists.
    /// </summary>
    private static void PopulatePsxLevelObjectCompanion(
        ModelDocument document,
        MeshImportRequest request,
        PsxGeometryWriter.PsxGeometryWriterContext geometryContext,
        float geometryTranslationDivisor)
    {
        if (!MeshCompanionResolver.TryResolvePsxLevelCompanions(
                request.Source, request.FileName, out var companions))
            return;

        try
        {
            // Parse the level's TRG and items.psx once; both layers consume them.
            var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(
                request.Source, companions.LevelStem);
            var items = PsxItemsBankSubstitution.TryLoadItems(request.Source);

            // items-object placements accumulated from the POWERUP layer and the
            // bank substitution, emitted as a single items geometry pass.
            var itemsPlacements = new Dictionary<int, List<PsxLevelObjectPlacement>>();

            // POWERUP layer first: it is authoritative for pickups, so the bank
            // layer drops any bank object whose mesh a POWERUP node already places.
            var suppressHashes = PsxPowerupPlacementResolver.EmptyHashSet;
            if (items != null
                && PsxPowerupPlacementResolver.Resolve(trg, items.File, geometryTranslationDivisor)
                    is { } powerupPlacements)
            {
                MergeItemsPlacements(itemsPlacements, powerupPlacements);
                suppressHashes = PsxPowerupPlacementResolver.PlacedModelHashes(
                    items.File, powerupPlacements);
            }

            PopulatePsxBankLayer(
                document, request, geometryContext, companions,
                trg, items, itemsPlacements, suppressHashes);

            if (items != null && itemsPlacements.Count > 0)
            {
                PsxGeometryWriter.PopulatePsx(
                    document,
                    items.File,
                    items.TextureProvider,
                    nodeNamePrefix: "items",
                    context: geometryContext,
                    objectPlacements: itemsPlacements.ToDictionary(
                        static pair => pair.Key,
                        static pair => (IReadOnlyList<PsxLevelObjectPlacement>)pair.Value));
            }
        }
        catch (Exception ex)
        {
            // Both layers are optional. A malformed or unrelated sibling must
            // not prevent the selected geometry layer from opening.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to parse optional PSX level-object companion: {ex.Message}");
        }
    }

    /// <summary>
    ///     Places the <c>*_o.psx</c> model bank when it exists: bank objects at
    ///     their authored positions + TRG platform overlay, with items-shared
    ///     meshes redirected onto <paramref name="itemsPlacements" /> and the
    ///     remaining bank objects emitted directly.
    /// </summary>
    private static void PopulatePsxBankLayer(
        ModelDocument document,
        MeshImportRequest request,
        PsxGeometryWriter.PsxGeometryWriterContext geometryContext,
        MeshCompanionResolver.PsxLevelCompanions companions,
        Trg.TrgFile? trg,
        PsxItemsBankSubstitution.LoadedItems? items,
        Dictionary<int, List<PsxLevelObjectPlacement>> itemsPlacements,
        IReadOnlySet<uint> suppressHashes)
    {
        // The *_o.psx bank is optional and independent of the POWERUP layer: a
        // missing, malformed, or unreadable bank must not prevent the items
        // (pickup) geometry from being emitted, so this layer swallows its own
        // failures rather than aborting the caller.
        try
        {
            var companionName = companions.BankCompanionName;
            if (request.Source.TryReadCompanion(companionName) is not { } companionBytes)
                return;

            var bank = PsxMeshFile.Parse(companionBytes, bakeColourPulses: false);
            if (bank == null || PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(bank))
                return;

            var bankPlacements = PsxLevelObjectPlacementResolver.Resolve(
                trg, bank, companions.ApplyTriggerOverlay);
            if (bankPlacements.Count == 0)
                return;

            var remainingBank = bankPlacements;
            if (items != null
                && PsxItemsBankSubstitution.Split(items.File, bank, bankPlacements, suppressHashes)
                    is { } split)
            {
                MergeItemsPlacements(itemsPlacements, split.ItemsPlacements);
                remainingBank = split.RemainingBankPlacements;
            }

            if (remainingBank.Count > 0)
            {
                PsxGeometryWriter.PopulatePsx(
                    document,
                    bank,
                    MeshCompanionResolver.BuildPsxTextureProvider(
                        request.Source, companionName, companionBytes),
                    nodeNamePrefix: "objects",
                    context: geometryContext,
                    objectPlacements: remainingBank);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to place optional PSX level-object bank: {ex.Message}");
        }
    }

    private static void MergeItemsPlacements(
        Dictionary<int, List<PsxLevelObjectPlacement>> accumulator,
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> additions)
    {
        foreach (var (objectIndex, placements) in additions)
        {
            if (!accumulator.TryGetValue(objectIndex, out var list))
            {
                list = [];
                accumulator[objectIndex] = list;
            }

            list.AddRange(placements);
        }
    }

    /// <summary>
    ///     True when the character will be animated and every driving clip is a
    ///     v1 direct-matrix animation (absolute per-part world transforms). Such
    ///     clips need a flat skeleton; a mixed or v2 set keeps the parented one.
    /// </summary>
    private static bool DrivingAnimationsAreV1Absolute(MeshImportRequest request)
    {
        if (request.PsxAnimationClips is { Count: > 0 } clips)
            return clips.All(static clip => clip.Animation.AbsoluteWorldTranslations);

        if (request.PsxDecodedAnimations is { Count: > 0 } animations)
            return animations.All(static entry => entry.Animation.AbsoluteWorldTranslations);

        return false;
    }

    private static ModelDocument ParsePs2Scene(MeshImportRequest request)
    {
        var data = request.Source.ReadBytes();
        var companionTexData = MeshCompanionResolver.ReadTextureCompanion(
            request.Source,
            request.OutputStem,
            Ps2TexExtensions,
            Ps2TexSubdirs,
            request.TexturePath,
            searchBuildTree: true);
        var textureProvider = MeshCompanionResolver.BuildPs2TextureProvider(companionTexData);
        var tex0Resolver = BuildPs2GeomTex0Resolver(companionTexData);

        if (request.Ps2SubFormat == Ps2SceneSubFormat.PakMdl)
        {
            var geomScene = Ps2GeomFile.ParsePakMdl(data);
            return BuildPs2GeomDocument(request.OutputStem, geomScene, textureProvider, tex0Resolver);
        }

        var scene = request.Ps2SubFormat switch
        {
            Ps2SceneSubFormat.ThawSkin => ThawPs2SkinFile.Parse(data, companionTexData),
            Ps2SceneSubFormat.PakSkin => ThawPs2SkinFile.ParsePakSkin(data, tex0Resolver),
            _ => Ps2SceneFile.Parse(data)
        };

        var skeleton = request.PreparedSkeleton ?? MeshCompanionResolver.TryLoadPs2Skeleton(
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

        Ps2SceneGeometryWriter.PopulatePs2Scene(document, scene, textureProvider, skeleton);

        if (request.SkaAnimations is { Count: > 0 } ps2Animations && document.Skeletons.Count > 0)
        {
            SkaAnimationWriter.PopulateSkaAnimations(
                document, skeletonIndex: 0, ps2Animations);
        }

        return document;
    }

    private static ModelDocument ParsePs2Geom(MeshImportRequest request)
    {
        var scene = Ps2GeomFile.Parse(request.Source.ReadBytes());
        var companionTexData = MeshCompanionResolver.ReadTextureCompanion(
            request.Source,
            request.OutputStem,
            Ps2TexExtensions,
            Ps2TexSubdirs,
            request.TexturePath,
            searchBuildTree: true);
        var textureProvider = MeshCompanionResolver.BuildPs2TextureProvider(companionTexData);
        var tex0Resolver = BuildPs2GeomTex0Resolver(companionTexData);
        return BuildPs2GeomDocument(request.OutputStem, scene, textureProvider, tex0Resolver);
    }

    /// <summary>
    ///     THPS4 GEOM leaves carry CGeomNode.texture_checksum = 0; their textures are
    ///     addressed by the GS TEX0 register (TBP/CBP VRAM pointers) embedded in the DMA
    ///     chain. Simulate the engine's LoadTextureGroup VRAM allocation over the
    ///     companion TEX dictionary to map (GroupChecksum, TBP, CBP) back to texture
    ///     checksums. THUG/THUG2 leaves have non-zero checksums and never consult this.
    /// </summary>
    private static Ps2Tex0ChecksumResolver? BuildPs2GeomTex0Resolver(byte[]? companionTexData)
    {
        if (companionTexData == null)
            return null;

        var vramMap = Ps2VramAllocator.BuildMapping(companionTexData);
        if (vramMap.Count == 0)
        {
            var source = new ZoneTextureCatalog.ZoneTexSource(
                "archive_companion.stex", companionTexData, IsMain: true);
            return ZoneTextureCatalog.TryBuild([source], out var catalog) && catalog != null
                ? catalog.CreateTex0ChecksumResolver(source.Label)
                : null;
        }

        // TBP+CBP fallback for leaves whose group checksum diverges from the TEX
        // group (only unambiguous addresses resolve, mirroring the diagnostic sim).
        var byTbpCbp = new Dictionary<(uint Tbp, uint Cbp), uint>();
        var ambiguous = new HashSet<(uint Tbp, uint Cbp)>();
        foreach (var ((_, tbp, cbp), checksum) in vramMap)
        {
            if (ambiguous.Contains((tbp, cbp)))
                continue;
            if (byTbpCbp.TryGetValue((tbp, cbp), out var existing))
            {
                if (existing != checksum)
                {
                    byTbpCbp.Remove((tbp, cbp));
                    ambiguous.Add((tbp, cbp));
                }

                continue;
            }

            byTbpCbp[(tbp, cbp)] = checksum;
        }

        return (dmaTex0, groupChecksum) =>
        {
            var key = Ps2VramAllocator.DecodeTex0Key(dmaTex0, groupChecksum);
            if (vramMap.TryGetValue(key, out var checksum))
                return checksum;
            return byTbpCbp.TryGetValue((key.Tbp, key.Cbp), out var fallback) ? fallback : 0u;
        };
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

        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, textureProvider, tex0Resolver);
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

        if (texPath == null && request.Source is ArchiveAssetSource archiveSource)
        {
            // Worldzone PAK nested inside a parent archive (e.g. DATAP.WAD): pool the
            // entry itself plus its same-directory sibling PAKs for texture lookup,
            // mirroring the sibling-file scan used for on-disk worldzone PAKs.
            var byteSources = ZoneTextureProviderBuilder.GetTexByteSources(
                archiveSource.Backend, archiveSource.Entry);
            if (ZoneTextureCatalog.TryBuild(byteSources, out textureCatalog) && textureCatalog != null)
                textureSourceHint = archiveSource.Entry.FullName;
        }
        else
        {
            ZoneTextureCatalog.TryBuild(texPath, out textureCatalog);
        }

        if (textureCatalog != null)
        {
            textureProvider = textureCatalog.CreateTextureResolver();
            texaTextureProvider = textureCatalog.CreateTexaAwareTextureResolver();
            tex0Resolver = textureCatalog.CreateTex0ChecksumResolver(textureSourceHint);
        }

        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
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
        var isNgc = NgcSceneFile.IsNgcScene(data);
        var scene = true switch
        {
            _ when isNgc => NgcSceneFile.Parse(data),
            _ when ThawSceneFile.IsThawScene(data) => ThawSceneFile.Parse(data),
            _ => XbxSceneFile.Parse(data)
        };
        var textureProvider = isNgc
            ? MeshCompanionResolver.BuildNgcSceneTextureProvider(request.Source, request.OutputStem, request.TexturePath)
            : MeshCompanionResolver.BuildXbxSceneTextureProvider(request.Source, request.OutputStem, request.TexturePath);
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

        XbxGeometryWriter.PopulateXbxScene(document, scene, textureProvider, request.WorldzoneScale);
        return document;
    }

    private static ModelDocument ParseRwDff(MeshImportRequest request)
    {
        var clump = RwDffFile.Parse(request.Source.ReadBytes());
        var textureProvider = MeshCompanionResolver.BuildRwTxdTextureProvider(
            request.Source,
            request.FileName,
            request.TexturePath);
        var document = ModelDocument.CreateNative(
            request.OutputStem,
            ModelSourceKind.RenderWareDff,
            new RenderWareDffNativeSource(clump, textureProvider));
        RwGeometryWriter.PopulateRwDff(document, clump, textureProvider);

        if (request.SkaAnimations is { Count: > 0 } rwAnimations && document.Skeletons.Count > 0)
        {
            var skin = clump.Atomics
                .Select(static a => a.SkinData)
                .FirstOrDefault(static s => s != null);
            var boneMap = SkaAnimationWriter.BuildRwDffBoneIndexMap(skin);
            SkaAnimationWriter.PopulateSkaAnimations(
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
                MeshCompanionResolver.BuildRwTxdTextureProvider(request.Source, request.FileName, request.TexturePath)));

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

        RwBspGeometryWriter.PopulateRwBsp(
            document,
            world,
            ((RenderWareBspNativeSource)document.NativeSource!).TextureProvider);
        return document;
    }
}
