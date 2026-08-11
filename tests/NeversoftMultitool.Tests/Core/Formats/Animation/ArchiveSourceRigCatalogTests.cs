using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class ArchiveSourceRigCatalogTests(TestPaths paths)
{
    private const string ThawGcBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string Thug2Ps2Build =
        "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";

    [Theory]
    [InlineData("human.ske", true)]
    [InlineData("human.ske.ps2", true)]
    [InlineData("human.SKE.NGC", true)]
    [InlineData("rigs/human.ske.ngc", true)]
    [InlineData("human.ske.xbx", false)]
    [InlineData("human.ps2", false)]
    [InlineData("human.ske.ngc.tmp", false)]
    public void CandidatePolicy_IsExactAndDoesNotFollowXbxLoaderExpansion(
        string entryName,
        bool expected)
    {
        Assert.Equal(expected, ArchiveSourceRigCatalog.IsCandidateEntryName(entryName));
    }

    [Fact]
    public void PickerPolicy_ReachesLocalizedAndCompoundPlatformArchives()
    {
        var extensions = ArchiveSourceRigCatalog.PickerExtensions;

        Assert.Contains(".apk", extensions);
        Assert.Contains(".prd", extensions);
        Assert.Contains(".prf", extensions);
        Assert.Contains(".prg", extensions);
        Assert.Contains(".ps2", extensions);
        Assert.Contains(".ngc", extensions);
        Assert.Contains(".wpc", extensions);
        Assert.Contains(".xbx", extensions);
        Assert.DoesNotContain(".z64", extensions);
    }

    [Fact]
    public void Open_EnumeratesDirectAndNestedDuplicatesByFullVirtualIdentity()
    {
        var path = WriteArchive(BuildCompressedPreV3(
            ("zeta/duplicate.ske", BuildSkeletonBytes(0x100u)),
            ("inner.pre", BuildCompressedPreV3(
                ("alpha/duplicate.ske", BuildSkeletonBytes(0x200u)),
                ("ignored.ske.xbx", BuildSkeletonBytes(0x300u)))),
            ("not-a-rig.ps2", BuildSkeletonBytes(0x400u))));

        try
        {
            using var catalog = ArchiveSourceRigCatalog.Open(
                path, TestContext.Current.CancellationToken);

            Assert.Equal(2, catalog.Candidates.Count);
            var displays = catalog.Candidates.Select(candidate => candidate.DisplayName).ToArray();
            Assert.Equal(
                displays
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name, StringComparer.Ordinal),
                displays);
            Assert.All(displays, display => Assert.Contains("::", display));
            Assert.NotEqual(displays[0], displays[1]);
            Assert.NotSame(
                catalog.Candidates[0].Source.Backend,
                catalog.Candidates[1].Source.Backend);

            var checksums = catalog.Candidates
                .Select(candidate => SkaAnimationSourceRig.Load(candidate.Source)
                    .Skeleton.Bones.Single().NameChecksum)
                .Order()
                .ToArray();
            Assert.Equal([0x100u, 0x200u], checksums);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_MeshPolicyAdmitsDirectAndNestedXbxDuplicatesWithoutWideningAnimation()
    {
        var path = WriteArchive(BuildCompressedPreV3(
            ("root/duplicate.ske.xbx", BuildSkeletonBytes(0x501u)),
            ("inner.pre", BuildCompressedPreV3(
                ("nested/duplicate.ske.xbx", BuildSkeletonBytes(0x502u)),
                ("nested/not-a-rig.xbx", BuildSkeletonBytes(0x503u))))));

        try
        {
            using (var animationCatalog = ArchiveSourceRigCatalog.Open(
                       path, TestContext.Current.CancellationToken))
            {
                Assert.Empty(animationCatalog.Candidates);
            }

            Ps2Skeleton[] parsed;
            using (var meshCatalog = ArchiveSourceRigCatalog.Open(
                       path,
                       SkeletonAssetLoader.IsSkeletonFileName,
                       TestContext.Current.CancellationToken))
            {
                Assert.Equal(2, meshCatalog.Candidates.Count);
                Assert.All(meshCatalog.Candidates, candidate =>
                    Assert.Equal("duplicate.ske.xbx", candidate.Source.EntryName));
                Assert.NotSame(
                    meshCatalog.Candidates[0].Source.Backend,
                    meshCatalog.Candidates[1].Source.Backend);

                var displays = meshCatalog.Candidates
                    .Select(static candidate => candidate.DisplayName)
                    .ToArray();
                Assert.Contains(displays, static display =>
                    display.EndsWith(
                        "::root/duplicate.ske.xbx",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Contains(displays, static display =>
                    display.EndsWith(
                        "::inner.pre::nested/duplicate.ske.xbx",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Equal(2, displays.Distinct(StringComparer.OrdinalIgnoreCase).Count());

                parsed = meshCatalog.Candidates
                    .Select(static candidate => SkeletonAssetLoader.Load(candidate.Source))
                    .ToArray();
            }

            // Parsed skeletons carry no backend dependency; deleting the root
            // additionally proves both direct and nested handles were released.
            Assert.Equal(
                [0x501u, 0x502u],
                parsed.Select(static skeleton =>
                        Assert.Single(skeleton.Bones).NameChecksum)
                    .Order()
                    .ToArray());
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ParsedRig_RemainsUsableForBindingAfterCatalogDisposal()
    {
        var path = WriteArchive(BuildCompressedPreV3(
            ("rigs/source.ske", BuildSkeletonBytes(0x1234u))));

        try
        {
            SkaAnimationSourceRig parsed;
            using (var catalog = ArchiveSourceRigCatalog.Open(
                       path, TestContext.Current.CancellationToken))
            {
                parsed = SkaAnimationSourceRig.Load(Assert.Single(catalog.Candidates).Source);
            }

            var target = new Ps2Skeleton
            {
                Version = 2,
                Flags = 0,
                Bones =
                [
                    new Ps2Bone
                    {
                        NameChecksum = 0x1234u,
                        ParentChecksum = 0,
                        FlipChecksum = 0x1234u,
                        ParentIndex = -1,
                        LocalRotation = Quaternion.Identity,
                        LocalTranslation = Vector3.Zero,
                        InverseBindMatrix = Matrix4x4.Identity
                    }
                ]
            };
            var plan = SkaAnimationBindingPlan.Create(target, parsed);

            Assert.Equal(1, plan.ExpectedTrackCount);
            Assert.Equal([0], plan.BoneMap!.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [CorpusFact]
    public void RealThawGcCompoundArchive_RigMapsAfterCatalogDisposal()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var archivePath = paths.FindSampleFile(ThawGcBuild, "global_s.apk.ngc");
        var targetPath = paths.FindSampleFile(Thug2Ps2Build, "thps6_human.ske.ps2");
        Assert.SkipWhen(archivePath == null || targetPath == null,
            "THAW GC global archive or THUG2 target skeleton is unavailable");

        SkaAnimationSourceRig sourceRig;
        using (var catalog = ArchiveSourceRigCatalog.Open(
                   archivePath!, TestContext.Current.CancellationToken))
        {
            var candidate = Assert.Single(catalog.Candidates, item =>
                item.Source.EntryName.Equals(
                    "thps7_human.ske.ngc", StringComparison.OrdinalIgnoreCase));
            sourceRig = SkaAnimationSourceRig.Load(candidate.Source);
            Assert.Equal(52, sourceRig.BoneCount);
        }

        var target = SkeletonAssetLoader.Load(new FileSystemAssetSource(targetPath!));
        var plan = SkaAnimationBindingPlan.Create(target, sourceRig);

        Assert.Equal(50, target.Bones.Length);
        Assert.Equal(52, plan.ExpectedTrackCount);
        Assert.Equal(48, plan.BoneMap!.MappedBoneCount);
    }

    [Fact]
    public void CancelledOrMalformedSelection_CatalogScopeReleasesRootHandle()
    {
        var path = WriteArchive(BuildCompressedPreV3(
            ("rigs/bad.ske", [1, 2, 3, 4])));

        static SkaAnimationSourceRig? Cancel(
            string archivePath,
            CancellationToken cancellationToken)
        {
            using var catalog = ArchiveSourceRigCatalog.Open(archivePath, cancellationToken);
            Assert.Single(catalog.Candidates);
            return null;
        }

        static void RejectMalformed(
            string archivePath,
            CancellationToken cancellationToken)
        {
            using var catalog = ArchiveSourceRigCatalog.Open(archivePath, cancellationToken);
            _ = SkaAnimationSourceRig.Load(Assert.Single(catalog.Candidates).Source);
        }

        try
        {
            var token = TestContext.Current.CancellationToken;
            Assert.Null(Cancel(path, token));
            Assert.ThrowsAny<Exception>(() => RejectMalformed(path, token));

            // FileArchiveFileSystem opens without FileShare.Delete. Successful
            // deletion therefore proves both early-exit scopes released root.
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string WriteArchive(byte[] data)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"NsMultitool_SourceRig_{Guid.NewGuid():N}.prx");
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>PRE v3 (0xABCD0003) with uncompressed entries.</summary>
    private static byte[] BuildCompressedPreV3(params (string Name, byte[] Data)[] files)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(0xABCD0003u);
        writer.Write(files.Length);

        foreach (var (name, data) in files)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
            writer.Write(data.Length);
            writer.Write(0);
            writer.Write((short)nameBytes.Length);
            writer.Write((short)0);
            writer.Write(0u);
            writer.Write(nameBytes);
            writer.Write(data);
            writer.Write(new byte[(4 - data.Length % 4) % 4]);
        }

        var bytes = stream.ToArray();
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] BuildSkeletonBytes(uint nameChecksum)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x12345678u);
        writer.Write(1);
        writer.Write(nameChecksum);
        writer.Write(0u);
        writer.Write(nameChecksum);
        return stream.ToArray();
    }
}
