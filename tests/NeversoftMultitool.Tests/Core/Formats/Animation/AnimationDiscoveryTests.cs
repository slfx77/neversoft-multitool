using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class AnimationDiscoveryTests(TestPaths paths)
{
    [CorpusFact]
    public void FindForCharacter_GbaSkater_RoutesExactClipSelectionBackToItsCharacter()
    {
        const string buildName = "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)";
        var romPath = paths.FindSampleFile(buildName, "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = Assert.Single(backend.Entries.Where(
            e => e.Name.EndsWith("13_spider_man.chr.gba", StringComparison.OrdinalIgnoreCase)));
        var source = new ArchiveAssetSource(backend, entry);

        var probes = AnimationDiscovery.FindForCharacter(
            source, skeletonBoneCount: 172, TestContext.Current.CancellationToken);

        // The four authored-empty clips are not offered; every other clip is.
        Assert.Equal(217, probes.Count);
        Assert.All(probes, probe =>
        {
            var clipSource = Assert.IsType<GbaAnimationSource>(probe.Source);
            Assert.Same(source, clipSource.ModelSource);
            Assert.Equal($"{entry.Name}::{clipSource.Label}", probe.ResolvedDisplayName);
            Assert.True(probe.MatchesSkeleton);
            Assert.Equal(172, probe.BoneCount);
            Assert.Equal(clipSource.TickCount / 60f, probe.DurationSec);
        });
        Assert.DoesNotContain(65, probes.Select(p => ((GbaAnimationSource)p.Source).ClipIndex));

        // The cart's own tricks.bin names the clips a single trick owns; the
        // rest keep the synthetic label.
        var labels = probes.ToDictionary(
            p => ((GbaAnimationSource)p.Source).ClipIndex,
            p => ((GbaAnimationSource)p.Source).Label);
        Assert.Equal("Kickflip", labels[20]);
        Assert.Equal("{The 900}", labels[181]);
        Assert.Equal("anim_136", labels[136]); // shared by BS Boardslide / FS Lipslide
        Assert.Equal(105, labels.Count(pair => !pair.Value.StartsWith("anim_", StringComparison.Ordinal)));

        // FrameCount is DISTINCT frames, so a clip that holds one pose for many
        // ticks is a single-frame pose the pane's filter can hide.
        Assert.Equal(51, probes.Count(static probe => probe.IsSinglePose));

        // Exact GUI-style selection: one checked row contributes only its clip.
        var selected = Assert.IsType<GbaAnimationSource>(probes[3].Source);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = selected.ModelSource,
            FileName = entry.Name,
            OutputStem = "13_spider_man",
            SourceKind = ModelSourceKind.GbaModel,
            GbaAnimationIndices = [selected.ClipIndex]
        });

        Assert.Equal($"anim_{selected.ClipIndex}", Assert.Single(document.Animations).Name);
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.NotNull(primitive.Skin));
    }

    [CorpusFact]
    public void FindForCharacter_N64DirectBank_RoutesExactEmbeddedSelectionBackToItsModel()
    {
        const string buildName = "Spider-Man (2000-11-21, N64 - Final)";
        var romPath = paths.FindSampleFile(buildName, "Spider-Man (USA).z64");
        Assert.SkipWhen(romPath == null, "Spider-Man N64 ROM sample not available");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "002");
        var source = new ArchiveAssetSource(backend, entry);
        var shell = PsxN64ShellFile.Parse(source.ReadBytes());
        Assert.NotNull(shell);
        Assert.Equal(16, shell!.Objects.Count);

        var probes = AnimationDiscovery.FindForCharacter(
            source,
            skeletonBoneCount: shell.Objects.Count,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, probes.Count);
        Assert.Equal(
            Enumerable.Range(0, 3).Select(index => $"{entry.Name}::anim_{index}"),
            probes.Select(static probe => probe.ResolvedDisplayName));
        for (var index = 0; index < probes.Count; index++)
        {
            var animationSource = Assert.IsType<N64AnimationSource>(probes[index].Source);
            Assert.Same(source, animationSource.ModelSource);
            Assert.Equal(index, animationSource.AnimationIndex);
            Assert.Equal(animationSource.FrameCount, probes[index].FrameCount);
            Assert.True(probes[index].MatchesSkeleton);
        }

        // Exact GUI-style selection: one checked row contributes only its
        // source slot, while companion lookup remains rooted at the shell.
        var selected = Assert.IsType<N64AnimationSource>(probes[2].Source);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = selected.ModelSource,
            FileName = entry.Name,
            OutputStem = "models_002",
            SourceKind = ModelSourceKind.N64Model,
            N64AnimationIndices = [selected.AnimationIndex]
        });

        var animation = Assert.Single(document.Animations);
        Assert.Equal("anim_2", animation.Name);
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.NotNull(primitive.Skin));
    }

    [CorpusFact]
    public void FindForCharacter_N64EligibleShell_RoutesExactEmbeddedSelectionBackToItsModel()
    {
        const string buildName = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
        var romPath = paths.FindSampleFile(buildName, "Tony Hawk's Pro Skater 2 (USA).z64");
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "045");
        var source = new ArchiveAssetSource(backend, entry);

        var probes = AnimationDiscovery.FindForCharacter(
            source,
            skeletonBoneCount: 19,
            TestContext.Current.CancellationToken);

        Assert.Equal(218, probes.Count);
        for (var index = 0; index < probes.Count; index++)
        {
            var animationSource = Assert.IsType<N64AnimationSource>(probes[index].Source);
            Assert.Same(source, animationSource.ModelSource);
            Assert.Equal(index, animationSource.AnimationIndex);
            Assert.True(animationSource.FrameCount > 0);
            Assert.Equal(animationSource.FrameCount, probes[index].FrameCount);
            Assert.True(probes[index].MatchesSkeleton);
        }

        // This is the core conversion request built by the Animations panel:
        // the selected embedded source contributes exactly its slot index, and
        // the original shell remains the mesh source used for companion lookup.
        var selected = Assert.IsType<N64AnimationSource>(probes[17].Source);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = selected.ModelSource,
            FileName = entry.Name,
            OutputStem = "models_045",
            SourceKind = ModelSourceKind.N64Model,
            N64AnimationIndices = [selected.AnimationIndex]
        });

        var animation = Assert.Single(document.Animations);
        Assert.Equal("anim_17", animation.Name);
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.NotNull(primitive.Skin));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
        "Tony Hawk's Pro Skater 2 (USA).z64", "046", 1)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)",
        "Spider-Man (USA).z64", "225", 6)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)",
        "Spider-Man (USA).z64", "108", 1)]
    public void FindForCharacter_N64ProvenBinding_RoutesExactSelectedSlot(
        string buildName,
        string romName,
        string slot,
        int expectedClips)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = N64Bundles.FindBundle(backend, slot);
        var source = new ArchiveAssetSource(backend, entry);
        var shell = PsxN64ShellFile.Parse(source.ReadBytes());
        Assert.NotNull(shell);

        var probes = AnimationDiscovery.FindForCharacter(
            source,
            shell!.Objects.Count,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedClips, probes.Count);
        var selected = Assert.IsType<N64AnimationSource>(probes[^1].Source);
        Assert.Same(source, selected.ModelSource);
        Assert.Equal(expectedClips - 1, selected.AnimationIndex);
        Assert.Equal(
            selected.FrameCount / PsxAnimationBank.DefaultPreviewFps,
            probes[^1].DurationSec,
            6);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = selected.ModelSource,
            FileName = entry.Name,
            OutputStem = $"models_{slot}",
            SourceKind = ModelSourceKind.N64Model,
            N64AnimationIndices = [selected.AnimationIndex]
        });

        var animation = Assert.Single(document.Animations);
        Assert.Equal($"anim_{expectedClips - 1}", animation.Name);
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.NotNull(primitive.Skin));
    }

    [Fact]
    public void AnimationProbe_ListMetadata_IsPopulatedForUnnamedClip()
    {
        var probe = new AnimationProbe(
            new FileSystemAssetSource("anim_7"),
            " \t",
            2.0f,
            15,
            true);

        // AnimationListEntry delegates these two UI-facing values directly to
        // the probe, so a nameless parsed clip still produces a visible row.
        Assert.Equal("anim_7", probe.ResolvedDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(probe.DurationDisplay));
        Assert.EndsWith(" s", probe.DurationDisplay, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0.033f, true)]
    [InlineData(0, 0f, true)]
    [InlineData(null, 0f, true)]
    [InlineData(40, 1.33f, false)]
    [InlineData(null, 2.0f, false)]
    public void AnimationProbe_IsSinglePose_FlagsOneFrameAndZeroDuration(
        int? frameCount, float durationSec, bool expected)
    {
        var probe = new AnimationProbe(
            new FileSystemAssetSource("anim_0"),
            "anim_0",
            durationSec,
            15,
            true,
            frameCount);

        Assert.Equal(expected, probe.IsSinglePose);
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
    public void SpideyEarlyStaticSlots_FromWad_AreAuthoredSingleFramePoses()
    {
        const string buildName = "Spider-Man (2000-9-1, PSX - Final)";
        var wadPath = paths.FindSampleFile(buildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man CD.WAD not found in sample builds");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = backend.FindEntry("spidey.psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry);
        var bytes = source.ReadBytes();
        var file = PsxMeshFile.Parse(bytes);
        Assert.NotNull(file);
        var animFile = PsxAnimFile.Parse(bytes, file.Objects.Count);
        Assert.NotNull(animFile);

        Assert.Equal(
            [40, 10, 1, 12, 1, 1, 1, 1, 1, 1, 1],
            animFile.Entries.Take(11).Select(static item => item.FrameCount));

        // The Animations pane's single-frame filter keys off these probes:
        // FrameCount flows through and one-frame pose slots flag IsSinglePose.
        var probes = PsxAnimationBank.CreateProbes(source, file.Objects.Count);
        Assert.Equal(animFile.Entries.Count, probes.Count);
        Assert.Equal(
            animFile.Entries.Select(static item => (int?)item.FrameCount),
            probes.Select(static probe => probe.FrameCount));
        Assert.False(probes[0].IsSinglePose);
        Assert.True(probes[2].IsSinglePose);

        foreach (var index in new[] { 2, 4, 5, 6, 7, 8, 9, 10 })
        {
            var animEntry = animFile.Entries[index];
            var animation = PsxAnimDecoder.Decode(
                animFile.Pool.Span[animEntry.PoolOffset..],
                file.Objects.Count,
                animEntry.FrameCount,
                out var consumed);

            Assert.Equal(1, animation.FrameCount);
            Assert.Equal(
                animFile.Entries[index + 1].PoolOffset - animEntry.PoolOffset,
                consumed);
            Assert.Contains(
                Enumerable.Range(0, animation.BoneCount),
                bone => animation.IsRotationAnimated(bone)
                        || animation.IsTranslationAnimated(bone));
        }
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

    [CorpusFact]
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
