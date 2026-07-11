using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Regression tests for the cutscene payloads extracted from .cut libraries:
///     platform SKA with OBJECTANIMDATA (object/camera anims) and the headerless
///     THUG cutscene .ske. Both are synthetic so they need no sample data.
/// </summary>
public class CutsceneAnimGateTests
{
    private const uint FlagPlatform = 1u << 28;
    private const uint FlagObjectAnimData = 1u << 24;

    [Fact]
    public void ParsePlatform_ObjectAnimData_SkipsBoneNameArrayAndAttachesChecksums()
    {
        // 2 bones, one Q key on bone 0, one T key on bone 1. Without skipping the
        // OBJECTANIMDATA bone-name array the per-bone counts would read 8 bytes early
        // and the key totals would not match the header.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1u);                       // version
        w.Write(FlagPlatform | FlagObjectAnimData); // flags
        w.Write(0f);                       // duration
        w.Write(2u);                       // numBones
        w.Write(1u);                       // numQKeys
        w.Write(1u);                       // numTKeys
        w.Write(0u);                       // numCustomKeys
        w.Write(0xAAAA0001u);              // bone-name QbKey [0]
        w.Write(0xBBBB0002u);              // bone-name QbKey [1]
        w.Write((byte)1); w.Write((byte)0); // bone 0: 1 Q, 0 T
        w.Write((byte)0); w.Write((byte)1); // bone 1: 0 Q, 1 T
        // per-bone frames = 4 bytes, already 4-aligned
        // Q key (standard, 8 bytes): header + 3×i16
        w.Write((ushort)0); w.Write((short)0); w.Write((short)0); w.Write((short)0);
        // T key (standard, 8 bytes): timestamp + 3×i16
        w.Write((short)0); w.Write((short)32); w.Write((short)0); w.Write((short)0);

        var anim = SkaFile.Parse(ms.ToArray());

        Assert.Equal(2, anim.BoneTracks.Length);
        Assert.Single(anim.BoneTracks[0].RotationKeys);
        Assert.Empty(anim.BoneTracks[0].TranslationKeys);
        Assert.Empty(anim.BoneTracks[1].RotationKeys);
        Assert.Single(anim.BoneTracks[1].TranslationKeys);
        Assert.Equal(0xAAAA0001u, anim.BoneTracks[0].BoneNameChecksum);
        Assert.Equal(0xBBBB0002u, anim.BoneTracks[1].BoneNameChecksum);
        // T key wrote 32 into the tx slot (32/32 = 1); landing it here proves the
        // OBJECTANIMDATA bone-name array was skipped before the per-bone frames.
        Assert.Equal(new Vector3(1, 0, 0), anim.BoneTracks[1].TranslationKeys[0].Translation);
    }

    [Fact]
    public void SkeletonFile_HeaderlessCutsceneSke_ParsesLikeStandaloneMinusChecksum()
    {
        // Cutscene .ske: version(2) + flags(0) + numBones + 3 name tables + poses,
        // i.e. the standalone THUG layout with the leading checksum dropped.
        const int numBones = 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(2);          // version
        w.Write(0);          // flags
        w.Write(numBones);   // numBones
        w.Write(0x1111u); w.Write(0x2222u);   // bone names
        w.Write(0u); w.Write(0x1111u);        // parents (bone 1's parent = bone 0)
        w.Write(0u); w.Write(0u);             // flip names
        for (var i = 0; i < numBones; i++)
        {
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); // identity quat
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); // zero translation + w
        }

        var data = ms.ToArray();
        Assert.Equal(12 + numBones * 44, data.Length);

        var skeleton = SkeletonFile.Parse(data);
        Assert.Equal(numBones, skeleton.Bones.Length);
        Assert.Equal(0x1111u, skeleton.Bones[0].NameChecksum);
        Assert.Equal(0x2222u, skeleton.Bones[1].NameChecksum);
        Assert.Equal(-1, skeleton.Bones[0].ParentIndex);
        Assert.Equal(0, skeleton.Bones[1].ParentIndex);
    }

    [Fact]
    public void SkeletonFile_HeaderlessGate_DoesNotShadowStandaloneOrReject()
    {
        // A standalone THUG .ske (leading checksum != 2) must still parse via the
        // checksum-first path, not the cutscene gate.
        const int numBones = 1;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x222756D5u); // constant THUG checksum
        w.Write(2);           // version
        w.Write(0);           // flags
        w.Write(numBones);    // numBones
        w.Write(0x3333u);     // bone name
        w.Write(0u);          // parent
        w.Write(0u);          // flip
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f);

        var skeleton = SkeletonFile.Parse(ms.ToArray());
        Assert.Single(skeleton.Bones);
        Assert.Equal(0x3333u, skeleton.Bones[0].NameChecksum);
    }
}
