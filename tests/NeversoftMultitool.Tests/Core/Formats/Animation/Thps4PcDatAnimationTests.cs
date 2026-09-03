using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class Thps4PcDatAnimationTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";

    [Theory]
    [InlineData("Walkska.dat", true)]
    [InlineData("DEFAULTSKA.DAT", true)]
    [InlineData("ska.dat", false)]
    [InlineData("Walk.ska.dat", false)]
    [InlineData("Walkska.ps2", false)]
    [InlineData("Walktex.dat", false)]
    public void CandidateName_IsStrictlyDelimiterFree(string fileName, bool expected)
    {
        Assert.Equal(expected, Thps4PcDatAnimationFile.IsCandidateFileName(fileName));
        Assert.Equal(expected, AnimationDiscovery.IsAnimFileName(fileName));
    }

    [Theory]
    [InlineData("Walkska.dat", "Walk")]
    [InlineData("DEFAULTSKA.DAT", "DEFAULT")]
    [InlineData("Walk.ska.ps2", "Walk")]
    public void CliAndGuiStem_StripsTheCompleteSuffix(string fileName, string expected)
    {
        Assert.Equal(expected, SkaCommand.GetOutputStem(fileName));
    }

    [Fact]
    public void ExactProbe_AcceptsCompleteContainerAndRejectsTrailingBytes()
    {
        var data = BuildEmptyCompressedClip();
        var probe = Assert.IsType<SkaProbeResult>(Thps4PcDatAnimationFile.TryProbeExact(data));
        Assert.Equal(1, probe.BoneCount);

        Array.Resize(ref data, data.Length + 1);
        Assert.Null(Thps4PcDatAnimationFile.TryProbeExact(data));
        Assert.Throws<InvalidDataException>(() => Thps4PcDatAnimationFile.ParseExact(data));
    }

    [Fact]
    public void Discovery_RequiresBothDelimiterFreeNameAndExactPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "thps4-pc-ska-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Walkska.dat"), BuildEmptyCompressedClip());
            var malformed = BuildEmptyCompressedClip();
            Array.Resize(ref malformed, malformed.Length + 1);
            File.WriteAllBytes(Path.Combine(root, "BadSka.dat"), malformed);
            File.WriteAllBytes(Path.Combine(root, "Walktex.dat"), BuildEmptyCompressedClip());

            var probe = Assert.Single(AnimationDiscovery.FindInDirectory(
                root, 1, CancellationToken.None));
            Assert.Equal("Walkska.dat", probe.DisplayName);
            Assert.True(probe.MatchesSkeleton);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [CorpusFact]
    public void Corpus_All1966FilesParseExactlyAndReachTheAnimationExportIr()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(Build, "*ska.dat")
            .Where(Thps4PcDatAnimationFile.IsCandidateFileName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(1_966, files.Length);

        var animPath = Assert.Single(files.Where(path =>
            path.Contains($"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}anims{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).Equals("defaultska.dat", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Path.GetDirectoryName(path)).Equals("skater_basics", StringComparison.OrdinalIgnoreCase)));
        var levelPath = files.First(path => path.Contains(
            $"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}levels{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase));
        var table = Assert.IsType<SkaCompressTable>(SkaCommand.FindCompressTable(animPath));
        Assert.NotNull(SkaCommand.FindCompressTable(levelPath));

        long bones = 0;
        long qKeys = 0;
        long tKeys = 0;
        long customKeys = 0;
        var flags = new Dictionary<uint, int>();

        foreach (var path in files)
        {
            var data = File.ReadAllBytes(path);
            Assert.NotNull(Thps4PcDatAnimationFile.TryProbeExact(data));
            var animation = Thps4PcDatAnimationFile.ParseExact(data, table);
            Assert.Equal(1u, animation.Version);

            flags[animation.Flags] = flags.GetValueOrDefault(animation.Flags) + 1;
            bones += animation.BoneTracks.Length;
            qKeys += animation.BoneTracks.Sum(static track => track.RotationKeys.Length);
            tKeys += animation.BoneTracks.Sum(static track => track.TranslationKeys.Length);
            customKeys += animation.CustomKeys.Length;

            Assert.All(animation.BoneTracks, static track =>
            {
                Assert.All(track.RotationKeys, static key =>
                {
                    Assert.True(float.IsFinite(key.Time));
                    Assert.True(float.IsFinite(key.Rotation.X));
                    Assert.True(float.IsFinite(key.Rotation.Y));
                    Assert.True(float.IsFinite(key.Rotation.Z));
                    Assert.True(float.IsFinite(key.Rotation.W));
                });
                Assert.All(track.TranslationKeys, static key =>
                {
                    Assert.True(float.IsFinite(key.Time));
                    Assert.True(float.IsFinite(key.Translation.X));
                    Assert.True(float.IsFinite(key.Translation.Y));
                    Assert.True(float.IsFinite(key.Translation.Z));
                });
            });

            var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
                CreateIdentitySkeleton(animation.BoneTracks.Length),
                [(SkaCommand.GetOutputStem(path), animation)]);
            Assert.Single(document.Skeletons);
            Assert.Equal(animation.BoneTracks.Length, document.Skeletons[0].Bones.Count);
        }

        Assert.Equal(72_368, bones);
        Assert.Equal(599_567, qKeys);
        Assert.Equal(139_026, tKeys);
        Assert.Equal(1_096, customKeys);
        Assert.Equal(4, flags.Count);
        Assert.Equal(1_650, flags[0x06800000]);
        Assert.Equal(282, flags[0x1E000000]);
        Assert.Equal(33, flags[0x1E400000]);
        Assert.Equal(1, flags[0x17400000]);

        var skeleton = paths.FindSampleFile(Build, "human.ske");
        Assert.NotNull(skeleton);
        var defaultPose = SkaCommand.FindDefaultPoseFile(skeleton, levelPath);
        Assert.NotNull(defaultPose);
        Assert.Equal("defaultska.dat", Path.GetFileName(defaultPose), ignoreCase: true);
        Assert.Equal("skater_basics", Path.GetFileName(Path.GetDirectoryName(defaultPose)), ignoreCase: true);
    }

    private static byte[] BuildEmptyCompressedClip()
    {
        var data = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x06800000);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), 1f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        // Header key/custom counts, allocation sizes and the sole Q/T byte-size
        // entries remain zero. Their exact end is byte 40.
        return data;
    }

    private static Ps2Skeleton CreateIdentitySkeleton(int boneCount)
    {
        var bones = new Ps2Bone[boneCount];
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i] = new Ps2Bone
            {
                NameChecksum = checked((uint)i + 1),
                ParentChecksum = 0,
                FlipChecksum = 0,
                ParentIndex = -1,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = Vector3.Zero,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        return new Ps2Skeleton { Version = 1, Flags = 0, Bones = bones };
    }
}
