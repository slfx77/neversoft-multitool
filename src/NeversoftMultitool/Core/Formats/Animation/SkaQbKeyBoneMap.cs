using System.Collections;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     A validated source-SKA-track to target-skeleton bone map built only from
///     exact bone-name QbKey matches. Missing non-root source bones map to -1;
///     no positional or bone-count fallback is applied.
/// </summary>
public sealed class SkaQbKeyBoneMap : IReadOnlyList<int>
{
    private readonly int[] _sourceToTarget;

    private SkaQbKeyBoneMap(int[] sourceToTarget)
    {
        _sourceToTarget = sourceToTarget;
        MappedBoneCount = sourceToTarget.Count(static index => index >= 0);
    }

    public int SourceBoneCount => _sourceToTarget.Length;
    public int MappedBoneCount { get; }
    public int Count => _sourceToTarget.Length;
    public int this[int index] => _sourceToTarget[index];

    /// <summary>
    ///     Builds an exact-QbKey map. Duplicate names, malformed hierarchies,
    ///     and any source root that is absent or non-root in the target are
    ///     rejected rather than guessed.
    /// </summary>
    public static SkaQbKeyBoneMap Create(Ps2Skeleton source, Ps2Skeleton target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        _ = BuildUniqueNameIndex(source, "animation-source");
        var targetNames = BuildUniqueNameIndex(target, "target");
        ValidateHierarchy(source, "animation-source");
        ValidateHierarchy(target, "target");

        var sourceRoots = source.Bones
            .Select(static (bone, index) => (bone, index))
            .Where(static item => item.bone.ParentIndex < 0)
            .Select(static item => item.index)
            .ToArray();
        if (sourceRoots.Length == 0)
            throw new InvalidDataException("Animation-source skeleton has no root bone.");

        var map = new int[source.Bones.Length];
        Array.Fill(map, -1);
        for (var sourceIndex = 0; sourceIndex < source.Bones.Length; sourceIndex++)
        {
            var checksum = source.Bones[sourceIndex].NameChecksum;
            if (targetNames.TryGetValue(checksum, out var targetIndex))
                map[sourceIndex] = targetIndex;
        }

        foreach (var sourceRoot in sourceRoots)
        {
            var targetRoot = map[sourceRoot];
            var checksum = source.Bones[sourceRoot].NameChecksum;
            if (targetRoot < 0)
            {
                throw new InvalidDataException(
                    $"Animation-source root QbKey 0x{checksum:X8} is absent from the target skeleton.");
            }

            if (target.Bones[targetRoot].ParentIndex >= 0)
            {
                throw new InvalidDataException(
                    $"Animation-source root QbKey 0x{checksum:X8} maps to non-root target bone {targetRoot}.");
            }
        }

        // SKA keys are local to their authored parent. An exact bone-name hit
        // is therefore unsafe unless the immediate mapped parent edge is also
        // identical: inserting a target parent changes the local basis, and a
        // mapped child below a source-only parent has no valid basis at all.
        for (var sourceIndex = 0; sourceIndex < source.Bones.Length; sourceIndex++)
        {
            var targetIndex = map[sourceIndex];
            var sourceParent = source.Bones[sourceIndex].ParentIndex;
            if (targetIndex < 0 || sourceParent < 0)
                continue;

            var targetParent = target.Bones[targetIndex].ParentIndex;
            var mappedSourceParent = map[sourceParent];
            var checksum = source.Bones[sourceIndex].NameChecksum;
            if (mappedSourceParent < 0)
            {
                var parentChecksum = source.Bones[sourceParent].NameChecksum;
                throw new InvalidDataException(
                    $"Mapped source bone QbKey 0x{checksum:X8} has unmapped source parent " +
                    $"QbKey 0x{parentChecksum:X8}.");
            }

            if (targetParent != mappedSourceParent)
            {
                throw new InvalidDataException(
                    $"Mapped source bone QbKey 0x{checksum:X8} changes parent edge: " +
                    $"source parent maps to target bone {mappedSourceParent}, but the target parent is " +
                    $"{targetParent}.");
            }
        }

        return new SkaQbKeyBoneMap(map);
    }

    public IEnumerator<int> GetEnumerator() =>
        ((IEnumerable<int>)_sourceToTarget).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _sourceToTarget.GetEnumerator();

    private static Dictionary<uint, int> BuildUniqueNameIndex(Ps2Skeleton skeleton, string label)
    {
        if (skeleton.Bones.Length == 0)
            throw new InvalidDataException($"{Capitalize(label)} skeleton has no bones.");

        var result = new Dictionary<uint, int>(skeleton.Bones.Length);
        for (var index = 0; index < skeleton.Bones.Length; index++)
        {
            var checksum = skeleton.Bones[index].NameChecksum;
            if (checksum == 0)
            {
                throw new InvalidDataException(
                    $"{Capitalize(label)} skeleton bone {index} has an empty QbKey.");
            }

            if (!result.TryAdd(checksum, index))
            {
                throw new InvalidDataException(
                    $"{Capitalize(label)} skeleton has duplicate QbKey 0x{checksum:X8} " +
                    $"at bones {result[checksum]} and {index}.");
            }
        }

        return result;
    }

    private static void ValidateHierarchy(Ps2Skeleton skeleton, string label)
    {
        var verified = new bool[skeleton.Bones.Length];
        for (var start = 0; start < skeleton.Bones.Length; start++)
        {
            if (verified[start])
                continue;

            var chain = new List<int>();
            var chainSet = new HashSet<int>();
            var index = start;
            while (index >= 0 && !verified[index])
            {
                if (!chainSet.Add(index))
                {
                    throw new InvalidDataException(
                        $"{Capitalize(label)} skeleton hierarchy contains a cycle at bone {index}.");
                }

                chain.Add(index);
                var parent = skeleton.Bones[index].ParentIndex;
                if (parent < -1 || parent >= skeleton.Bones.Length || parent == index)
                {
                    throw new InvalidDataException(
                        $"{Capitalize(label)} skeleton bone {index} has invalid parent index {parent}.");
                }

                index = parent;
            }

            foreach (var boneIndex in chain)
                verified[boneIndex] = true;
        }
    }

    private static string Capitalize(string value) =>
        char.ToUpperInvariant(value[0]) + value[1..];
}
