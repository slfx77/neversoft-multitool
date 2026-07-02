using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool;

/// <summary>
///     Builds animated GLB output for the Character Preview tab. Routes both
///     PS2 skinned scenes and THPS3 RW DFF characters through the unified
///     <see cref="MeshModelParser" /> + <see cref="GltfModelExporter" /> pipeline,
///     attaching parsed SKA animations via <c>MeshImportRequest.SkaAnimations</c>.
/// </summary>
internal static class CharacterAnimationConverter
{
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
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var skeleton = MeshConverterTabFileConverter.TryLoadPs2Skeleton(character, stem);
        if (skeleton == null)
            return new Result(null, 0, "No skeleton found for this character.");

        // V1 (THPS4) skeletons have no native bind pose; enrich from a default
        // animation in the same archetype subtree if available.
        if (skeleton.Version == 1)
        {
            var defaultAnim = TryFindDefaultPoseAnim(character, animations[0]);
            if (defaultAnim != null && defaultAnim.BoneTracks.Length == skeleton.Bones.Length)
                skeleton = Ps2SkeletonDefaultPose.EnrichWithDefaultPose(skeleton, defaultAnim);
        }

        var named = new List<(string Name, SkaAnimation Animation)>();
        foreach (var probe in animations)
        {
            var anim = TryParseAnimation(probe);
            if (anim == null) continue;
            if (anim.BoneTracks.Length != skeleton.Bones.Length) continue;
            named.Add((StripAnimExtension(probe.DisplayName), anim));
        }

        if (named.Count == 0)
            return new Result(null, 0, "No animations matched the character's skeleton.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = stem,
            SourceKind = ModelSourceKind.Ps2Scene,
            Ps2SubFormat = character.Ps2SubFormat,
            PreparedSkeleton = skeleton,
            SkaAnimations = named
        });

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        if (triangles == 0 || glbBytes == null)
            return new Result(null, 0, "Mesh has no triangles after skinning.");

        return new Result(glbBytes, triangles, null);
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
            named.Add((StripAnimExtension(probe.DisplayName), anim));
        }

        if (named.Count == 0)
            return new Result(null, 0, "No animations matched the character's bone count.");

        var fileName = Path.GetFileName(character.Source.FileSystemPath ?? character.FileName);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = character.Source,
            FileName = fileName,
            OutputStem = Path.GetFileNameWithoutExtension(fileName),
            SourceKind = ModelSourceKind.RenderWareDff,
            SkaAnimations = named
        });

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        if (triangles == 0 || glbBytes == null)
            return new Result(null, 0, "DFF produced no triangles.");

        return new Result(glbBytes, triangles, null);
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

    private static MeshNamedTextureResolver? BuildRwDffTextureProvider(MeshFileEntry character)
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

    public sealed record Result(byte[]? GlbBytes, int Triangles, string? Error);
}
