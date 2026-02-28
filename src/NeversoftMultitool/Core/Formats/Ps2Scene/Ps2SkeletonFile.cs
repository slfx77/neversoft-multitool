using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parser for PS2 skeleton files (.ske.ps2).
///     Format from THUG source Gfx/Skeleton.cpp CSkeletonData::Load():
///     version(u32) + flags(u32) + numBones(i32) +
///     boneNameTable(N×u32) + parentNameTable(N×u32) + flipNameTable(N×u32) +
///     neutralPoses(N × (Quat(4×f32) + Vec(4×f32)))
///     Inverse bind matrices are computed at load time by walking the hierarchy.
///     THUG/THUG2 only — THPS4 has no .ske.ps2 files.
/// </summary>
public static class Ps2SkeletonFile
{
    public static Ps2Skeleton Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static Ps2Skeleton Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        var version = r.ReadInt32();
        if (version < 2)
            throw new InvalidDataException($"Unsupported SKE version {version} (need >= 2)");

        var flags = r.ReadInt32();
        var numBones = r.ReadInt32();
        if (numBones <= 0 || numBones > 256)
            throw new InvalidDataException($"Invalid bone count {numBones}");

        // Read the three name tables
        var boneNames = new uint[numBones];
        var parentNames = new uint[numBones];
        var flipNames = new uint[numBones];

        for (var i = 0; i < numBones; i++) boneNames[i] = r.ReadUInt32();
        for (var i = 0; i < numBones; i++) parentNames[i] = r.ReadUInt32();
        for (var i = 0; i < numBones; i++) flipNames[i] = r.ReadUInt32();

        // Build name → index lookup for parent resolution
        var nameToIndex = new Dictionary<uint, int>(numBones);
        for (var i = 0; i < numBones; i++)
            nameToIndex[boneNames[i]] = i;

        // Read neutral poses and compute inverse bind matrices
        // Replicates CSkeletonData::Load() lines 1375-1419
        var bones = new Ps2Bone[numBones];
        var inverseBindMatrices = new Matrix4x4[numBones];

        for (var i = 0; i < numBones; i++)
        {
            // Read quaternion (XYZW) — word at a time per source comment
            var qx = r.ReadSingle();
            var qy = r.ReadSingle();
            var qz = r.ReadSingle();
            var qw = r.ReadSingle();

            // Read translation vector (XYZW, W unused)
            var tx = r.ReadSingle();
            var ty = r.ReadSingle();
            var tz = r.ReadSingle();
            _ = r.ReadSingle(); // W (unused)

            // QuatVecToMatrix (quat.inl line 617) conjugates the quaternion first:
            // pQ->Invert() negates XYZ via Vector::Negate() (which only touches XYZ, not W).
            // This means the file stores q but the engine uses q* (conjugate) for the rotation.
            var quat = Quaternion.Conjugate(new Quaternion(qx, qy, qz, qw));
            var translation = new Vector3(tx, ty, tz);

            // QuatVecToMatrix: conjugated rotation + translation → 4×4 affine matrix
            var neutralPoseMatrix = Matrix4x4.CreateFromQuaternion(quat)
                                    * Matrix4x4.CreateTranslation(translation);

            // Resolve parent index
            var parentIndex = -1;
            if (i != 0 && parentNames[i] != 0 && nameToIndex.TryGetValue(parentNames[i], out var pi))
            {
                parentIndex = pi;

                // Replicate THUG logic: neutralPose *= inverse(parent.inverseBind)
                // i.e., neutralPose *= parent.forwardWorldPose
                Matrix4x4.Invert(inverseBindMatrices[parentIndex], out var parentForward);
                neutralPoseMatrix *= parentForward;

                // Clear W column (homogeneous cleanup)
                neutralPoseMatrix.M14 = 0f;
                neutralPoseMatrix.M24 = 0f;
                neutralPoseMatrix.M34 = 0f;
                neutralPoseMatrix.M44 = 1f;
            }

            // Invert to get inverse bind matrix
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

        return new Ps2Skeleton
        {
            Version = version,
            Flags = flags,
            Bones = bones
        };
    }

    /// <summary>
    ///     Check if data looks like a valid PS2 skeleton file.
    /// </summary>
    public static bool IsPs2Skeleton(byte[] data)
    {
        if (data.Length < 12) return false;
        var version = BitConverter.ToInt32(data, 0);
        if (version < 2 || version > 10) return false;
        var numBones = BitConverter.ToInt32(data, 8);
        if (numBones <= 0 || numBones > 256) return false;
        // Minimum size: 12 header + 3 name tables + poses
        var minSize = 12 + numBones * 12 + numBones * 32;
        return data.Length >= minSize;
    }
}
