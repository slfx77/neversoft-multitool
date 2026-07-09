using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

internal static class PsxAnimationBoneMap
{
    private static readonly string[] PshExtensions = [".psh"];

    public static PsxAnimationBoneRemap? TryCreate(
        AssetSource sourceBank,
        AssetSource targetCharacter,
        int boneCount,
        out string? diagnostic)
    {
        diagnostic = null;
        if (boneCount <= 0)
            return null;

        var sourcePsh = TryReadPsh(sourceBank);
        var targetPsh = TryReadPsh(targetCharacter);
        if (sourcePsh == null || targetPsh == null)
        {
            diagnostic = "missing source or target PSH companion";
            return null;
        }

        if (!TryBuild(sourcePsh, targetPsh, boneCount, out var map, out diagnostic))
            return null;

        return new PsxAnimationBoneRemap(map);
    }

    public static PsxAnimation Remap(
        PsxAnimation animation,
        PsxAnimationBoneRemap? remap,
        int targetBoneCount)
    {
        if (remap == null || remap.IsIdentity)
            return animation;

        var channels = new short[targetBoneCount, PsxAnimation.ChannelsPerBone, animation.FrameCount];
        var sourceLimit = Math.Min(animation.BoneCount, remap.SourceToTarget.Count);
        for (var sourceBone = 0; sourceBone < sourceLimit; sourceBone++)
        {
            var targetBone = remap.SourceToTarget[sourceBone];
            if (targetBone < 0 || targetBone >= targetBoneCount)
                continue;

            for (var channel = 0; channel < PsxAnimation.ChannelsPerBone; channel++)
            {
                for (var frame = 0; frame < animation.FrameCount; frame++)
                    channels[targetBone, channel, frame] = animation.Channels[sourceBone, channel, frame];
            }
        }

        return new PsxAnimation
        {
            FrameCount = animation.FrameCount,
            BoneCount = targetBoneCount,
            Channels = channels,
            RotationUnitsPerRevolution = animation.RotationUnitsPerRevolution
        };
    }

    internal static PshFile? TryReadPsh(AssetSource source)
    {
        var stem = Path.GetFileNameWithoutExtension(source.EntryName);
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        var bytes = source.TryReadCompanion(stem, PshExtensions);
        return bytes == null ? null : PshFile.Parse(bytes);
    }

    internal static bool TryBuild(
        PshFile sourcePsh,
        PshFile targetPsh,
        int boneCount,
        out int[] sourceToTarget,
        out string? diagnostic)
    {
        sourceToTarget = new int[boneCount];
        Array.Fill(sourceToTarget, -1);
        diagnostic = null;

        var sourceByIndex = sourcePsh.Bones
            .Where(static b => b.Index >= 0)
            .GroupBy(static b => b.Index)
            .ToDictionary(static g => g.Key, static g => g.First());

        // Engine tier: CalculateAnimOrder (decomp byte-PERFECT, 2026-07-09)
        // matches with a real BIOS strcmp — CASE-SENSITIVE, no normalization —
        // and on duplicate names the FIRST (lowest-index) bone wins (inner
        // loop breaks on first hit). The case-insensitive and semantic tiers
        // below are converter leniency beyond the engine, consulted only when
        // the engine-faithful comparison finds nothing.
        var targetExact = BuildFirstWinsIndex(targetPsh, StringComparer.Ordinal);
        var targetIgnoreCase = BuildFirstWinsIndex(targetPsh, StringComparer.OrdinalIgnoreCase);
        var targetSemantic = BuildUniqueSemanticIndex(targetPsh);

        var usedTargets = new bool[boneCount];
        for (var sourceIndex = 0; sourceIndex < boneCount; sourceIndex++)
        {
            if (!sourceByIndex.TryGetValue(sourceIndex, out var sourceBone))
            {
                diagnostic = $"source PSH has no bone {sourceIndex}";
                return false;
            }

            if (!TryResolveTargetIndex(
                    sourceBone.Name, targetExact, targetIgnoreCase, targetSemantic,
                    out var targetIndex)
                || targetIndex < 0
                || targetIndex >= boneCount)
            {
                diagnostic = $"target PSH has no match for source bone '{sourceBone.Name}'";
                return false;
            }

            if (usedTargets[targetIndex])
            {
                diagnostic = $"target bone {targetIndex} matched more than once";
                return false;
            }

            sourceToTarget[sourceIndex] = targetIndex;
            usedTargets[targetIndex] = true;
        }

        return true;
    }

    private static bool TryResolveTargetIndex(
        string sourceName,
        Dictionary<string, int> targetExact,
        Dictionary<string, int> targetIgnoreCase,
        Dictionary<string, int> targetSemantic,
        out int targetIndex)
    {
        // Tier 1 mirrors the engine: a case-sensitive strcmp where the
        // first duplicate wins. Tiers 2 and 3 are converter leniency for
        // cross-bank pairings the engine would leave unmapped.
        if (targetExact.TryGetValue(sourceName, out targetIndex))
            return true;

        if (targetIgnoreCase.TryGetValue(sourceName, out targetIndex))
            return true;

        return targetSemantic.TryGetValue(ToSemanticName(sourceName), out targetIndex);
    }

    private static Dictionary<string, int> BuildFirstWinsIndex(
        PshFile targetPsh, StringComparer comparer)
    {
        var index = new Dictionary<string, int>(comparer);
        foreach (var bone in targetPsh.Bones)
        {
            if (bone.Index < 0)
                continue;

            // TryAdd keeps the first (lowest-index) bone on duplicate names,
            // matching CalculateAnimOrder's break-on-first-hit inner loop.
            index.TryAdd(bone.Name, bone.Index);
        }

        return index;
    }

    private static Dictionary<string, int> BuildUniqueSemanticIndex(PshFile targetPsh)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bone in targetPsh.Bones)
        {
            if (bone.Index < 0)
                continue;

            var semantic = ToSemanticName(bone.Name);
            if (ambiguous.Contains(semantic))
                continue;

            if (index.TryGetValue(semantic, out var existing) && existing != bone.Index)
            {
                index.Remove(semantic);
                ambiguous.Add(semantic);
                continue;
            }

            index[semantic] = bone.Index;
        }

        return index;
    }

    private static string ToSemanticName(string name)
    {
        var firstUnderscore = name.IndexOf('_');
        return firstUnderscore > 0 && firstUnderscore + 1 < name.Length
            ? name[(firstUnderscore + 1)..]
            : name;
    }
}

internal sealed record PsxAnimationBoneRemap(IReadOnlyList<int> SourceToTarget)
{
    public bool IsIdentity
    {
        get
        {
            for (var i = 0; i < SourceToTarget.Count; i++)
            {
                if (SourceToTarget[i] != i)
                    return false;
            }

            return true;
        }
    }

    public int RemappedCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < SourceToTarget.Count; i++)
            {
                if (SourceToTarget[i] != i)
                    count++;
            }

            return count;
        }
    }
}
