using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class AnimationDiscoveryTests(TestPaths paths)
{
    [Fact]
    public void AnimationProbe_ListMetadata_IsPopulatedForUnnamedClip()
    {
        var probe = new AnimationProbe(
            new FileSystemAssetSource("anim_7"),
            " \t",
            2.0f,
            15,
            MatchesSkeleton: true);

        // AnimationListEntry delegates these two UI-facing values directly to
        // the probe, so a nameless parsed clip still produces a visible row.
        Assert.Equal("anim_7", probe.ResolvedDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(probe.DurationDisplay));
        Assert.EndsWith(" s", probe.DurationDisplay, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Apocalypse (1998-11-17, PSX - Final)", "bruce.psx", 19)]
    [InlineData("Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)", "hawk.psx", 63)]
    public void FindForCharacter_FromWad_DiscoversFlatSuperAnimations(
        string buildName,
        string characterName,
        int expectedEmbeddedAnimations)
    {
        var wadPath = paths.FindSampleFile(buildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, $"{buildName} CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry(characterName);
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry);
        var psxFile = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(psxFile);

        // These are the exact pre-HIER character layouts that the merged tab
        // used to reject before it ever called animation discovery.
        Assert.False(psxFile.HasHierarchy);
        Assert.True(psxFile.IsSuperModel);

        var probes = AnimationDiscovery.FindForCharacter(
            source,
            psxFile.Objects.Count,
            TestContext.Current.CancellationToken);
        var embedded = probes
            .Where(probe => probe.Source is PsxAnimationSource animationSource
                            && ReferenceEquals(animationSource.BankSource, source))
            .ToList();

        Assert.Equal(expectedEmbeddedAnimations, embedded.Count);
        Assert.All(embedded, probe =>
        {
            Assert.False(string.IsNullOrWhiteSpace(probe.DisplayName));
            Assert.True(probe.DurationSec > 0);
            Assert.True(probe.MatchesSkeleton);
        });

        // Exercise the same archive-backed document path used by the animated
        // preview. Flat supers already have all-root joints; the core writer
        // can skin and animate them without a HIER table.
        var firstProbe = embedded[0];
        var animationSource = Assert.IsType<PsxAnimationSource>(firstProbe.Source);
        var animation = animationSource.Decode();
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = characterName,
            OutputStem = Path.GetFileNameWithoutExtension(characterName),
            SourceKind = ModelSourceKind.Psx,
            PsxAnimationOptions = new PsxAnimationOptions(Fps: PsxAnimationBank.DefaultPreviewFps),
            PsxAnimationClips = [new PsxAnimationClip(firstProbe.DisplayName, animation)]
        });

        Assert.True(document.TriangleCount > 0);
        Assert.Single(document.Animations);
        Assert.Equal(firstProbe.DisplayName, document.Animations[0].Name);
    }

    [Theory]
    [InlineData("superock.psx", 45, 22, 17)]
    [InlineData("docock.psx", 46, 43, 18)]
    public void FindForCharacter_FromWad_DiscoversLargeArticulatedSuperAnimations(
        string characterName,
        int expectedBones,
        int expectedEmbeddedAnimations,
        int firstAppendageBone)
    {
        const string buildName = "Spider-Man (2000-9-1, PSX - Final)";
        var wadPath = paths.FindSampleFile(buildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry(characterName);
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry);
        var psxFile = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(psxFile);

        // These characters exceed the old 32-part heuristic because their
        // appendages consist of many separately transformed box segments.
        // The 0x2C animation chunk is the runtime's authoritative IsSuper
        // marker, so all parts use the super vertex scale and remain animatable.
        Assert.True(psxFile.HasHierarchy);
        Assert.True(psxFile.IsSuperModel);
        Assert.Equal(expectedBones, psxFile.Objects.Count);
        Assert.Equal(expectedBones, psxFile.Meshes.Count);
        Assert.Equal(psxFile.TranslationDivisor * 16f, psxFile.ScaleDivisor);

        var probes = AnimationDiscovery.FindForCharacter(
            source,
            expectedBones,
            TestContext.Current.CancellationToken);
        var embedded = probes
            .Where(probe => probe.Source is PsxAnimationSource animationSource
                            && ReferenceEquals(animationSource.BankSource, source))
            .ToList();

        Assert.Equal(expectedEmbeddedAnimations, embedded.Count);
        Assert.All(embedded, probe => Assert.True(probe.MatchesSkeleton));

        // At least one decoded clip must carry motion on the appendage segment
        // bones, not merely on the conventional humanoid prefix.
        PsxAnimation? appendageAnimation = null;
        foreach (var probe in embedded)
        {
            var decoded = Assert.IsType<PsxAnimationSource>(probe.Source).Decode();
            if (Enumerable.Range(firstAppendageBone, expectedBones - firstAppendageBone)
                .Any(decoded.IsTranslationAnimated))
            {
                appendageAnimation = decoded;
                break;
            }
        }

        Assert.NotNull(appendageAnimation);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = characterName,
            OutputStem = Path.GetFileNameWithoutExtension(characterName),
            SourceKind = ModelSourceKind.Psx,
            PsxAnimationOptions = new PsxAnimationOptions(Fps: PsxAnimationBank.DefaultPreviewFps),
            PsxAnimationClips = [new PsxAnimationClip("appendage_motion", appendageAnimation)]
        });

        var skeleton = Assert.Single(document.Skeletons);
        Assert.Equal(expectedBones, skeleton.Bones.Count);
        var animation = Assert.Single(document.Animations);
        Assert.Contains(animation.Channels, channel =>
            channel.BoneIndex >= firstAppendageBone
            && channel.Property == ModelAnimationProperty.Translation);
    }

    [Fact]
    public void ArchiveAssetSource_DisplayName_IncludesEntryDirectory()
    {
        const string buildName = "Apocalypse (1998-11-17, PSX - Final)";
        var wadPath = paths.FindSampleFile(buildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Apocalypse CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry("bruce.psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry);

        Assert.EndsWith($"::{entry.FullName}", source.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindForCharacter_Thps3NestedPre_DerivesUnknownClipBoneCounts()
    {
        const string buildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";
        var wadPath = paths.FindSampleFile(buildName, "SKATE3.WAD");
        Assert.SkipWhen(wadPath == null, "THPS3 SKATE3.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var rootFileSystem = backend.FileSystem;

        var cases = new[]
        {
            (Pre: "Foo.pre", Anim: "Anims/pedestrian_a/pedestrian_a_BBQGuyBrow.ska", Tracks: 14),
            (Pre: "Rio.pre", Anim: "Anims/Crowd_A/Crowd_A_CrowdClap.ska", Tracks: 11),
            (Pre: "SI.pre", Anim: "Anims/Bird_A/Bird_A_Flap.ska", Tracks: 3),
            (Pre: "Tok.pre", Anim: "Anims/Crowd_A/Crowd_A_CrowdClap.ska", Tracks: 11)
        };

        foreach (var testCase in cases)
        {
            var preEntry = backend.FindEntry(testCase.Pre);
            Assert.NotNull(preEntry);
            var nested = backend.TryOpenNested(preEntry);
            Assert.NotNull(nested);
            using var nestedFileSystem = nested.FileSystem;

            var meshEntry = nested.FindEntry("PedPro_Muska.skn");
            Assert.NotNull(meshEntry);
            var meshSource = new ArchiveAssetSource(nested, meshEntry);
            var skin = RwDffFile.Parse(meshSource.ReadBytes()).Atomics
                .Select(static atomic => atomic.SkinData)
                .First(static skinData => skinData != null)!;
            Assert.Equal(29, skin.NumBones);

            var animEntry = nested.FindByPath(testCase.Anim);
            Assert.NotNull(animEntry);
            var animBytes = nested.ReadEntryBytes(animEntry);
            var headerProbe = SkaFile.TryProbe(animBytes);
            Assert.NotNull(headerProbe);
            Assert.Null(headerProbe.BoneCount);
            Assert.Equal(testCase.Tracks, SkaFile.Parse(animBytes).BoneTracks.Length);

            var discovered = AnimationDiscovery.FindForCharacter(
                meshSource,
                skin.NumBones,
                TestContext.Current.CancellationToken);
            var discoveredProbe = Assert.Single(discovered, probe =>
                probe.Source is ArchiveAssetSource archiveSource
                && archiveSource.Entry.FullName.Equals(testCase.Anim, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(testCase.Tracks, discoveredProbe.BoneCount);
            Assert.False(discoveredProbe.MatchesSkeleton);
        }
    }
}
