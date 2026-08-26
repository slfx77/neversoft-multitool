using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Trg;

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
            ModelSourceKind.N64Model => ParseN64Model(request),
            ModelSourceKind.GbaLevel => ParseGbaLevel(request),
            ModelSourceKind.GbaModel => ParseGbaModel(request),
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
            ? MeshCompanionResolver.ResolveCompanionPath(request.Source, request.OutputStem + "_o", ".psx",
                request.PsxPath)
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

    /// <summary>
    ///     A model bundle carved from an N64 ROM. The shell supplies the
    ///     skeleton and naming, the render bank supplies the geometry, and the
    ///     ROM's shared light rig is read from boot.bin so lit surfaces can be
    ///     shaded the way the console shades them.
    /// </summary>
    private static ModelDocument ParseN64Model(MeshImportRequest request)
    {
        var shellData = request.Source.ReadBytes();
        var shell = PsxN64ShellFile.Parse(shellData)
                    ?? throw new InvalidOperationException(
                        "Not a readable N64 model shell (empty bundle slot or unrecognised container)");

        var native = new N64.N64ModelNativeSource(
            shellData,
            shell,
            N64.N64ModelCompanions.TryReadRenderBank(request.Source),
            N64.N64ModelCompanions.TryReadRenderBankId(request.Source),
            N64.N64ModelCompanions.BuildTextureProvider(request.Source),
            N64.N64ModelCompanions.TryReadLightRig(request.Source),
            // Bound here rather than in the writer so the GUI and the CLI share
            // one path — the writer has no asset source of its own. Deferred so
            // a static export never runs the scan.
            slots => Animation.N64TrickTableLocator.ForBundle(request.Source, slots));

        var document = ModelDocument.CreateNative(request.OutputStem, ModelSourceKind.N64Model, native);
        N64.N64ModelWriter.Populate(
            document,
            native,
            request.N64AnimationIndices,
            request.IncludeAllN64Animations,
            request.N64AnimationOneShot);
        return document;
    }

    /// <summary>
    ///     A carved GBA level: the 0x15C table record plus its <c>rom.gbarom</c>
    ///     companion (the record's pointers, art pools, and the ROM-executed collision
    ///     height functions all dereference into the ROM). The record's ROM offset is
    ///     recovered by content — its bytes occur exactly once at the level table.
    /// </summary>
    private static ModelDocument ParseGbaLevel(MeshImportRequest request)
    {
        var record = request.Source.ReadBytes();
        var rom = request.Source.TryReadCompanion(Gba.GbaLevelCarver.RomEntryName)
                  ?? throw new InvalidOperationException(
                      $"Missing '{Gba.GbaLevelCarver.RomEntryName}' companion — carved GBA levels " +
                      "must stay beside the ROM they were carved from");
        var trueRecord = Gba.GbaLevelCarver.FindRecordOffset(rom, record);
        if (trueRecord < 0)
            throw new InvalidOperationException("The level record does not belong to the companion ROM");

        var levelName = request.OutputStem;
        var location = "";
        foreach (var carved in Gba.GbaLevelCarver.ListLevels(rom))
        {
            if (!carved.EntryName.EndsWith(Path.GetFileName(request.FileName), StringComparison.OrdinalIgnoreCase))
                continue;
            levelName = carved.Name;
            location = carved.Location;
            break;
        }

        var native = new GbaLevelNativeSource(record, rom, trueRecord, levelName, location);
        var document = ModelDocument.CreateNative(request.OutputStem, ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(document, native);
        return document;
    }

    /// <summary>
    ///     A carved GBA character: the roster record plus the ROM companion. The
    ///     character index is recovered by content — the record occurs exactly once,
    ///     at the character table.
    /// </summary>
    private static ModelDocument ParseGbaModel(MeshImportRequest request)
    {
        var record = request.Source.ReadBytes();
        var rom = request.Source.TryReadCompanion(Gba.GbaLevelCarver.RomEntryName)
                  ?? throw new InvalidOperationException(
                      $"Missing '{Gba.GbaLevelCarver.RomEntryName}' companion — carved GBA characters " +
                      "must stay beside the ROM they were carved from");
        var model = Gba.GbaSkaterModel.TryLocate(rom)
                    ?? throw new InvalidOperationException("The companion ROM does not carry the skater model");

        var at = rom.AsSpan().IndexOf(record);
        if (at < model.CharacterTableOffset || (at - model.CharacterTableOffset) % 0x4C != 0
            || (at - model.CharacterTableOffset) / 0x4C >= model.CharacterCount)
            throw new InvalidOperationException("The character record does not belong to the companion ROM");
        var characterIndex = (at - model.CharacterTableOffset) / 0x4C;
        var name = Gba.GbaSkaterModel.TryGetCharacterName(rom, model, characterIndex) ?? request.OutputStem;

        var native = new GbaModelNativeSource(record, rom, characterIndex, name, Outfit: 0);
        var document = ModelDocument.CreateNative(request.OutputStem, ModelSourceKind.GbaModel, native);

        // Fail-closed: an animated request that selects nothing valid falls back
        // to the plain static export, byte-identical to a request with no
        // animation fields at all.
        var animationRequested = request.IncludeAllGbaAnimations
                                 || request.GbaAnimationIndices is { Count: > 0 };
        var exported = animationRequested
            ? GbaAnimatedModelWriter.TryPopulate(
                document, native, request.GbaAnimationIndices, request.IncludeAllGbaAnimations)
            : 0;
        if (exported == 0)
            GbaModelGeometryWriter.Populate(document, native);
        return document;
    }

    private static ModelDocument ParsePsx(MeshImportRequest request)
    {
        var psxData = request.Source.ReadBytes();
        // Banks bake pulses like every region (the engine ticks the obj
        // region every frame — see PsxSurfaceAnimationReader).
        var psxFile = PsxMeshFile.Parse(psxData)
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
        // Resolve this once from the model's complete embedded animation bank.
        // The one-chain signature needs all authored slots, not merely whichever
        // preview clip the user happened to select; the result is then shared by
        // geometry suppression, reconstruction, and generated frame rotations.
        var splineChains = PsxSplineAppendageGeometry.DiscoverControllerChains(
            psxFile, request.Source, psxData);
        var reconstructSplineAppendages = request.PsxAnimationOptions != null
                                          && (request.PsxAnimationClips is { Count: > 0 }
                                              || request.PsxDecodedAnimations is { Count: > 0 });
        PsxSplineClawLocator.ResolvedClaw? splineClaw = null;
        if (reconstructSplineAppendages && splineChains.Count == 4)
        {
            try
            {
                // Discover the unique sibling tip kit by content. Dedicated
                // payloads rank ahead of mapped or conservative legacy bank
                // candidates; an ambiguous scope deliberately has no tip.
                splineClaw = PsxSplineClawLocator.Locate(request.Source);
            }
            catch (Exception ex)
            {
                // The controller tubes remain useful when an archive lacks or
                // truncates the optional runtime claw model.
                Debug.WriteLine(
                    $"Unable to resolve optional PSX spline claw companion: {ex.Message}");
            }
        }

        var geometryContext = new PsxGeometryWriter.PsxGeometryWriterContext
        {
            EngineLight = PsxEngineLight.FromName(request.PsxLightPreset)
        };
        PsxGeometryWriter.PopulatePsx(
            document, psxFile, textureProvider, pshFile,
            forceFlatSkeleton, request.PsxFlatBoneIndices, splineClaw,
            splineChains, visibility.HiddenObjectIndices,
            reconstructSplineAppendages, context: geometryContext);

        if (request.IncludeLevelObjects
            && !PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile))
            PopulatePsxLevelObjectCompanion(
                document, request, geometryContext, psxFile,
                visibility.HiddenObjectIndices);

        if (request.PsxAnimationOptions is { } animationOptions
            && document.Skeletons.Count > 0)
        {
            var clips = request.PsxAnimationClips;
            if (clips is { Count: > 0 })
            {
                PsxAnimationChannelWriter.PopulatePsxAnimationClips(
                    document, psxFile, 0, clips, animationOptions);
                if (reconstructSplineAppendages)
                {
                    PsxSplineAppendageGeometry.ApplyGeneratedFrameRotations(
                        document, 0, splineChains, clips);
                }
            }
            else if (request.PsxDecodedAnimations is { Count: > 0 } animations)
            {
                PsxAnimationChannelWriter.PopulatePsxAnimations(
                    document, psxFile, 0, animations, animationOptions);
                if (reconstructSplineAppendages)
                {
                    var clipsForFrames = animations
                        .Select(static entry => new PsxAnimationClip(entry.Name, entry.Animation))
                        .ToArray();
                    PsxSplineAppendageGeometry.ApplyGeneratedFrameRotations(
                        document, 0, splineChains, clipsForFrames);
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
    ///         <item>
    ///             the <c>*_o.psx</c> model bank, whose object table is itself a
    ///             placed layer (stored positions are authored world instances — the
    ///             DDM layout convention), with sibling TRG PLATFORM/MANIPOB nodes
    ///             overlaying scripted re-instances; bank meshes shared with
    ///             items.psx render from the items copy;
    ///         </item>
    ///         <item>
    ///             TRG POWERUP nodes, rendered as items.psx pickups keyed by
    ///             <c>pickupType</c> (<see cref="PsxPowerupPlacementResolver" />).
    ///         </item>
    ///     </list>
    ///     Both merge into ONE items geometry pass. The POWERUP layer works even
    ///     when no <c>*_o.psx</c> companion exists.
    /// </summary>
    private static void PopulatePsxLevelObjectCompanion(
        ModelDocument document,
        MeshImportRequest request,
        PsxGeometryWriter.PsxGeometryWriterContext geometryContext,
        PsxMeshFile levelMesh,
        IReadOnlySet<int>? hiddenLevelObjectIndices)
    {
        var geometryTranslationDivisor = levelMesh.TranslationDivisor;
        if (!MeshCompanionResolver.TryResolvePsxLevelCompanions(
                request.Source, request.FileName, out var companions))
            return;

        try
        {
            // Parse the level's TRG and items.psx once; both layers consume them.
            var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(
                request.Source, companions.LevelStem);
            var items = PsxItemsBankSubstitution.TryLoadItems(request.Source);

            // The engine clears the framebuffer to the TRG's SetSkyColor every
            // frame (Db_UpdateSky) whether or not a sky dome exists, so record
            // the backdrop at document scope independent of the bank/dome path
            // below — a domeless region (skny_2's SkNY_O2 bank carries no
            // background object) still has its authored night-blue clear.
            if (PsxSkyDomeClassifier.FindSkyColor(trg) is { } backdropColor)
                document.NativeMetadata.Add(new PsxSkyBackdropMetadata(backdropColor));

            // Terrain query over the level's own render geometry (the surface the
            // engine ray-casts) so grounded POWERUP pickups reseat onto the floor.
            var terrain = PsxTerrainHeightField.BuildFromLevel(levelMesh);
            var pickupTerrain = terrain.IsEmpty ? null : terrain;

            // items-object placements accumulated from the POWERUP layer and the
            // bank substitution, emitted as a single items geometry pass.
            var itemsPlacements = new Dictionary<int, List<PsxLevelObjectPlacement>>();

            // POWERUP layer first: it is authoritative for pickups, so the bank
            // layer drops any bank object whose mesh a POWERUP node already places.
            var suppressHashes = PsxPowerupPlacementResolver.EmptyHashSet;
            if (items != null
                && PsxPowerupPlacementResolver.Resolve(trg, items.File, geometryTranslationDivisor, pickupTerrain)
                    is { } powerupPlacements)
            {
                MergeItemsPlacements(itemsPlacements, powerupPlacements);
                suppressHashes = PsxPowerupPlacementResolver.PlacedModelHashes(
                    items.File, powerupPlacements);
            }

            PopulatePsxBankLayer(
                document, request, geometryContext, companions,
                trg, items, itemsPlacements, suppressHashes, levelMesh,
                hiddenLevelObjectIndices);

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

            PopulatePsxPlacedTraffic(
                document,
                request,
                geometryContext,
                companions.LevelStem,
                trg,
                geometryTranslationDivisor);
        }
        catch (Exception ex)
        {
            // Both layers are optional. A malformed or unrelated sibling must
            // not prevent the selected geometry layer from opening.
            Debug.WriteLine(
                $"Unable to parse optional PSX level-object companion: {ex.Message}");
        }
    }

    /// <summary>
    ///     Emits independently-spooled traffic supers at their proven initial
    ///     TRG positions. Runtime-created traffic is intentionally absent from
    ///     the authored initial scene; those placements are exposed as a
    ///     default-disabled snapshot because their road motion, repeats, and
    ///     script timing are not reconstructed here.
    /// </summary>
    private static void PopulatePsxPlacedTraffic(
        ModelDocument document,
        MeshImportRequest request,
        PsxGeometryWriter.PsxGeometryWriterContext geometryContext,
        string levelStem,
        TrgFile? trg,
        float levelTranslationDivisor)
    {
        try
        {
            var resolved = PsxPlacedTrafficResolver.Resolve(
                request.Source, trg, levelTranslationDivisor);
            if (resolved.Count == 0)
                return;

            var assetHash = QbKey.QbKey.Hash(levelStem.ToUpperInvariant());
            var scripted = resolved
                .Where(static placement => !placement.InitiallyCreated)
                .ToArray();
            var scriptedEnabled = false;
            if (scripted.Length > 0)
            {
                var id = $"psx.scripted_traffic.{assetHash:X8}";
                scriptedEnabled = request.VisibilityOverrides?
                    .TryGetValue(id, out var selected) == true && selected;
                document.VisibilityGroups.Add(new ModelVisibilityGroup
                {
                    Id = id,
                    Label = "Possible scripted traffic snapshot",
                    DefaultEnabled = false,
                    IsEnabled = scriptedEnabled,
                    Source = ModelVisibilityGroupSource.TriggerCondition,
                    SourceReference =
                        "Script-reachable BADDY nodes "
                        + string.Join(", ", scripted
                            .Select(static placement => placement.TriggerNodeIndex)
                            .Distinct()
                            .Order())
                        + "; initial road positions only (no path motion, timing, or repeats)"
                });
            }

            var selectedPlacements = resolved
                .Where(placement => placement.InitiallyCreated || scriptedEnabled)
                .ToArray();
            if (selectedPlacements.Length == 0)
                return;

            var emittedTraffic = false;
            foreach (var sourceGroup in selectedPlacements.GroupBy(
                         static placement => placement.Source))
            {
                emittedTraffic |= TryPopulatePsxPlacedTrafficSource(
                    document,
                    request.Source,
                    geometryContext.EngineLight,
                    sourceGroup.Key,
                    sourceGroup.ToArray());
            }

            if (emittedTraffic)
                ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        }
        catch (Exception ex)
        {
            // Traffic is an optional runtime layer. Its TRG evidence, model,
            // texture, or animation must never prevent the selected level and
            // its ordinary object companions from opening.
            Debug.WriteLine(
                $"Unable to parse optional PSX placed traffic: {ex.Message}");
        }
    }

    internal static bool TryPopulatePsxPlacedTrafficSource(
        ModelDocument document,
        AssetSource levelSource,
        PsxEngineLight? engineLight,
        PsxPlacedTrafficSource source,
        IReadOnlyList<PsxPlacedTrafficPlacement> placements)
    {
        var snapshot = ModelDocumentAppendSnapshot.Capture(document);
        try
        {
            if (placements.Count == 0)
                return false;

            MeshChecksumTextureResolver? textureProvider = null;
            try
            {
                textureProvider = MeshCompanionResolver.BuildPsxTextureProvider(
                    levelSource, source.CompanionName, source.Bytes);
            }
            catch (Exception ex)
            {
                // Missing or malformed optional texture libraries should leave
                // usable untextured traffic geometry, not suppress the source.
                Debug.WriteLine(
                    $"Unable to resolve PSX traffic textures for "
                    + $"{source.CompanionName}: {ex.Message}");
            }

            var pshFile = TryLoadPsxTrafficHierarchy(levelSource, source);
            var sourceStem = Path.GetFileNameWithoutExtension(source.CompanionName);
            var modelAnimation = new ModelAnimation
            {
                Name = $"{sourceStem}_anim_0"
            };
            var clip = new PsxAnimationClip("anim_0", source.Animation);
            var animationOptions = new PsxAnimationOptions();

            for (var placementIndex = 0; placementIndex < placements.Count; placementIndex++)
            {
                var placement = placements[placementIndex];
                if (!IsFinite(placement.RootTransform))
                    throw new InvalidDataException(
                        $"Traffic BADDY {placement.TriggerNodeIndex} has a non-finite root transform.");

                var instanceName =
                    $"traffic_{sourceStem}_{placement.TriggerNodeIndex:D4}_{placementIndex:D3}";
                var skeletonIndex = document.Skeletons.Count;
                var meshStart = document.Meshes.Count;
                var nodeStart = document.Nodes.Count;
                PsxSkinnedGeometryWriter.PopulatePsxSkinned(
                    document,
                    source.MeshFile,
                    pshFile,
                    textureProvider,
                    // A v1 direct-matrix clip carries absolute per-part world
                    // transforms even when its model ships a HIER table. As in
                    // the primary character path, its bind must be flat or the
                    // glTF hierarchy re-composes and tears those parts apart.
                    flatSkeleton: !source.MeshFile.HasHierarchy
                                  || source.Animation.AbsoluteWorldTranslations,
                    flatBoneIndices: null,
                    splineClaw: null,
                    splineChains: null,
                    hiddenObjectIndices: null,
                    reconstructSplineAppendages: false,
                    engineLight: engineLight,
                    skeletonName: $"{instanceName}_skeleton",
                    rootTransform: placement.RootTransform,
                    boneNamePrefix: $"{instanceName}_",
                    combinedMeshName: $"{instanceName}_mesh");

                if (!IsValidTrafficInstance(
                        document, skeletonIndex, meshStart, nodeStart))
                {
                    throw new InvalidDataException(
                        $"Traffic BADDY {placement.TriggerNodeIndex} emitted no complete skinned geometry.");
                }

                if (!PsxAnimationChannelWriter.AppendPsxAnimationClipChannels(
                        modelAnimation,
                        document,
                        source.MeshFile,
                        skeletonIndex,
                        clip,
                        animationOptions))
                {
                    throw new InvalidDataException(
                        $"Traffic BADDY {placement.TriggerNodeIndex} emitted no animation channels.");
                }
            }

            document.Animations.Add(modelAnimation);
            return true;
        }
        catch (Exception ex)
        {
            snapshot.Restore(document);
            // Sources are independent (taxi, van, cable car, ...). Keep the
            // other traffic types usable when one optional payload is novel or
            // malformed.
            Debug.WriteLine(
                $"Unable to emit PSX traffic source {source.CompanionName}: {ex.Message}");
            return false;
        }
    }

    private static bool IsValidTrafficInstance(
        ModelDocument document,
        int skeletonIndex,
        int meshStart,
        int nodeStart)
    {
        if (document.Skeletons.Count != skeletonIndex + 1
            || document.Meshes.Count <= meshStart
            || document.Nodes.Count <= nodeStart)
        {
            return false;
        }

        var referencedMeshes = document.Nodes
            .Skip(nodeStart)
            .Where(static node => node.MeshIndex.HasValue)
            .Select(static node => node.MeshIndex!.Value)
            .ToHashSet();
        if (!Enumerable.Range(meshStart, document.Meshes.Count - meshStart)
                .All(referencedMeshes.Contains))
        {
            return false;
        }

        var primitives = document.Meshes
            .Skip(meshStart)
            .SelectMany(static mesh => mesh.Primitives)
            .ToArray();
        return primitives.Length > 0
               && primitives.Sum(static primitive => primitive.TriangleCount) > 0
               && primitives.All(primitive =>
                   primitive.Skin is { } skin
                   && skin.SkeletonIndex == skeletonIndex
                   && skin.Influences.Length == primitive.Vertices.Length);
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        return float.IsFinite(matrix.M11)
               && float.IsFinite(matrix.M12)
               && float.IsFinite(matrix.M13)
               && float.IsFinite(matrix.M14)
               && float.IsFinite(matrix.M21)
               && float.IsFinite(matrix.M22)
               && float.IsFinite(matrix.M23)
               && float.IsFinite(matrix.M24)
               && float.IsFinite(matrix.M31)
               && float.IsFinite(matrix.M32)
               && float.IsFinite(matrix.M33)
               && float.IsFinite(matrix.M34)
               && float.IsFinite(matrix.M41)
               && float.IsFinite(matrix.M42)
               && float.IsFinite(matrix.M43)
               && float.IsFinite(matrix.M44);
    }

    private static PshFile? TryLoadPsxTrafficHierarchy(
        AssetSource levelSource,
        PsxPlacedTrafficSource source)
    {
        if (!source.MeshFile.HasHierarchy)
            return null;

        try
        {
            var pshName = Path.GetFileNameWithoutExtension(source.CompanionName) + ".psh";
            return levelSource.TryReadCompanion(pshName) is { } bytes
                ? PshFile.Parse(bytes)
                : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Unable to parse optional PSX traffic hierarchy for "
                + $"{source.CompanionName}: {ex.Message}");
            return null;
        }
    }

    private readonly record struct ModelDocumentAppendSnapshot(
        int SceneCount,
        int[] SceneRootCounts,
        int NodeCount,
        int MeshCount,
        int MaterialCount,
        int TextureCount,
        int SkeletonCount,
        int AnimationCount,
        int NativeMetadataCount,
        int TriangleCount)
    {
        internal static ModelDocumentAppendSnapshot Capture(ModelDocument document)
        {
            return new ModelDocumentAppendSnapshot(
                document.Scenes.Count,
                document.Scenes.Select(static scene => scene.RootNodeIndices.Count).ToArray(),
                document.Nodes.Count,
                document.Meshes.Count,
                document.Materials.Count,
                document.Textures.Count,
                document.Skeletons.Count,
                document.Animations.Count,
                document.NativeMetadata.Count,
                document.TriangleCount);
        }

        internal void Restore(ModelDocument document)
        {
            var existingSceneCount = Math.Min(SceneCount, document.Scenes.Count);
            for (var i = 0; i < existingSceneCount; i++)
                Truncate(document.Scenes[i].RootNodeIndices, SceneRootCounts[i]);
            Truncate(document.Scenes, SceneCount);
            Truncate(document.Nodes, NodeCount);
            Truncate(document.Meshes, MeshCount);
            Truncate(document.Materials, MaterialCount);
            Truncate(document.Textures, TextureCount);
            Truncate(document.Skeletons, SkeletonCount);
            Truncate(document.Animations, AnimationCount);
            Truncate(document.NativeMetadata, NativeMetadataCount);
            document.TriangleCount = TriangleCount;
        }

        private static void Truncate<T>(List<T> values, int count)
        {
            if (values.Count > count)
                values.RemoveRange(count, values.Count - count);
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
        TrgFile? trg,
        PsxItemsBankSubstitution.LoadedItems? items,
        Dictionary<int, List<PsxLevelObjectPlacement>> itemsPlacements,
        IReadOnlySet<uint> suppressHashes,
        PsxMeshFile levelMesh,
        IReadOnlySet<int>? hiddenLevelObjectIndices)
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

            // Banks bake colour pulses like every other region: the engine's
            // M3d_RenderSetup ticks M3d_PreprocessPulsingColours for the obj
            // and items regions every frame with NO per-model gate — THPS2
            // proto jals at 0x80095740..70 and Spider-Man final at
            // 0x80076228..58 cover env, obj, and items regions alike. The
            // earlier raw-palette rule mis-attributed the l1a1 "?"'s in-game
            // dark blue to the bank copy — that colour is the ITEMS-region
            // copy's own staggered-blue pulse, and the bank duplicate never
            // reaches output (POWERUP suppression + items substitution).
            // Fire/star art serializes BLACK with the pulse as its only colour
            // source, so the raw palette rendered them black (user report).
            var bank = PsxMeshFile.Parse(companionBytes);
            if (bank == null || PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(bank))
                return;

            var resolved = PsxLevelObjectPlacementResolver.ResolveDetailed(
                trg, bank, companions.ApplyTriggerOverlay);
            if (resolved.Placements.Count == 0)
                return;

            // "What If?" easter-egg spawns (C_IF_WHAT_IF-guarded PLATFORM
            // nodes, e.g. l2a1's rooftop motorcycle) exist in-game only when
            // the mode is active — gate them behind an opt-in visibility
            // group. Everything the gate removes never reaches the items
            // substitution or the sky classifier.
            var assetHash = QbKey.QbKey.Hash(companions.LevelStem.ToUpperInvariant());
            var bankPlacements = PsxWhatIfContentGate.Apply(
                document, request.VisibilityOverrides, assetHash, resolved);
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
                // The engine renders backgrounds CAMERA-LOCKED (TRG 0xAB
                // BackgroundCreate; M3d_RenderBackground applies rotation only
                // with translation zeroed) — the sky's bank position is dead
                // data (sksf parks its dome 6,350 units below the level).
                // Anchor the static bake at the registering node (the camera's
                // start) or the level centroid; the in-app viewer re-locks it
                // to the camera per frame.
                var sky = PsxSkyDomeClassifier.Classify(levelMesh, bank, trg);
                if (sky != null)
                {
                    var anchor = sky.AnchorNodePosition != null
                        ? PsxLevelObjectPlacementResolver.CreateNodeTranslation(
                            sky.AnchorNodePosition, levelMesh.TranslationDivisor)
                        : Matrix4x4.CreateTranslation(
                            PsxSkyDomeClassifier.LevelCentroidGltf(levelMesh));
                    var withSkyAnchors =
                        new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>(remainingBank);
                    foreach (var skyIndex in sky.ObjectIndices)
                    {
                        withSkyAnchors[skyIndex] =
                        [
                            new PsxLevelObjectPlacement(
                                PsxLevelObjectPlacementResolver.BankInstanceNodeIndex, anchor)
                        ];
                    }

                    remainingBank = withSkyAnchors;
                }

                // The per-file detector cannot see a bank face after its TRG
                // placement lands on level geometry. Assemble that exact
                // level+remaining-bank scope here, after What-If gating,
                // items substitution, and sky anchoring have established what
                // this pass will actually emit. Results stay keyed by placement
                // index so a repeated prop is split only where it overlaps.
                IReadOnlyDictionary<PsxPlacedFaceInstanceKey, PsxCoplanarOverlayAssignment>?
                    placedCoplanarOverlays = null;
                try
                {
                    placedCoplanarOverlays =
                        PsxPlacedCoplanarOverlayResolver.FindBankOverlays(
                            levelMesh,
                            bank,
                            remainingBank,
                            sky?.ObjectIndices,
                            hiddenLevelObjectIndices).Assignments;
                }
                catch (Exception ex)
                {
                    // This is optional ordering metadata. A novel transform or
                    // malformed face must never suppress the bank geometry.
                    Debug.WriteLine(
                        $"Unable to resolve placed PSX coplanar overlays: {ex.Message}");
                }

                PsxGeometryWriter.PopulatePsx(
                    document,
                    bank,
                    MeshCompanionResolver.BuildPsxTextureProvider(
                        request.Source, companionName, companionBytes),
                    nodeNamePrefix: "objects",
                    context: geometryContext,
                    objectPlacements: remainingBank,
                    skyObjectIndices: sky?.ObjectIndices,
                    skyLayerOrder: sky?.LayerOrder,
                    skyColor: sky?.SkyColor,
                    ghostOptions: new PsxGhostEmissionOptions
                    {
                        AssetHash = assetHash,
                        VisibilityOverrides = request.VisibilityOverrides
                    },
                    placedCoplanarOverlays: placedCoplanarOverlays);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
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
            true);
        // Offset-named pak MDLs ride zone dictionaries; skins/scenes ride the
        // DMA-REF-verified scene decoder (both v6 TEX parsers false-accept the
        // other's layout, so the sub-format decides — see BuildPs2TextureProvider).
        var textureProvider = MeshCompanionResolver.BuildPs2TextureProvider(
            companionTexData,
            request.Ps2SubFormat == Ps2SceneSubFormat.PakMdl);
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
                document, 0, ps2Animations, boneIndexMap: request.SkaQbKeyBoneMap);
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
            true);
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
                "archive_companion.stex", companionTexData, true);
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

        var debugCollector = request.WorldzoneDebugDirectory != null
            ? new Ps2GeomDebugCollector(request.OutputStem)
            : null;

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
            request.WorldzoneScale,
            debugCollector: debugCollector,
            visibilityOverrides: request.VisibilityOverrides);

        if (debugCollector != null && request.WorldzoneDebugDirectory != null)
        {
            Ps2WorldzoneDebugDump.Write(
                request.WorldzoneDebugDirectory,
                request.OutputStem,
                debugCollector,
                textureCatalog);
        }

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
            ? MeshCompanionResolver.BuildNgcSceneTextureProvider(request.Source, request.OutputStem,
                request.TexturePath)
            : MeshCompanionResolver.BuildXbxSceneTextureProvider(request.Source, request.OutputStem,
                request.TexturePath);
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
                firstTexture,
                material.Passes.Length > 0 ? material.Passes[0].BlendMode : 0,
                material.Passes.Length > 0 ? material.Passes[0].FixedAlpha : 0,
                material.Passes.Length));
            document.Materials.Add(renderMaterial);
        }

        var explicitSkeleton = request.PreparedSkeleton ?? TryLoadExplicitXbxSkeleton(
            request.SkeletonPath, request.OutputStem);
        XbxGeometryWriter.PopulateXbxScene(
            document,
            scene,
            textureProvider,
            request.WorldzoneScale,
            explicitSkeleton);
        return document;
    }

    private static Ps2Skeleton? TryLoadExplicitXbxSkeleton(
        string? skeletonPath,
        string meshStem)
    {
        // Skin emission on this route is intentionally caller-explicit. Callers
        // provide an exact file or a directory containing an exact-stem companion;
        // a missing, malformed, or unrelated skeleton preserves historical rigid
        // output. No implicit rig discovery or bone-count inference is permitted.
        var resolvedPath = MeshCompanionResolver.ResolveExplicitPath(
            skeletonPath,
            meshStem,
            [".ske.ps2", ".ske.xbx", ".ske.ngc", ".ske"],
            ["SKE", "Skeletons"]);
        if (resolvedPath == null)
            return null;

        try
        {
            return SkeletonAssetLoader.Parse(
                Path.GetFileName(resolvedPath), File.ReadAllBytes(resolvedPath));
        }
        catch
        {
            return null;
        }
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
                document, 0, rwAnimations, SkaCompositionMode.Thps3Runtime, boneMap);
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
                MeshCompanionResolver.BuildRwTxdTextureProvider(request.Source, request.FileName,
                    request.TexturePath)));

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
