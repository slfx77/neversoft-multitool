using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool;

/// <summary>
///     Builds animated GLB output for the Character Preview tab. Wraps the
///     pieces shared between previewing one animation and exporting many.
///
///     Supports two character paths:
///     <list type="bullet">
///         <item>PS2 skinned scenes (.skin.ps2 / .iskin.ps2 / ThawSkin / PakSkin) via <see cref="Ps2SceneGltfWriter.WriteSkinnedAnimated"/>.</item>
///         <item>RenderWare DFF (THPS3 .SKN) via <see cref="RwDffGltfWriter.WriteAnimated"/> — animation correctness is still in progress per docs/thps3-ska-animation-correctness-handoff.md.</item>
///     </list>
/// </summary>
internal static class CharacterAnimationConverter
{
    public sealed record Result(byte[]? GlbBytes, int Triangles, string? Error);

    /// <summary>
    ///     Build an animated GLB from a character + N animations. Returns bytes
    ///     in memory (no temp files). Caller writes to disk as needed.
    /// </summary>
    public static Result BuildAnimatedGlb(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations)
    {
        if (animations.Count == 0)
            return new Result(null, 0, "No animations selected.");

        if (character.IsRwDff)
            return BuildRwDff(character, animations);

        if (character.IsPs2Scene)
            return BuildPs2Scene(character, animations);

        if (character.IsPsx && character.PsxHasHierarchy)
            return BuildPsx(character, animations);

        return new Result(null, 0,
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

            if (character.IsPsx && character.PsxHasHierarchy)
            {
                // PSX characters use Objects as bones (1:1 with the joint hierarchy).
                return character.ObjectCount;
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

    private static Result BuildPs2Scene(
        MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        var data = character.Source.ReadBytes();
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);

        var companionTexData = character.Source.TryReadCompanion(
            stem, MeshConverterTabFileConverter.Ps2TexExtensions, MeshConverterTabFileConverter.Ps2TexSubdirs);
        var textureProvider = MeshConverterTabFileConverter.BuildPs2TextureProvider(companionTexData);

        var scene = character.Ps2SubFormat switch
        {
            Ps2SceneSubFormat.ThawSkin => ThawPs2SkinFile.Parse(data, companionTexData),
            Ps2SceneSubFormat.PakSkin => ThawPs2SkinFile.ParsePakSkin(data),
            _ => Ps2SceneFile.Parse(data)
        };

        var skeleton = MeshConverterTabFileConverter.TryLoadPs2Skeleton(character, stem);
        if (skeleton == null)
            return new Result(null, 0, "No skeleton found for this character.");

        // THAW transfer step: skinning lives in the PC sibling, copied into the PS2 scene.
        if (character.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin)
        {
            var pcBytes = character.Source.TryReadCompanion(
                stem, MeshConverterTabFileConverter.PcSkinExtensions, MeshConverterTabFileConverter.PcSkinSubdirs);
            var transferred = pcBytes != null
                ? ThawPs2SkinningTransfer.TryApplyFromBytes(scene, pcBytes, skeleton)
                : null;
            if (transferred is { SkinnedVertexCount: > 0 })
                scene = transferred.Scene;
            else
                return new Result(null, 0,
                    "THAW skinned mesh requires a PC sibling (.skin.wpc) to recover skinning data.");
        }

        // V1 (THPS4) skeletons have no native bind pose; enrich from a default
        // animation in the same archetype subtree if available.
        if (skeleton.Version == 1)
        {
            var defaultAnim = TryFindDefaultPoseAnim(character, animations[0]);
            if (defaultAnim != null && defaultAnim.BoneTracks.Length == skeleton.Bones.Length)
                skeleton = Ps2SkeletonDefaultPose.EnrichWithDefaultPose(skeleton, defaultAnim);
        }

        // Parse each animation; skip any that fail.
        var named = new List<(string Name, SkaAnimation Animation)>();
        foreach (var probe in animations)
        {
            var anim = TryParseAnimation(probe);
            if (anim == null) continue;
            if (anim.BoneTracks.Length != skeleton.Bones.Length) continue;
            var name = StripAnimExtension(probe.DisplayName);
            named.Add((name, anim));
        }

        if (named.Count == 0)
            return new Result(null, 0, "No animations matched the character's skeleton.");

        var (model, triangles) = Ps2SceneGltfWriter.BuildSkinnedAnimated(
            scene, skeleton, named, textureProvider);
        if (triangles == 0)
            return new Result(null, 0, "Mesh has no triangles after skinning.");

        return new Result(WriteGlbToMemory(model), triangles, null);
    }

    private static Result BuildRwDff(
        MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        var clump = RwDffFile.Parse(character.Source.ReadBytes());
        var skin = clump.Atomics.FirstOrDefault(a => a.SkinData != null)?.SkinData;
        if (skin == null)
            return new Result(null, 0, "DFF clump is not skinned.");

        var named = new List<(string Name, SkaAnimation Animation)>();
        foreach (var probe in animations)
        {
            var anim = TryParseAnimation(probe);
            if (anim == null) continue;
            if (anim.BoneTracks.Length != skin.NumBones) continue;
            var name = StripAnimExtension(probe.DisplayName);
            named.Add((name, anim));
        }

        if (named.Count == 0)
            return new Result(null, 0, "No animations matched the character's bone count.");

        var textureProvider = BuildRwDffTextureProvider(character);

        // Use the single-anim overload when only one is selected (returns the same
        // bytes as the multi-anim path with a one-element list, but slightly
        // shorter call).
        using var ms = new MemoryStream();
        var tempPath = Path.Combine(
            Path.GetTempPath(), "NeversoftMultitool", "CharacterPreview",
            $"{Guid.NewGuid():N}.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            var triangles = RwDffGltfWriter.WriteAnimated(clump, named, tempPath, textureProvider);
            if (triangles == 0)
                return new Result(null, 0, "DFF produced no triangles.");

            var bytes = File.ReadAllBytes(tempPath);
            return new Result(bytes, triangles, null);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
        }
    }

    private static Result BuildPsx(
        MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        var data = character.Source.ReadBytes();
        var psxFile = PsxMeshFile.Parse(data);
        if (psxFile == null)
            return new Result(null, 0, "PSX file has no parseable mesh data.");
        if (!psxFile.HasHierarchy)
            return new Result(null, 0, "PSX file is not a hierarchical character.");

        // Parse the anim table once for the whole character; each selected
        // animation just decodes its slot directly, avoiding N redundant
        // file reads + parses through PsxAnimationSource.Decode.
        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        if (animFile == null)
            return new Result(null, 0, "PSX file has no recognizable animation table.");

        var named = new List<(string Name, PsxAnimation Animation)>();
        foreach (var probe in animations)
        {
            if (probe.Source is not PsxAnimationSource psxSource) continue;
            if (psxSource.AnimIndex < 0 || psxSource.AnimIndex >= animFile.Entries.Count) continue;
            try
            {
                var entry = animFile.Entries[psxSource.AnimIndex];
                var slice = animFile.Pool.Span[entry.PoolOffset..];
                var animation = PsxAnimDecoder.Decode(slice, psxFile.Objects.Count, entry.FrameCount);
                if (animation.BoneCount != psxFile.Objects.Count) continue;
                named.Add((probe.DisplayName, animation));
            }
            catch
            {
                // Single anim failed — keep going so the rest can still preview.
            }
        }

        if (named.Count == 0)
            return new Result(null, 0, "No animations decoded successfully for this PSX character.");

        var pshFile = PshFile.FindCompanion(character.Source.FileSystemPath ?? string.Empty);
        var textureProvider = BuildPsxTextureProvider(character);

        var (model, triangles) = PsxGltfWriter.BuildAnimated(
            psxFile, named, textureProvider, pshFile);
        if (triangles == 0)
            return new Result(null, 0, "PSX mesh produced no triangles.");

        return new Result(WriteGlbToMemory(model), triangles, null);
    }

    private static PsxGltfWriter.TextureProvider? BuildPsxTextureProvider(MeshFileEntry character)
    {
        var fsPath = character.Source.FileSystemPath;
        return fsPath == null ? null : PsxTextureProviderFactory.FromFile(fsPath);
    }

    private static SkaAnimation? TryParseAnimation(AnimationProbe probe)
    {
        try
        {
            var bytes = probe.Source.ReadBytes();
            if (!SkaFile.IsSkaFile(bytes)) return null;

            // Find compress table for filesystem-backed anims; archive-backed
            // anims fall back to no table (uncompressed anims still parse;
            // compressed ones throw and we skip).
            SkaCompressTable? table = null;
            var fsPath = probe.Source.FileSystemPath;
            if (fsPath != null)
                table = SkaCommand.FindCompressTable(fsPath);

            return SkaFile.Parse(bytes, table);
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
            if (!SkaFile.IsSkaFile(bytes)) return null;

            var table = SkaCommand.FindCompressTable(defaultPath);
            return SkaFile.Parse(bytes, table);
        }
        catch
        {
            return null;
        }
    }

    private static RwDffGltfWriter.TextureProvider? BuildRwDffTextureProvider(MeshFileEntry character)
    {
        // RW DFF textures live in companion .tex files. Reuse the existing helper
        // that handles both filesystem and archive sources.
        return MeshConverterTabFileConverter.BuildRwDffTextureProvider(character);
    }

    private static byte[] WriteGlbToMemory(SharpGLTF.Schema2.ModelRoot model)
    {
        using var ms = new MemoryStream();
        model.WriteGLB(ms);
        return ms.ToArray();
    }

    private static string StripAnimExtension(string fileName)
    {
        // Strip ".ska", ".ska.ps2", etc. so animation track names are clean.
        var idx = fileName.IndexOf(".ska", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? fileName[..idx] : Path.GetFileNameWithoutExtension(fileName);
    }
}
