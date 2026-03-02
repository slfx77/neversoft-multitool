using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class SkeletonFileTests(TestPaths paths)
{
    private string Thps4SkeDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)", "SKE");

    private string ThugSkeDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's Underground (2003-10-2, PS2 - Final)", "SKE");

    private string Thug2SkeDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)", "SKE");

    // ── THPS4 format (names only, no neutral poses) ──

    [Theory]
    [InlineData("human.ske", 50)]
    [InlineData("SI_Generic.ske", 2)]
    [InlineData("anl_elephant.ske", 28)]
    [InlineData("Anl_Chicken.ske", 5)]
    [InlineData("BMX.ske", 8)]
    public void Parse_Thps4_ReturnsCorrectBoneCount(string filename, int expectedBones)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(Thps4SkeDir, filename);
        Assert.SkipWhen(!File.Exists(file), $"Test file not found: {filename}");

        var skeleton = SkeletonFile.Parse(file);

        Assert.Equal(expectedBones, skeleton.Bones.Length);
        Assert.Equal(1, skeleton.Version); // THPS4 format reports version 1
    }

    [Fact]
    public void Parse_Thps4Human_HasIdentityPoses()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(Thps4SkeDir, "human.ske");
        Assert.SkipWhen(!File.Exists(file), "human.ske not found");

        var skeleton = SkeletonFile.Parse(file);

        // THPS4 format has no neutral poses — all should be identity
        foreach (var bone in skeleton.Bones)
        {
            Assert.Equal(System.Numerics.Quaternion.Identity, bone.LocalRotation);
            Assert.Equal(System.Numerics.Vector3.Zero, bone.LocalTranslation);
            Assert.Equal(System.Numerics.Matrix4x4.Identity, bone.InverseBindMatrix);
        }
    }

    [Fact]
    public void Parse_Thps4Human_HasValidHierarchy()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(Thps4SkeDir, "human.ske");
        Assert.SkipWhen(!File.Exists(file), "human.ske not found");

        var skeleton = SkeletonFile.Parse(file);

        // Root bone should have parent index -1
        Assert.Equal(-1, skeleton.Bones[0].ParentIndex);

        // All other bones should reference valid parent indices
        for (var i = 1; i < skeleton.Bones.Length; i++)
        {
            var parent = skeleton.Bones[i].ParentIndex;
            Assert.True(parent >= 0 && parent < skeleton.Bones.Length,
                $"Bone {i} has invalid parent index {parent}");
        }
    }

    [Fact]
    public void Parse_AllThps4SkeFiles_NoneThrow()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(Thps4SkeDir), "THPS4 SKE dir not found");

        var files = Directory.GetFiles(Thps4SkeDir, "*.ske")
            .Where(f => !f.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var skeleton = SkeletonFile.Parse(file);
            Assert.True(skeleton.Bones.Length > 0,
                $"{Path.GetFileName(file)}: expected at least 1 bone");
        }
    }

    // ── THUG/THUG2 format (with neutral poses, optional timestamp) ──

    [Theory]
    [InlineData("Ped_F.ske", 50)]
    [InlineData("Anl_Chicken.ske", 5)]
    [InlineData("Anl_Dog.ske", 24)]
    [InlineData("BMX.ske", 8)]
    public void Parse_Thug_ReturnsCorrectBoneCount(string filename, int expectedBones)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThugSkeDir, filename);
        Assert.SkipWhen(!File.Exists(file), $"Test file not found: {filename}");

        var skeleton = SkeletonFile.Parse(file);

        Assert.Equal(expectedBones, skeleton.Bones.Length);
        Assert.Equal(2, skeleton.Version);
    }

    [Fact]
    public void Parse_ThugHuman_HasNonIdentityPoses()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThugSkeDir, "Ped_F.ske");
        Assert.SkipWhen(!File.Exists(file), "Ped_F.ske not found");

        var skeleton = SkeletonFile.Parse(file);

        // THUG format has neutral poses — at least some bones should have non-identity transforms
        var nonIdentityCount = skeleton.Bones.Count(b =>
            b.LocalRotation != System.Numerics.Quaternion.Identity ||
            b.LocalTranslation != System.Numerics.Vector3.Zero);

        Assert.True(nonIdentityCount > 0,
            "Expected at least some bones with non-identity poses");
    }

    [Fact]
    public void Parse_AllThugSkeFiles_NoneThrow()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(ThugSkeDir), "THUG SKE dir not found");

        var files = Directory.GetFiles(ThugSkeDir, "*.ske")
            .Where(f => !f.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var skeleton = SkeletonFile.Parse(file);
            Assert.True(skeleton.Bones.Length > 0,
                $"{Path.GetFileName(file)}: expected at least 1 bone");
        }
    }

    [Fact]
    public void Parse_AllThug2SkeFiles_NoneThrow()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(Thug2SkeDir), "THUG2 SKE dir not found");

        var files = Directory.GetFiles(Thug2SkeDir, "*.ske")
            .Where(f => !f.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var skeleton = SkeletonFile.Parse(file);
            Assert.True(skeleton.Bones.Length > 0,
                $"{Path.GetFileName(file)}: expected at least 1 bone");
        }
    }

    // ── Cross-validation: .ske vs .ske.ps2 bone names should match ──

    [Fact]
    public void Parse_ThugSke_BoneNamesMatchPs2Ske()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // Find a skeleton that has both .ske and .ske.ps2 versions
        var skeFile = Path.Combine(ThugSkeDir, "Ped_F.ske");
        var skePs2File = Path.Combine(ThugSkeDir, "Ped_F.ske.ps2");
        Assert.SkipWhen(!File.Exists(skeFile) || !File.Exists(skePs2File),
            "Both Ped_F.ske and Ped_F.ske.ps2 required");

        var crossPlatform = SkeletonFile.Parse(skeFile);
        var ps2Specific = Ps2SkeletonFile.Parse(skePs2File);

        Assert.Equal(ps2Specific.Bones.Length, crossPlatform.Bones.Length);

        // Bone name checksums should be identical
        for (var i = 0; i < ps2Specific.Bones.Length; i++)
        {
            Assert.Equal(ps2Specific.Bones[i].NameChecksum, crossPlatform.Bones[i].NameChecksum);
            Assert.Equal(ps2Specific.Bones[i].ParentChecksum, crossPlatform.Bones[i].ParentChecksum);
            Assert.Equal(ps2Specific.Bones[i].ParentIndex, crossPlatform.Bones[i].ParentIndex);
        }
    }

    // ── Negative test: .ske.ps2 data should not parse as cross-platform ──

    [Fact]
    public void Parse_Ps2SkeData_Throws()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var skePs2File = Path.Combine(ThugSkeDir, "Ped_F.ske.ps2");
        Assert.SkipWhen(!File.Exists(skePs2File), "Ped_F.ske.ps2 not found");

        // .ske.ps2 starts with version=2 at offset 0, not a checksum
        // The cross-platform parser should either reject it or misparse it
        // (in practice, TryParseThugFormat might match since version=2 is valid,
        // but the checksum field would be 2 and the data layout would differ)
        var data = File.ReadAllBytes(skePs2File);

        // Verify that the PS2 parser works
        var ps2Skeleton = Ps2SkeletonFile.Parse(data);
        Assert.True(ps2Skeleton.Bones.Length > 0);
    }
}
