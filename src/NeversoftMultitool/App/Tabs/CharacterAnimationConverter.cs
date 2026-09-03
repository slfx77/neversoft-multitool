using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool;

/// <summary>
///     Builds animated GLB output for the Character Preview tab. Routes both
///     PS2 skinned scenes and THPS3 RW DFF characters through the unified
///     <see cref="MeshModelParser" /> + <see cref="GltfModelExporter" /> pipeline,
///     attaching parsed SKA animations via <c>MeshImportRequest.SkaAnimations</c>.
///     PSX characters use the same pipeline with <c>PsxDecodedAnimations</c>;
///     conservatively gated N64 shells route selected embedded 0x2A/0x2C indices.
/// </summary>
internal static class CharacterAnimationConverter
{
    /// <summary>
    ///     Build an animated GLB from a character + N animations. Returns bytes
    ///     in memory (no temp files). Caller writes to disk as needed.
    /// </summary>
    public static Result BuildAnimatedGlb(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        SkaAnimationSourceRig? animationSourceRig = null,
        bool oneShot = false)
    {
        var (document, error) = BuildDocument(
            character, animations, visibilityOverrides, animationSourceRig, oneShot);
        if (document == null)
            return new Result(null, 0, error);

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        if (triangles == 0 || glbBytes == null)
            return new Result(null, 0, "Mesh has no triangles after skinning.");

        return new Result(glbBytes, triangles, null)
        {
            VisibilityGroups = document.VisibilityGroups.ToArray()
        };
    }

    /// <summary>
    ///     Build the animated model document for a character + N animations.
    ///     The document feeds either exporter (GLB via
    ///     <see cref="GltfModelExporter" />, .blend via
    ///     <see cref="ModelExportService" />).
    ///     <paramref name="oneShot" /> selects the tween end-of-clip branch for
    ///     the two families that store one: PSX banks and N64 direct (0x2A)
    ///     clips. SKA-based PS2/RW characters store every frame and ignore it.
    /// </summary>
    public static DocumentResult BuildDocument(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        SkaAnimationSourceRig? animationSourceRig = null,
        bool oneShot = false)
    {
        if (animations.Count == 0)
            return new DocumentResult(null, "No animations selected.");

        if (animationSourceRig != null && !character.IsPs2Scene)
            return new DocumentResult(null,
                "An explicit animation source rig is supported only for PS2-scene characters.");

        if (character.IsRwDff)
            return BuildRwDff(character, animations, visibilityOverrides);

        if (character.IsPs2Scene)
            return BuildPs2Scene(
                character, animations, visibilityOverrides, animationSourceRig);

        if (character.IsN64Model && character.N64HasEmbeddedAnimations)
            return BuildN64(character, animations, oneShot);

        if (character.IsGbaModel)
            return BuildGba(character, animations);

        if (character.IsNdsGeometry)
            return BuildNds(character, animations);

        if (character.IsPsx && character.PsxIsSuperModel)
            return BuildPsx(character, animations, visibilityOverrides, oneShot);

        return new DocumentResult(null,
            $"Animated preview not supported for {character.FormatDisplay}.");
    }

