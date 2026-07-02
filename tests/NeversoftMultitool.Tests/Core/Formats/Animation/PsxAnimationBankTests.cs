using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

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
            animIndex: -1,
            animName: null,
            namePrefix: "sk2anim");

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
            animIndex: 0,
            animName: "idle",
            namePrefix: null);

        var selection = Assert.Single(selected);
        Assert.Equal(0, selection.Index);
        Assert.Equal("idle", selection.Name);
    }

    [Fact]
    public void PsxAnimationSource_DecodesFromGenericAssetSource()
    {
        var source = new InMemoryAssetSource("memory_bank.psx", BuildMinimalDirectMatrixPsx());
        var probes = PsxAnimationBank.CreateProbes(source, targetBoneCount: 1);
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
        var bank = PsxAnimationBank.TryProbe(source, targetBoneCount: 2);
        Assert.NotNull(bank);
        Assert.False(bank.MatchesTargetBoneCount);

        var selected = PsxAnimationBank.ResolveSelections(
            bank.AnimFile,
            animIndex: -1,
            animName: null,
            namePrefix: null);
        var result = PsxAnimationBank.Decode(bank, targetBoneCount: 2, selected);

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

        var remap = PsxAnimationBoneMap.TryCreate(source, target, boneCount: 2, out var diagnostic);

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

        var reordered = PsxAnimationBoneMap.Remap(animation, remap, targetBoneCount: 2);

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

        var remap = PsxAnimationBoneMap.TryCreate(source, target, boneCount: 2, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.NotNull(remap);
        Assert.Equal([1, 0], remap.SourceToTarget);
    }

    [Fact]
    public void Probe_Sk2AnimPsx_DetectsSharedMonolithicBank()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "sk2anim.psx");
        Assert.SkipWhen(path == null, "sk2anim.psx not found in sample builds");

        var source = new FileSystemAssetSource(path!);
        var bank = PsxAnimationBank.TryProbe(source, targetBoneCount: 19);

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

    [Fact]
    public void BoneMap_Sk2AnimToMullen_RemapsSharedSkaterOrder()
    {
        var mullenPath = paths.FindSampleFile(Thps2ProtoBuild, "mullen.psx");
        var sk2AnimPath = paths.FindSampleFile(Thps2ProtoBuild, "sk2anim.psx");
        Assert.SkipWhen(mullenPath == null || sk2AnimPath == null,
            "mullen.psx/sk2anim.psx not found in sample builds");

        var remap = PsxAnimationBoneMap.TryCreate(
            new FileSystemAssetSource(sk2AnimPath!),
            new FileSystemAssetSource(mullenPath!),
            boneCount: 19,
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

    [Fact]
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

    [Fact]
    public void Probe_Sk2DefPsx_DisablesMismatchedNineteenBoneSkater()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "sk2def.psx");
        Assert.SkipWhen(path == null, "sk2def.psx not found in sample builds");

        var source = new FileSystemAssetSource(path!);
        var probes = PsxAnimationBank.CreateProbes(source, targetBoneCount: 19);

        var probe = Assert.Single(probes);
        Assert.Equal(93, probe.BoneCount);
        Assert.False(probe.MatchesSkeleton);
    }

    private static PsxAnimationBankInfo ParseSyntheticBank()
    {
        var source = new InMemoryAssetSource("memory_bank.psx", BuildMinimalDirectMatrixPsx());
        var bank = PsxAnimationBank.TryProbe(source, targetBoneCount: 1);
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

        return System.Text.Encoding.ASCII.GetBytes(string.Join(Environment.NewLine, lines));
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
