using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class PsxAnimationBankTests(TestPaths paths)
{
    private const string Thps2ProtoBuild = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";

    [Fact]
    public void ResolveSelections_WithPrefix_AvoidsDuplicateAnimNames()
    {
        var bank = ParseSyntheticBank();

        var selected = PsxAnimationBank.ResolveSelections(
            bank.AnimFile,
            -1,
            null,
            "sk2anim");

        var selection = Assert.Single(selected);
        Assert.Equal(0, selection.Index);
        Assert.Equal("sk2anim_anim_0", selection.Name);
    }

    [Fact]
    public void ResolveSelections_WithoutPrefix_UsesCustomSingleName()
    {
        var bank = ParseSyntheticBank();

        var selected = PsxAnimationBank.ResolveSelections(
            bank.AnimFile,
            0,
            "idle",
            null);

        var selection = Assert.Single(selected);
        Assert.Equal(0, selection.Index);
        Assert.Equal("idle", selection.Name);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void ResolveSelections_NegativeIndexBelowAllSentinel_IsRejected(int animIndex)
    {
        var bank = ParseSyntheticBank();

        var selected = PsxAnimationBank.ResolveSelections(
            bank.AnimFile,
            animIndex,
            null,
            null);

        Assert.Empty(selected);
    }

    [Fact]
    public void PsxAnimationSource_DecodesFromGenericAssetSource()
    {
        var source = new InMemoryAssetSource("memory_bank.psx", BuildMinimalDirectMatrixPsx());
        var probes = PsxAnimationBank.CreateProbes(source, 1);
        var probe = Assert.Single(probes);
        var psxSource = Assert.IsType<PsxAnimationSource>(probe.Source);

        Assert.Null(psxSource.FileSystemPath);

        var animation = psxSource.Decode();

        Assert.Equal(1, animation.BoneCount);
        Assert.Equal(1, animation.FrameCount);
    }

    [Fact]
    public void Decode_BoneCountMismatch_ReturnsDiagnosticWithoutDecoding()
    {
        var source = new InMemoryAssetSource("memory_bank.psx", BuildMinimalDirectMatrixPsx());
        var bank = PsxAnimationBank.TryProbe(source, 2);
        Assert.NotNull(bank);
        Assert.False(bank.MatchesTargetBoneCount);

        var selected = PsxAnimationBank.ResolveSelections(
            bank.AnimFile,
            -1,
            null,
            null);
        var result = PsxAnimationBank.Decode(bank, 2, selected);

        Assert.Empty(result.Animations);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("bank has 1 bones", diagnostic.Error);
    }

    [Fact]
    public void BoneMap_UsesPshNamesToReorderExternalBank()
    {
        var source = new InMemoryAssetSource(
            "source.psx",
            BuildMinimalDirectMatrixPsx(),
            new Dictionary<string, byte[]>
            {
                ["source.psh"] = PshBytes(
                    ("SOURCEPART_SRC_RIGHT_THIGH", 0, "Scene Root"),
                    ("SOURCEPART_SRC_RIGHT_SHIN", 1, "src_right_thigh"))
            });
        var target = new InMemoryAssetSource(
            "target.psx",
            BuildMinimalDirectMatrixPsx(),
            new Dictionary<string, byte[]>
            {
                ["target.psh"] = PshBytes(
                    ("TARGETPART_DST_RIGHT_SHIN", 0, "dst_right_thigh"),
                    ("TARGETPART_DST_RIGHT_THIGH", 1, "Scene Root"))
            });

        var remap = PsxAnimationBoneMap.TryCreate(source, target, 2, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(remap);
        Assert.Equal([1, 0], remap.SourceToTarget);

        var channels = new short[2, PsxAnimation.ChannelsPerBone, 1];
        channels[0, 0, 0] = 100;
        channels[1, 0, 0] = 200;
        var animation = new PsxAnimation
        {
            BoneCount = 2,
            FrameCount = 1,
            Channels = channels
        };

        var reordered = PsxAnimationBoneMap.Remap(animation, remap, 2);

        Assert.Equal(200, reordered.Channels[0, 0, 0]);
        Assert.Equal(100, reordered.Channels[1, 0, 0]);
    }

    [Fact]
    public void BoneMap_PrefersExactNamesWhenSemanticFallbackIsAmbiguous()
    {
        var source = new InMemoryAssetSource(
            "source.psx",
            BuildMinimalDirectMatrixPsx(),
            new Dictionary<string, byte[]>
            {
                ["source.psh"] = PshBytes(
                    ("SOURCEPART_BANK_RIGHT_HAND", 0, "Scene Root"),
                    ("SOURCEPART_ALT_RIGHT_HAND", 1, "bank_right_hand"))
            });
        var target = new InMemoryAssetSource(
            "target.psx",
            BuildMinimalDirectMatrixPsx(),
            new Dictionary<string, byte[]>
            {
                ["target.psh"] = PshBytes(
                    ("SOURCEPART_ALT_RIGHT_HAND", 0, "bank_right_hand"),
                    ("SOURCEPART_BANK_RIGHT_HAND", 1, "Scene Root"))
            });

        var remap = PsxAnimationBoneMap.TryCreate(source, target, 2, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(remap);
        Assert.Equal([1, 0], remap.SourceToTarget);
    }

    [CorpusFact]
    public void Probe_Sk2AnimPsx_DetectsSharedMonolithicBank()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "sk2anim.psx");
        Assert.SkipWhen(path == null, "sk2anim.psx not found in sample builds");

        var source = new FileSystemAssetSource(path!);
        var bank = PsxAnimationBank.TryProbe(source, 19);

        Assert.NotNull(bank);
        Assert.Equal(PsxAnimLayoutVariant.Monolithic, bank.AnimFile.Layout);
        Assert.Equal(PsxAnimationFormatRevision.CompressedV2, bank.AnimFile.FormatRevision);
        Assert.Equal(PsxCharacterRuntimeRevision.ClassicSuper, bank.AnimFile.MinimumRuntimeRevision);
        Assert.False(bank.AnimFile.RequiresExtendedAnimationSlotIndex);
        Assert.Equal(147, bank.AnimFile.NumStreamsDeclared);
        Assert.Equal(147, bank.AnimFile.Entries.Count);
        Assert.Equal(19, bank.BoneCount);
        Assert.True(bank.MatchesTargetBoneCount);
    }

    [CorpusFact]
    public void BoneMap_Sk2AnimToMullen_RemapsSharedSkaterOrder()
    {
        var mullenPath = paths.FindSampleFile(Thps2ProtoBuild, "mullen.psx");
        var sk2AnimPath = paths.FindSampleFile(Thps2ProtoBuild, "sk2anim.psx");
        Assert.SkipWhen(mullenPath == null || sk2AnimPath == null,
            "mullen.psx/sk2anim.psx not found in sample builds");

        var remap = PsxAnimationBoneMap.TryCreate(
            new FileSystemAssetSource(sk2AnimPath!),
            new FileSystemAssetSource(mullenPath!),
            19,
            out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(remap);
        Assert.False(remap.IsIdentity);
        Assert.Equal(3, remap.SourceToTarget[1]); // sk2anim right_thigh -> mullen right_thigh
        Assert.Equal(1, remap.SourceToTarget[2]); // sk2anim right_shoe -> mullen right_shoe
        Assert.Equal(2, remap.SourceToTarget[3]); // sk2anim right_shin -> mullen right_shin
        Assert.Equal(6, remap.SourceToTarget[4]); // sk2anim left_thigh -> mullen left_thigh
        Assert.Equal(4, remap.SourceToTarget[5]); // sk2anim left_shoe -> mullen left_shoe
        Assert.Equal(5, remap.SourceToTarget[6]); // sk2anim left_shin -> mullen left_shin
    }

    [CorpusFact]
    public void Decode_MullenPlusSk2Anim_CombinesEmbeddedAndExternalBanks()
    {
        var mullenPath = paths.FindSampleFile(Thps2ProtoBuild, "mullen.psx");
        var sk2AnimPath = paths.FindSampleFile(Thps2ProtoBuild, "sk2anim.psx");
        Assert.SkipWhen(mullenPath == null || sk2AnimPath == null,
            "mullen.psx/sk2anim.psx not found in sample builds");

        var mullenSource = new FileSystemAssetSource(mullenPath!);
        var mullenData = File.ReadAllBytes(mullenPath!);
        var psxFile = PsxMeshFile.Parse(mullenData);
        Assert.NotNull(psxFile);

        var targetBoneCount = psxFile.Objects.Count;
        var sk2Source = new FileSystemAssetSource(sk2AnimPath!);
        var embeddedBank = PsxAnimationBank.TryProbe(mullenSource, mullenData, targetBoneCount);
        var externalBank = PsxAnimationBank.TryProbe(sk2Source, targetBoneCount);
        Assert.NotNull(embeddedBank);
        Assert.NotNull(externalBank);
        var remap = PsxAnimationBoneMap.TryCreate(
            sk2Source, mullenSource, targetBoneCount, out _);

        var embedded = PsxAnimationBank.Decode(
            embeddedBank,
            targetBoneCount,
            PsxAnimationBank.ResolveSelections(
                embeddedBank.AnimFile, -1, null, "mullen"));
        var external = PsxAnimationBank.Decode(
            externalBank,
            targetBoneCount,
            PsxAnimationBank.ResolveSelections(
                externalBank.AnimFile, -1, null, "sk2anim"),
            remap);

        Assert.Single(embedded.Animations);
        Assert.Equal(147, external.Animations.Count);
        Assert.Equal(148, embedded.Animations.Count + external.Animations.Count);
    }

    [CorpusFact]
    public void Probe_Sk2DefPsx_DisablesMismatchedNineteenBoneSkater()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "sk2def.psx");
        Assert.SkipWhen(path == null, "sk2def.psx not found in sample builds");

        var source = new FileSystemAssetSource(path!);
        var probes = PsxAnimationBank.CreateProbes(source, 19);

        var probe = Assert.Single(probes);
        Assert.Equal(93, probe.BoneCount);
        Assert.False(probe.MatchesSkeleton);
    }

    [CorpusFact]
    public void TweenedDirectClips_AreApocalypseAndTheSpiderManLineageOnly()
    {
        // The one-shot / cycle choice is an END-OF-CLIP branch for TWEENED v1
        // (0x2A) payloads: a tween flag of 0 stores every frame, so both
        // branches produce identical output. This census answers "does the
        // flag do anything for the shipped PS1 corpus at all?" — the whole
        // reason to surface it in the GUI — and would catch a decoder change
        // that started reading the field from the wrong place.
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var buildsWithTween = new SortedSet<string>(StringComparer.Ordinal);
        var tweenedClips = 0;
        var directClips = 0;

        foreach (var file in Directory.EnumerateFiles(
                     paths.SampleBuildsDir!, "*.psx", SearchOption.AllDirectories))
        {
            PsxAnimationBankInfo? bank;
            try
            {
                bank = PsxAnimationBank.TryProbe(new FileSystemAssetSource(file), null);
            }
            catch
            {
                continue;
            }

            if (bank is not { AnimFile.IsDirectMatrix: true }) continue;

            foreach (var entry in bank.AnimFile.Entries)
            {
                directClips++;
                if (entry.TweenFlag <= 0) continue;
                tweenedClips++;
                buildsWithTween.Add(BuildNameOf(file));
            }
        }

        // Just over half the shipped direct clips are tweened, so the choice is
        // not a corner case: it changes the last frame of 3,599 clips.
        Assert.Equal(7_097, directClips);
        Assert.Equal(3_599, tweenedClips);

        // Apocalypse (1998) plus the whole Spider-Man lineage — PSX protos and
        // finals, the DC proto, the PC final, and all three SM2:EE builds.
        Assert.Equal(11, buildsWithTween.Count);
        Assert.All(buildsWithTween, build => Assert.True(
            build.StartsWith("Apocalypse", StringComparison.Ordinal)
            || build.StartsWith("Spider-Man", StringComparison.Ordinal),
            $"Unexpected build with tweened direct clips: {build}"));

        // No THPS build ships one, so the flag is inert for that whole line —
        // worth knowing before chasing a "one-shot did nothing" report.
        Assert.DoesNotContain(buildsWithTween, b => b.Contains("Tony Hawk", StringComparison.Ordinal));
    }

    private static string BuildNameOf(string file)
    {
        var parts = file.Replace('\\', '/').Split('/');
        var index = Array.LastIndexOf(parts, "Builds");
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : parts[0];
    }

    [Fact]
    public void DefaultPreviewFps_IsTheEngineGameTickNotTheVblankRate()
    {
        // Reproduces FPS_Display (MAIN.cpp:1649-1690) rather than restating its
        // answer, so this fails if anyone "corrects" the constant to 60.
        //
        //   FPS[i]   = Xblanks elapsed during game frame i   (Xblanks are 60 Hz)
        //   Total    = FPS[0] + FPS[1] + FPS[2]
        //   TimeScale = 256 * Total / (2 * 3)
        //
        // TimeScale is 8.8 fixed point, so its neutral value is 256. Solving
        // for the Total that produces it gives the engine's nominal cadence.
        const int neutralTimeScale = 256;
        var totalAtNeutral = neutralTimeScale * (2 * 3) / 256;
        Assert.Equal(6, totalAtNeutral);

        const float vblankHz = 60f;
        var vblanksPerGameFrame = totalAtNeutral / 3f;
        Assert.Equal(2f, vblanksPerGameFrame);

        // UpdateFrame does AdjustedSpeed = mAnimSpeed * TimeScale >> 8, and the
        // default mAnimSpeed is 1.0, so one anim frame advances per game tick.
        var gameTickHz = vblankHz / vblanksPerGameFrame;
        Assert.Equal(30f, gameTickHz);
        // Derivation is the authority here; the constant is what's under test.
        // Argument order follows xUnit2000 (constant first), not that reading.
        Assert.Equal(PsxAnimationBank.DefaultPreviewFps, gameTickHz);

        // FPS_Display's own readout agrees at the same point.
        Assert.Equal(30, 60 * 3 / totalAtNeutral);

        // The 60 Hz clock is real but belongs to XFrames (XFramesShifted +=
        // TimeScale * 2), which drives surface animation, not skeletal frames.
        Assert.Equal(vblankHz, PsxAnimationBank.DefaultPreviewFps * vblanksPerGameFrame);
    }

    [Fact]
    public void DecodeSlot_ForwardsTheOneShotBranchToTweenExpansion()
    {
        // The GUI Animations pane reaches the decoder ONLY through DecodeSlot
        // (via PsxAnimationSource.Decode), so the branch has to survive that
        // hop. Two stored records over 4 frames at interval 2: the cycle
        // branch blends the tail back toward frame 0 (→ 50), the one-shot
        // branch extrapolates past the last key (→ 150). Those exact values
        // are pinned against the engine's lerp by PsxAnimDecoderTests; here
        // they are the witness that the flag was forwarded at all.
        var bytes = BuildTweenedDirectMatrixPsx();
        Assert.NotNull(PsxMeshFile.ParseHeaderOnly(bytes));
        var animFile = PsxAnimFile.Parse(bytes, 1);
        Assert.NotNull(animFile);
        Assert.Single(animFile.Entries);
        Assert.Equal(1, animFile.Entries[0].TweenFlag);
        var source = new InMemoryAssetSource("tweened_bank.psx", bytes);

        var cycle = PsxAnimationBank.DecodeSlot(source, 1, 0);
        var oneShot = PsxAnimationBank.DecodeSlot(source, 1, 0, boneRemap: null, oneShot: true);

        Assert.Equal(100, cycle.Channels[0, 3, 2]);
        Assert.Equal(100, oneShot.Channels[0, 3, 2]);
        Assert.Equal(50, cycle.Channels[0, 3, 3]);
        Assert.Equal(150, oneShot.Channels[0, 3, 3]);
    }

    [Fact]
    public void PsxAnimationSourceDecode_DefaultsToTheCycleBranch()
    {
        // Default must stay the CycleAnim wrap: it is the engine's dominant
        // character-anim mode, and every previously exported GLB used it.
        var source = new InMemoryAssetSource(
            "tweened_bank.psx", BuildTweenedDirectMatrixPsx());
        var slot = new PsxAnimationSource(source, animIndex: 0, frameCount: 4, targetBoneCount: 1);

        Assert.Equal(50, slot.Decode().Channels[0, 3, 3]);
        Assert.Equal(150, slot.Decode(oneShot: true).Channels[0, 3, 3]);
    }

    /// <summary>
    ///     The minimal bank, but with a TWEEN flag: 4 playback frames stored as
    ///     two records at interval 2, translating Tx 0 → 100.
    /// </summary>
    private static byte[] BuildTweenedDirectMatrixPsx()
    {
        const int stride = 24;
        // Same shape as BuildMinimalDirectMatrixPsx, including its trailing
        // slack past the terminator, which the header parser reads through.
        var data = new byte[0x4C + 2 * stride + 4 + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x00), 0x04);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x02), 0x02);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), 1);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x38), PsxMeshFile.HierChunkV1Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(0x3C), (uint)(0x0C + 2 * stride));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x48), 4);  // frames
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x4A), 1);  // tween: interval 2

        short[] keyTx = [0, 100];
        for (var record = 0; record < 2; record++)
        {
            var offset = 0x4C + record * stride;
            Span<short> identity = [4096, 0, 0, 0, 4096, 0, 0, 0, 4096];
            for (var i = 0; i < identity.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + i * 2), identity[i]);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 18), keyTx[record]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x4C + 2 * stride), 0xFFFFFFFF);
        return data;
    }

    private static PsxAnimationBankInfo ParseSyntheticBank()
    {
        var source = new InMemoryAssetSource("memory_bank.psx", BuildMinimalDirectMatrixPsx());
        var bank = PsxAnimationBank.TryProbe(source, 1);
        Assert.NotNull(bank);
        return bank;
    }

    private static byte[] BuildMinimalDirectMatrixPsx()
    {
        var data = new byte[0x70];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x00), 0x04);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x02), 0x02);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), 1);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x38), PsxMeshFile.HierChunkV1Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 0x24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x44), 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x48), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x4A), 0);

        Span<short> matrix =
        [
            4096, 0, 0,
            0, 4096, 0,
            0, 0, 4096
        ];
        for (var i = 0; i < matrix.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x4C + i * 2), matrix[i]);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x64), 0xFFFFFFFF);
        return data;
    }

    private static byte[] PshBytes(params (string Define, int Index, string Parent)[] bones)
    {
        var lines = new List<string>();
        foreach (var (define, index, parent) in bones)
        {
            lines.Add($"#define {define}\t\t\t{index}");
            lines.Add($"//   parent: {parent}");
        }

        return Encoding.ASCII.GetBytes(string.Join(Environment.NewLine, lines));
    }

    private sealed class InMemoryAssetSource(
        string entryName,
        byte[] data,
        IReadOnlyDictionary<string, byte[]>? companions = null) : AssetSource
    {
        public override string DisplayName => entryName;
        public override string EntryName => entryName;

        public override byte[] ReadBytes()
        {
            return data;
        }

        public override bool CompanionExists(string nameWithExtension)
        {
            return companions?.ContainsKey(nameWithExtension) == true;
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            return companions != null && companions.TryGetValue(nameWithExtension, out var bytes)
                ? bytes
                : null;
        }

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            if (companions != null)
            {
                foreach (var ext in extensions)
                {
                    var key = stem + ext;
                    if (companions.TryGetValue(key, out var bytes))
                        return bytes;
                }
            }

            return null;
        }
    }
}