    /// <summary>
    ///     Resolve the skeleton bone count for a character (used to filter the
    ///     animation list). Returns null when no skeleton is found.
    /// </summary>
    public static int? GetSkeletonBoneCount(MeshFileEntry character)
    {
        try
        {
            if (character.IsRwDff)
            {
                var clump = RwDffFile.Parse(character.Source.ReadBytes());
                var skin = clump.Atomics.FirstOrDefault(a => a.SkinData != null)?.SkinData;
                return skin?.NumBones;
            }

            if (character.IsPs2Scene)
            {
                var stem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
                var skel = MeshConverterTabFileConverter.TryLoadPs2Skeleton(character, stem);
                return skel?.Bones.Length;
            }

            if (character.IsPsx && character.PsxIsSuperModel)
            {
                // PSX supers use Objects as bones. Apocalypse / THPS1 flat
                // supers have all-root joints rather than a HIER table.
                return character.ObjectCount;
            }

            if (character.IsN64Model && character.N64HasEmbeddedAnimations)
                return character.ObjectCount;

            if (character.IsGbaModel)
            {
                // A GBA rider is a morph model with no skeleton; the pane's rig
                // size is its vertex count, and every clip fits it.
                var rom = character.Source.TryReadCompanion(GbaLevelCarver.RomEntryName);
                return rom == null ? null : GbaRiderClips.TryGetVertexCount(rom);
            }

            if (character.IsNdsGeometry)
            {
                // The DS engine has no skeleton: bones are the matrices the display
                // list actually uses, identified by provenance in the writer.
                var data = character.Source.ReadBytes();
                return NdsGeometryFile.TryParseValidated(data, out var geometry)
                    ? NdsGxInterpreter.RunInterpreter(data, geometry).UsedMatrices.Count
                    : null;
            }
        }
        catch
        {
            // Skeleton load can fail for many reasons (corrupt file, missing
            // companion, etc.) — surface as "unknown" so the discovery layer
            // doesn't filter every anim out.
        }

        return null;
    }

    private static DocumentResult BuildN64(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        bool oneShot)
    {
        var indices = animations
            .Select(static probe => probe.Source)
            .OfType<N64AnimationSource>()
            .Where(source => ReferenceEquals(source.ModelSource, character.Source))
            .Select(static source => source.AnimationIndex)
            .Distinct()
            .ToArray();
        if (indices.Length == 0)
            return new DocumentResult(null, "No embedded N64 animation slots were selected.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = MeshTypeDetector.GetN64BundleStem(fileName),
            SourceKind = ModelSourceKind.N64Model,
            N64AnimationIndices = indices,
            N64AnimationOneShot = oneShot
        });

