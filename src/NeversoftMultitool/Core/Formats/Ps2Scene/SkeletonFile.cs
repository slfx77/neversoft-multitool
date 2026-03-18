using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parser for cross-platform skeleton files (.ske).
///     Two sub-formats confirmed via binary analysis of 154 files:
///     THPS4: checksum(u32) + numBones(u32) + 3 name tables. Size = 8 + N×12. No neutral poses.
///     THUG/THUG2: checksum(u32) + version(u32) + flags(u32) + numBones(u32) + 3 name tables + neutral poses.
///     Size = 16 + N×44, with optional trailing null-terminated build timestamp string.
///     Unlike Ps2SkeletonFile (.ske.ps2) which starts directly with version,
///     the cross-platform format prepends a name checksum.
///     Discrimination: THUG/THUG2 have checksum=0x222756D5, version=2, flags=0 (constant).
///     THPS4 has per-skeleton checksum and numBones directly at offset 4.
/// </summary>
public static class SkeletonFile
{
    public static Ps2Skeleton Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static Ps2Skeleton Parse(byte[] data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("File too small for skeleton data");

        // Try THUG/THUG2 format first: checksum + version(2) + flags(0) + numBones + data [+ timestamp]
        if (data.Length >= 16 && TryParseThugFormat(data, out var skeleton))
            return skeleton;

        // Try THPS4 format: checksum + numBones + 3 name tables (no poses)
        if (TryParseThps4Format(data, out skeleton))
            return skeleton;

        throw new InvalidDataException(
            $"Unrecognized cross-platform skeleton format (size={data.Length})");
    }

    private static bool TryParseThps4Format(byte[] data, out Ps2Skeleton skeleton)
    {
        skeleton = null!;

        var numBones = BitConverter.ToInt32(data, 4);
        if (numBones <= 0 || numBones > 256)
            return false;

        var expectedSize = 8 + numBones * 12;
        if (data.Length != expectedSize)
            return false;

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        _ = r.ReadUInt32(); // checksum
        _ = r.ReadInt32(); // numBones (already read above)

        // Read name tables
        var boneNames = ReadUInt32Array(r, numBones);
        var parentNames = ReadUInt32Array(r, numBones);
        var flipNames = ReadUInt32Array(r, numBones);

        // Build hierarchy
        var nameToIndex = BuildNameIndex(boneNames);
        var bones = new Ps2Bone[numBones];

        for (var i = 0; i < numBones; i++)
        {
            var parentIndex = ResolveParent(i, parentNames[i], nameToIndex);
            bones[i] = new Ps2Bone
            {
                NameChecksum = boneNames[i],
                ParentChecksum = parentNames[i],
                FlipChecksum = flipNames[i],
                ParentIndex = parentIndex,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = Vector3.Zero,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        skeleton = new Ps2Skeleton { Version = 1, Flags = 0, Bones = bones };
        return true;
    }

    private static bool TryParseThugFormat(byte[] data, out Ps2Skeleton skeleton)
    {
        skeleton = null!;

        var version = BitConverter.ToInt32(data, 4);
        if (version < 2 || version > 10)
            return false;

        var flags = BitConverter.ToInt32(data, 8);
        var numBones = BitConverter.ToInt32(data, 12);
        if (numBones <= 0 || numBones > 256)
            return false;

        // Bone data size: 3 name tables (N×4 each) + neutral poses (N × (quat(16) + vec(16)))
        var boneDataSize = numBones * 12 + numBones * 32;
        var expectedMinSize = 16 + boneDataSize;

        // File may have trailing timestamp string — allow extra bytes
        if (data.Length < expectedMinSize)
            return false;

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        _ = r.ReadUInt32(); // checksum
        _ = r.ReadInt32(); // version
        _ = r.ReadInt32(); // flags
        _ = r.ReadInt32(); // numBones

        // Read name tables
        var boneNames = ReadUInt32Array(r, numBones);
        var parentNames = ReadUInt32Array(r, numBones);
        var flipNames = ReadUInt32Array(r, numBones);

        // Build hierarchy
        var nameToIndex = BuildNameIndex(boneNames);

        // Read neutral poses and compute inverse bind matrices
        // Same algorithm as Ps2SkeletonFile.Parse() lines 54-109
        var bones = new Ps2Bone[numBones];
        var inverseBindMatrices = new Matrix4x4[numBones];

        for (var i = 0; i < numBones; i++)
        {
            var qx = r.ReadSingle();
            var qy = r.ReadSingle();
            var qz = r.ReadSingle();
            var qw = r.ReadSingle();

            var tx = r.ReadSingle();
            var ty = r.ReadSingle();
            var tz = r.ReadSingle();
            _ = r.ReadSingle(); // W (unused)

            // QuatVecToMatrix conjugates the quaternion (see Ps2SkeletonFile.cs)
            var quat = Quaternion.Conjugate(new Quaternion(qx, qy, qz, qw));
            var translation = new Vector3(tx, ty, tz);

            var neutralPoseMatrix = Matrix4x4.CreateFromQuaternion(quat)
                                    * Matrix4x4.CreateTranslation(translation);

            var parentIndex = ResolveParent(i, parentNames[i], nameToIndex);

            if (parentIndex >= 0)
            {
                Matrix4x4.Invert(inverseBindMatrices[parentIndex], out var parentForward);
                neutralPoseMatrix *= parentForward;

                neutralPoseMatrix.M14 = 0f;
                neutralPoseMatrix.M24 = 0f;
                neutralPoseMatrix.M34 = 0f;
                neutralPoseMatrix.M44 = 1f;
            }

            Matrix4x4.Invert(neutralPoseMatrix, out var inverseBind);
            inverseBindMatrices[i] = inverseBind;

            bones[i] = new Ps2Bone
            {
                NameChecksum = boneNames[i],
                ParentChecksum = parentNames[i],
                FlipChecksum = flipNames[i],
                ParentIndex = parentIndex,
                LocalRotation = quat,
                LocalTranslation = translation,
                InverseBindMatrix = inverseBind
            };
        }

        // Remaining bytes (if any) are a null-terminated build timestamp string — ignored

        skeleton = new Ps2Skeleton { Version = version, Flags = flags, Bones = bones };
        return true;
    }

    private static uint[] ReadUInt32Array(BinaryReader r, int count)
    {
        var arr = new uint[count];
        for (var i = 0; i < count; i++)
            arr[i] = r.ReadUInt32();
        return arr;
    }

    private static Dictionary<uint, int> BuildNameIndex(uint[] boneNames)
    {
        var map = new Dictionary<uint, int>(boneNames.Length);
        for (var i = 0; i < boneNames.Length; i++)
            map[boneNames[i]] = i;
        return map;
    }

    private static int ResolveParent(int boneIndex, uint parentChecksum, Dictionary<uint, int> nameToIndex)
    {
        if (boneIndex == 0 || parentChecksum == 0)
            return -1;
        return nameToIndex.TryGetValue(parentChecksum, out var pi) ? pi : -1;
    }
}
