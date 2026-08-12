using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

/// <summary>
///     Parser for THAW-generation .ske skeletons — little-endian on PS2/PC
///     (bare .ske inside paks) and big-endian on GameCube (.ske.ngc). The two
///     are field-for-field endian mirrors; layout established via PS2↔GC
///     Rosetta pairs and validated structurally on all 330 pairs:
///     <code>
///     0x00  u16 version = 1, u16 headerSize = 0x30
///     0x04  u32 numBones
///     0x08  u32[2] zero
///     0x10  u32 offsets[6]: boneNames, parentNames, flipNames, flipIndices,
///                           flags (all 0 in corpus), 128-byte 0x7F7FC99E block
///     0x28  u32 matrixOffset (= 0x30 + N×16)
///     0x2C  u32 vecOffset    (= 0x30)
///     0x30  vec4[N]  local translation xyz + local rotation quaternion W
///           mat4[N]  inverse bind matrices @matrixOffset (row-vector,
///                    translation in row 3, last column (0,0,0,1))
///           u32[N] × 5 name/parent/flip/flipIdx/flags arrays @offsets[0..4]
///     </code>
///     Unlike THUG (which stores quat+trans neutral poses and computes inverse
///     bind matrices at load), THAW ships the IBMs precomputed; local
///     transforms are derived here as local = inverse(IBM_bone) × IBM_parent
///     (verified exact against the stored vec4 translations and quaternion W).
/// </summary>
public static class ThawSkeletonFile
{
    private const int HeaderSize = 0x30;
    private const int MaxBones = 256;

    /// <summary>Strict structural gate; detects either endianness.</summary>
    public static bool IsThawSkeleton(byte[] data)
    {
        return TryDetectEndian(data, out _);
    }

    private static bool TryDetectEndian(byte[] data, out bool bigEndian)
    {
        foreach (var big in (ReadOnlySpan<bool>)[false, true])
        {
            if (IsValid(new EndianSpanReader(data, big)))
            {
                bigEndian = big;
                return true;
            }
        }

        bigEndian = false;
        return false;
    }

    private static bool IsValid(EndianSpanReader r)
    {
        if (r.Length < HeaderSize + 16 + 64 + 4 * 6)
            return false;
        if (r.U16(0) != 1 || r.U16(2) != HeaderSize)
            return false;

        var numBones = r.U32(4);
        if (numBones == 0 || numBones > MaxBones)
            return false;
        if (r.U32(8) != 0 || r.U32(12) != 0)
            return false;

        // matrixOffset directly follows the vec4 block; vecOffset is the header end.
        if (r.U32(0x28) != HeaderSize + numBones * 16 || r.U32(0x2C) != HeaderSize)
            return false;

        // Six ascending in-bounds arrays; the first starts right after the matrices.
        var prev = 0u;
        for (var i = 0; i < 6; i++)
        {
            var off = r.U32(0x10 + i * 4);
            if (off <= prev || off >= r.Length)
                return false;
            prev = off;
        }

        // offsets[0..4] are u32-per-bone arrays; offsets[5] is a fixed 128-byte
        // block (0x7F7FC99E fill, constant across the corpus) ending the file.
        if (r.U32(0x10) != HeaderSize + numBones * 16 + numBones * 64)
            return false;

        for (var i = 0; i < 5; i++)
        {
            if ((long)r.U32(0x10 + i * 4) + numBones * 4 > r.U32(0x14 + i * 4))
                return false;
        }

        return (long)r.U32(0x24) + 128 <= r.Length;
    }

    public static Ps2Skeleton Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static Ps2Skeleton Parse(byte[] data)
    {
        if (!TryDetectEndian(data, out var bigEndian))
            throw new InvalidDataException("Not a THAW skeleton (header gate failed)");

        var r = new EndianSpanReader(data, bigEndian);
        var numBones = (int)r.U32(4);
        var namesOff = (int)r.U32(0x10);
        var parentsOff = (int)r.U32(0x14);
        var flipsOff = (int)r.U32(0x18);
        var matrixOff = (int)r.U32(0x28);

        var boneNames = new uint[numBones];
        var nameToIndex = new Dictionary<uint, int>(numBones);
        for (var i = 0; i < numBones; i++)
        {
            boneNames[i] = r.U32(namesOff + i * 4);
            nameToIndex[boneNames[i]] = i;
        }

        var inverseBinds = new Matrix4x4[numBones];
        for (var i = 0; i < numBones; i++)
        {
            var b = matrixOff + i * 64;
            inverseBinds[i] = new Matrix4x4(
                r.F32(b), r.F32(b + 4), r.F32(b + 8), r.F32(b + 12),
                r.F32(b + 16), r.F32(b + 20), r.F32(b + 24), r.F32(b + 28),
                r.F32(b + 32), r.F32(b + 36), r.F32(b + 40), r.F32(b + 44),
                r.F32(b + 48), r.F32(b + 52), r.F32(b + 56), r.F32(b + 60));
        }

        var bones = new Ps2Bone[numBones];
        for (var i = 0; i < numBones; i++)
        {
            var parentName = r.U32(parentsOff + i * 4);
            var parentIndex = parentName != 0 && nameToIndex.TryGetValue(parentName, out var pi) && pi != i
                ? pi
                : -1;

            // Under row-vector convention, derive a child's local transform by
            // multiplying its world pose by the parent's inverse bind matrix.
            // A root's local transform is its world pose.
            Matrix4x4.Invert(inverseBinds[i], out var world);
            var local = parentIndex >= 0 ? world * inverseBinds[parentIndex] : world;

            bones[i] = new Ps2Bone
            {
                NameChecksum = boneNames[i],
                ParentChecksum = parentName,
                FlipChecksum = r.U32(flipsOff + i * 4),
                ParentIndex = parentIndex,
                LocalRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local)),
                LocalTranslation = local.Translation,
                InverseBindMatrix = inverseBinds[i]
            };
        }

        return new Ps2Skeleton
        {
            // The file's own version field is 1, but Ps2Skeleton.Version uses
            // the THUG scheme where 1 means "THPS4, no bind pose" (triggering
            // default-anim enrichment and identity-pose fallbacks). THAW
            // skeletons carry full bind poses, so report the "has neutral
            // poses" tier.
            Version = 2,
            Flags = 0,
            Bones = bones
        };
    }
}