        return document.Animations.Count > 0
            ? new DocumentResult(document, null)
            : new DocumentResult(null, "The selected N64 animation slots did not decode.");
    }

    /// <summary>
    ///     The GBA clips a probe selection names, in pane order.
    /// </summary>
    public static IReadOnlyList<int> GbaClipIndices(
        MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        return animations
            .Select(static probe => probe.Source)
            .OfType<GbaAnimationSource>()
            .Where(source => ReferenceEquals(source.ModelSource, character.Source))
            .Select(static source => source.ClipIndex)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    ///     Builds ONE GBA clip. The skater animates by morphing, and a glTF
    ///     weights track addresses every target of the mesh, so a document
    ///     carries one clip — callers wanting several build several.
    /// </summary>
    public static DocumentResult BuildGbaClip(MeshFileEntry character, int clipIndex)
    {
        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = MeshTypeDetector.GetStem(fileName),
            SourceKind = ModelSourceKind.GbaModel,
            GbaAnimationIndices = [clipIndex]
        });

        return document.Animations.Count > 0
            ? new DocumentResult(document, null)
            : new DocumentResult(null, "The selected GBA animation clip did not decode.");
    }

    private static DocumentResult BuildGba(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations)
    {
        var indices = GbaClipIndices(character, animations);
        return indices.Count == 0
            ? new DocumentResult(null, "No GBA animation clips were selected.")
            : BuildGbaClip(character, indices[0]);
    }

    /// <summary>
    ///     The DS clips a probe selection names, in pane order.
    /// </summary>
    public static IReadOnlyList<int> NdsClipIndices(
        MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        return animations
            .Select(static probe => probe.Source)
            .OfType<NdsAnimationSource>()
            .Where(source => ReferenceEquals(source.ModelSource, character.Source))
            .Select(static source => source.ClipIndex)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    ///     Builds a DS model carrying the selected clips. Unlike GBA morph targets,
    ///     DS clips are ordinary skinned tracks over the display list's matrices, so
    ///     several fit in one document.
    /// </summary>
    private static DocumentResult BuildNds(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations)
    {
        var indices = NdsClipIndices(character, animations);
        if (indices.Count == 0)
            return new DocumentResult(null, "No DS animation clips were selected.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = MeshTypeDetector.GetStem(fileName),
            SourceKind = ModelSourceKind.NdsModel,
            NdsAnimationIndices = indices
        });

        return document.Animations.Count == 0
            ? new DocumentResult(null, "The selected DS clips do not apply to this model.")
            : new DocumentResult(document, null);
    }

    private static DocumentResult BuildPs2Scene(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides,
        SkaAnimationSourceRig? animationSourceRig)
    {
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var skeleton = MeshConverterTabFileConverter.TryLoadPs2Skeleton(character, stem);
        if (skeleton == null)
            return new DocumentResult(null, "No skeleton found for this character.");

        // V1 (THPS4) skeletons have no native bind pose; enrich from a default
        // animation in the same archetype subtree if available.
        if (skeleton.Version == 1)
        {
            var defaultAnim = TryFindDefaultPoseAnim(character, animations[0]);
            if (defaultAnim != null && defaultAnim.BoneTracks.Length == skeleton.Bones.Length)
                skeleton = Ps2SkeletonDefaultPose.EnrichWithDefaultPose(skeleton, defaultAnim);
        }

        SkaAnimationBindingPlan bindingPlan;
        try
        {
            bindingPlan = SkaAnimationBindingPlan.Create(skeleton, animationSourceRig);
        }
        catch (InvalidDataException ex)
        {
            return new DocumentResult(null, $"Animation rig cannot bind to this character: {ex.Message}");
        }

        var named = new List<(string Name, SkaAnimation Animation)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in animations)
        {
            var anim = TryParseAnimation(probe);
            if (anim == null) continue;
            if (!bindingPlan.MatchesTrackCount(anim.BoneTracks.Length)) continue;
            var animationName = AnimationExportName.ForMesh(
                stem, StripAnimExtension(probe.ResolvedDisplayName), usedNames);
            named.Add((animationName, anim));
        }

        if (named.Count == 0)
            return new DocumentResult(null, "No animations matched the character's skeleton.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = stem,
            SourceKind = ModelSourceKind.Ps2Scene,
            Ps2SubFormat = character.Ps2SubFormat,
            PreparedSkeleton = skeleton,
            SkaAnimations = named,
            SkaQbKeyBoneMap = bindingPlan.BoneMap,
            VisibilityOverrides = visibilityOverrides
        });

        return new DocumentResult(document, null);
    }

    private static DocumentResult BuildRwDff(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides)
    {
        var clump = RwDffFile.Parse(character.Source.ReadBytes());
        var skin = clump.Atomics.FirstOrDefault(a => a.SkinData != null)?.SkinData;
        if (skin == null)
            return new DocumentResult(null, "DFF clump is not skinned.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var named = new List<(string Name, SkaAnimation Animation)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in animations)
        {
            var anim = TryParseAnimation(probe);
            if (anim == null) continue;
            if (anim.BoneTracks.Length != skin.NumBones) continue;
            var animationName = AnimationExportName.ForMesh(
                stem, StripAnimExtension(probe.ResolvedDisplayName), usedNames);
            named.Add((animationName, anim));
        }

        if (named.Count == 0)
            return new DocumentResult(null, "No animations matched the character's bone count.");

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = stem,
            SourceKind = ModelSourceKind.RenderWareDff,
            SkaAnimations = named,
            VisibilityOverrides = visibilityOverrides
        });

        return new DocumentResult(document, null);
    }

    private static DocumentResult BuildPsx(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides,
        bool oneShot)
    {
        var data = character.Source.ReadBytes();
        var psxFile = PsxMeshFile.Parse(data);
        if (psxFile == null)
            return new DocumentResult(null, "PSX file has no parseable mesh data.");
        if (!psxFile.IsSuperModel)
            return new DocumentResult(null, "PSX file is not a character super model.");

        // Translation channels compose through the hierarchy that ships with
        // the anim data, so clips from an external bank (e.g. sk2anim.psx)
        // carry that bank's parent table. Cache per bank — probes from the
        // same bank share it.
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var parentsByBank = new Dictionary<AssetSource, int[]?>();
        var clips = new List<PsxAnimationClip>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in animations)
        {
            if (probe.Source is not PsxAnimationSource psxSource) continue;
            try
            {
                var animation = psxSource.Decode(oneShot);
                if (animation.BoneCount != psxFile.Objects.Count) continue;
                if (!parentsByBank.TryGetValue(psxSource.BankSource, out var parents))
                {
                    parents = PsxAnimationBank.TryBuildSourceParentIndices(
                        psxSource.BankSource, psxFile.Objects.Count, psxSource.BoneRemap);
                    parentsByBank[psxSource.BankSource] = parents;
                }

                var animationName = AnimationExportName.ForMesh(
                    stem, probe.ResolvedDisplayName, usedNames);
                clips.Add(new PsxAnimationClip(animationName, animation, parents));
            }
            catch
            {
                // Single anim failed — keep going so the rest can still preview.
            }
        }

        if (clips.Count == 0)
            return new DocumentResult(null, "No animations decoded successfully for this PSX character.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = Path.GetFileNameWithoutExtension(fileName),
            SourceKind = ModelSourceKind.Psx,
            PsxAnimationOptions = new PsxAnimationOptions(
                Fps: PsxAnimationBank.DefaultPreviewFps, OneShot: oneShot),
            PsxAnimationClips = clips,
            VisibilityOverrides = visibilityOverrides
        });

        return new DocumentResult(document, null);
    }

    private static SkaAnimation? TryParseAnimation(AnimationProbe probe)
    {
        try
        {
            var bytes = probe.Source.ReadBytes();

            // Filesystem animations can use a nearby compression table. Archive
            // sources cannot resolve one here: uncompressed clips still parse,
            // while compressed clips are rejected and omitted.
            SkaCompressTable? table = null;
            var fsPath = probe.Source.FileSystemPath;
            if (fsPath != null)
                table = SkaCommand.FindCompressTable(fsPath);

            return SkaFile.ParseExportableCharacterAnimation(bytes, table);
        }
        catch
        {
            return null;
        }
    }

    private static SkaAnimation? TryFindDefaultPoseAnim(
        MeshFileEntry character, AnimationProbe seedAnim)
    {
        // V1 default-pose enrichment requires a filesystem-backed character (so
        // we can walk ancestor dirs for {archetype}/default.ska.ps2). Archive-
        // backed characters fall back to identity bind pose.
        var skinFsPath = character.Source.FileSystemPath;
        var animFsPath = seedAnim.Source.FileSystemPath;
        if (skinFsPath == null || animFsPath == null) return null;

        try
        {
            var defaultPath = SkaCommand.FindDefaultPoseFile(skinFsPath, animFsPath);
            if (defaultPath == null) return null;

            var bytes = File.ReadAllBytes(defaultPath);
            var table = SkaCommand.FindCompressTable(defaultPath);
            return SkaFile.ParseExportableCharacterAnimation(bytes, table);
        }
        catch
        {
            return null;
        }
    }

    private static string StripAnimExtension(string fileName)
    {
        // Strip ".ska", ".ska.ps2", etc. so animation track names are clean.
        var idx = fileName.IndexOf(".ska", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? fileName[..idx] : Path.GetFileNameWithoutExtension(fileName);
    }

    public sealed record Result(byte[]? GlbBytes, int Triangles, string? Error)
    {
        public IReadOnlyList<ModelVisibilityGroup> VisibilityGroups { get; init; } = [];
    }

    public sealed record DocumentResult(ModelDocument? Document, string? Error);
}
