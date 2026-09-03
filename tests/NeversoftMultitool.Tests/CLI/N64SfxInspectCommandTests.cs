using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.CLI;

public sealed class N64SfxInspectCommandTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string Thps1N64Rom = "Tony Hawk's Pro Skater (USA).z64";
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2N64Rom = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string Thps3N64Build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
    private const string Thps3N64Rom = "Tony Hawk's Pro Skater 3 (USA).z64";
    private const string SpiderN64Build = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderN64Rom = "Spider-Man (USA).z64";

    public static TheoryData<string, string, int, int, string, int, int, int, bool>
        RomManifestExpectations() => new()
    {
        { Thps1N64Build, Thps1N64Rom, 0, 0, "unresolved", 0, 0, 0, false },
        { Thps2N64Build, Thps2N64Rom, 14, 671, "partialStateDependentOutcomeNotEstablished", 622, 34, 15, true },
        { Thps3N64Build, Thps3N64Rom, 14, 572, "resolvedIncludingExplicitNoTarget", 542, 30, 0, true },
        { SpiderN64Build, SpiderN64Rom, 55, 1_929, "resolvedIncludingExplicitNoTarget", 1_696, 233, 0, true }
    };

    [Fact]
    public void Resolver_ScansEveryAssetStrictlyAndSortsFullPathsOrdinal()
    {
        var suffixed = BuildBank(loopFlag: 0x00, note: 0x20);
        var misclassifiedBin = BuildBank(loopFlag: 0xFE, note: 0xFF);
        var malformedSuffix = BuildBank(loopFlag: 0x00, note: 0x20);
        malformedSuffix[12] = 1;
        N64AssetCarver.CarvedAsset[] assets =
        [
            new("sfx/010.sfx.n64", suffixed),
            new("misc/not-a-cue.bin", [1, 2, 3]),
            new("sfx/002.bin", misclassifiedBin),
            new("sfx/001.sfx.n64", malformedSuffix)
        ];

        var banks = N64SfxInspectCommand.SelectCarvedBanks(assets);

        Assert.Equal(["sfx/002.bin", "sfx/010.sfx.n64"],
            banks.Select(static bank => bank.Source));
        Assert.Equal(0xFE, banks[0].Bank.Records[0].LoopFlagRaw);
        Assert.Equal(0xFF, banks[0].Bank.Records[0].NoteRaw);
    }

    [Fact]
    public void Command_StandaloneWritesOneBankAggregateAndRejectsPairingOrPlaybackOptions()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "cue.sfx.n64");
        var output = Path.Combine(temp.Path, "nested", "manifest.json");
        File.WriteAllBytes(input, BuildBank(loopFlag: 0xFE, note: 0x80));

        var command = N64SfxInspectCommand.Create();
        Assert.Equal("n64-sfx-inspect", command.Name);
        Assert.Equal(0, command.Parse([input, "-o", output]).Invoke());

        using (var json = JsonDocument.Parse(File.ReadAllText(output)))
        {
            var root = json.RootElement;
            Assert.Equal("cue.sfx.n64", root.GetProperty("inputSource").GetString());
            Assert.Equal("explicitFile", root.GetProperty("selectionBasis").GetString());
            Assert.Equal(1, root.GetProperty("bankCount").GetInt32());
            Assert.Equal(1, root.GetProperty("recordCount").GetInt32());
            Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("compiledAliasMap").ValueKind);
            Assert.Equal(0, root.GetProperty("resolvedTargetCount").GetInt32());
            var bank = Assert.Single(root.GetProperty("banks").EnumerateArray());
            Assert.Equal("cue.sfx.n64", bank.GetProperty("source").GetString());
            var record = bank.GetProperty("records")[0];
            Assert.Equal(0x80, record.GetProperty("noteRaw").GetInt32());
            Assert.Equal(JsonValueKind.Null,
                record.GetProperty("compiledAliasResolution").ValueKind);
        }

        foreach (var option in new[] { "--pointer", "--wave", "--sample-rate", "--target" })
        {
            var forbiddenOutput = Path.Combine(temp.Path, $"forbidden-{option[2..]}.json");
            Assert.NotEqual(0, N64SfxInspectCommand.Create()
                .Parse([input, option, "value", "-o", forbiddenOutput])
                .Invoke());
            Assert.False(File.Exists(forbiddenOutput));
        }

    }

    [Fact]
    public void Command_MalformedInputLeavesAbsentAndExistingDestinationsUntouched()
    {
        using var temp = new TempDirectory();
        var malformed = Path.Combine(temp.Path, "malformed.sfx.n64");
        var badData = BuildBank(loopFlag: 0x00, note: 0x20);
        badData[^1] = 0;
        File.WriteAllBytes(malformed, badData);

        var absent = Path.Combine(temp.Path, "absent", "manifest.json");
        Assert.Equal(1, N64SfxInspectCommand.Execute(malformed, absent));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));

        var existing = Path.Combine(temp.Path, "existing.json");
        const string sentinel = "existing output must survive";
        File.WriteAllText(existing, sentinel);
        Assert.Equal(1, N64SfxInspectCommand.Execute(malformed, existing));
        Assert.Equal(sentinel, File.ReadAllText(existing));

        var missingOutput = Path.Combine(temp.Path, "missing.json");
        Assert.Equal(1, N64SfxInspectCommand.Execute(Path.Combine(temp.Path, "missing.sfx.n64"), missingOutput));
        Assert.False(File.Exists(missingOutput));
    }

    [Fact]
    public void Command_OutputCanonicalAliasCannotOverwriteStandaloneSource()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "cue.sfx.n64");
        var original = BuildBank(loopFlag: 0xFE, note: 0x80);
        File.WriteAllBytes(input, original);
        var outputAlias = Path.Combine(temp.Path, ".", Path.GetFileName(input));

        Assert.Equal(1, N64SfxInspectCommand.Execute(input, outputAlias));
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void ProgramRoute_RegistersCommandHelp()
    {
        Assert.Equal(0, Program.Main(["n64-sfx-inspect", "--help"]));
    }

    [CorpusTheory]
    [MemberData(nameof(RomManifestExpectations))]
    public void Command_RomWritesOneAggregateIncludingZeroBankAndCorrectedCueSuffixes(
        string build,
        string rom,
        int expectedBankCount,
        int expectedRecordCount,
        string expectedMappingStatus,
        int expectedResolved,
        int expectedUnmapped,
        int expectedDynamic,
        bool expectsCompiledMap)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "manifest.json");

        Assert.Equal(0, N64SfxInspectCommand.Execute(romPath!, output));

        using var json = JsonDocument.Parse(File.ReadAllText(output));
        var root = json.RootElement;
        Assert.Equal(rom, root.GetProperty("inputSource").GetString());
        Assert.Equal("strictRomStructuralScan", root.GetProperty("selectionBasis").GetString());
        Assert.Equal(expectedBankCount, root.GetProperty("bankCount").GetInt32());
        Assert.Equal(expectedRecordCount, root.GetProperty("recordCount").GetInt32());
        Assert.Equal(expectedMappingStatus, root.GetProperty("cueMappingStatus").GetString());
        Assert.Equal(expectedResolved, root.GetProperty("resolvedTargetCount").GetInt32());
        Assert.Equal(expectedUnmapped, root.GetProperty("explicitlyUnmappedCount").GetInt32());
        Assert.Equal(expectedDynamic, root.GetProperty("dynamicOverrideCount").GetInt32());
        Assert.Equal(rom == Thps2N64Rom ? 3 : 0,
            root.GetProperty("stateDependentUnknownCount").GetInt32());
        Assert.Equal(0, root.GetProperty("outsidePinnedTableCount").GetInt32());
        Assert.Equal(expectsCompiledMap ? JsonValueKind.Object : JsonValueKind.Null,
            root.GetProperty("compiledAliasMap").ValueKind);
        if (expectsCompiledMap)
        {
            var map = root.GetProperty("compiledAliasMap");
            var expectedSources = N64SfxInspectCommand.Resolve(romPath!);
            var expectedBinding = Assert.IsType<N64SfxCueEffectBankBindingProvenance>(
                expectedSources.EffectBankBinding);
            var binding = map.GetProperty("effectBankBinding");
            Assert.Equal(expectedBinding.BindingBasis,
                binding.GetProperty("bindingBasis").GetString());
            Assert.Equal(expectedBinding.BfxSource,
                binding.GetProperty("bfxSource").GetString());
            Assert.Equal(expectedBinding.BfxSerializedSize,
                binding.GetProperty("bfxSerializedSize").GetInt32());
            Assert.Equal(expectedBinding.BfxSha256,
                binding.GetProperty("bfxSha256").GetString());
            Assert.Equal(expectedBinding.PointerSource,
                binding.GetProperty("pointerSource").GetString());
            Assert.Equal(expectedBinding.PointerSerializedSize,
                binding.GetProperty("pointerSerializedSize").GetInt32());
            Assert.Equal(expectedBinding.PointerSha256,
                binding.GetProperty("pointerSha256").GetString());
            Assert.Equal(expectedResolved + expectedUnmapped + expectedDynamic,
                expectedRecordCount);
            Assert.Equal(
                rom switch
                {
                    Thps2N64Rom => 322,
                    Thps3N64Rom => 186,
                    _ => 994
                },
                map.GetProperty("effectCount").GetInt32());
            Assert.Equal(
                rom switch
                {
                    Thps2N64Rom => 395,
                    Thps3N64Rom => 472,
                    _ => 481
                },
                map.GetProperty("maximumAliasInclusive").GetInt32());
            var cueAliasMask = map.GetProperty("cueAliasMask").GetUInt32();
            Assert.Equal(rom == SpiderN64Rom ? uint.MaxValue : ushort.MaxValue,
                cueAliasMask);
            var rules = map.GetProperty("stateDependentRules").EnumerateArray().ToArray();
            Assert.Equal(rom == Thps2N64Rom ? 5 : 0, rules.Length);
            Assert.Equal(
                0,
                map.GetProperty("contextualResolutions").GetArrayLength());
            var evidenceRanges = map.GetProperty("pinnedEvidenceRanges")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                rom switch
                {
                    Thps2N64Rom => 8,
                    Thps3N64Rom => 7,
                    _ => 4
                },
                evidenceRanges.Length);
            Assert.All(evidenceRanges, static evidence =>
                Assert.True(evidence.GetProperty("kind").GetString() is "code" or "data"));
            if (rom == Thps2N64Rom)
            {
                var ownerLayout = map.GetProperty("cueOwnerLayout");
                Assert.Equal(0x800E4A84u,
                    ownerLayout.GetProperty("ownerIndexRamAddress").GetUInt32());
                Assert.Equal(0xC6550,
                    ownerLayout.GetProperty("descriptorTableOffset").GetInt32());
                Assert.Equal(0x800DD070u,
                    ownerLayout.GetProperty("descriptorTableRamAddress").GetUInt32());
                Assert.Equal(0x10,
                    ownerLayout.GetProperty("descriptorEntryStride").GetInt32());
                Assert.Equal(0x800F0E80u,
                    ownerLayout.GetProperty("activeRecordBaseRamAddress").GetUInt32());
                Assert.Equal(0x180,
                    ownerLayout.GetProperty("activeRecordStride").GetInt32());
                Assert.Equal(0x44,
                    ownerLayout.GetProperty("activeRecordFieldOffset").GetInt32());
                var descriptorEvidence = Assert.Single(evidenceRanges,
                    static evidence => evidence.GetProperty("kind").GetString() == "data");
                Assert.Equal(0xC6550, descriptorEvidence.GetProperty("offset").GetInt32());
                Assert.Equal(
                    new uint[] { 0xF4, 0x13C, 0x156, 0x157, 0x158 },
                    rules.Select(static rule => rule.GetProperty("alias").GetUInt32()));
                Assert.All(rules, static rule =>
                    Assert.NotEmpty(rule.GetProperty("cases").EnumerateArray()));
                Assert.Contains(
                    rules.SelectMany(static rule => rule.GetProperty("cases").EnumerateArray()),
                    static item => item.GetProperty("compiledTargetRaw").ValueKind == JsonValueKind.Number &&
                                   item.GetProperty("compiledTargetRaw").GetUInt32() == ushort.MaxValue &&
                                   item.GetProperty("bfxEffectIndex").ValueKind == JsonValueKind.Null);
                var global158 = Assert.Single(rules,
                    static rule => rule.GetProperty("alias").GetUInt32() == 0x158);
                Assert.Contains(
                    global158.GetProperty("cases").EnumerateArray(),
                    static item => item.GetProperty("outcome").GetString() ==
                                   "runtimeOutcomeNotEstablished" &&
                                   item.GetProperty("compiledTargetRaw").ValueKind == JsonValueKind.Null);

            }
            else
            {
                Assert.Equal(JsonValueKind.Null, map.GetProperty("cueOwnerLayout").ValueKind);
            }

            if (rom == SpiderN64Rom)
            {
                Assert.Equal(sizeof(uint), map.GetProperty("tableEntrySize").GetInt32());
                Assert.Equal(0x0000_0FA0u, map.GetProperty("explicitNoTargetRaw").GetUInt32());
                Assert.Equal(0x0000_FFFFu, map.GetProperty("effectIndexMask").GetUInt32());
                Assert.Equal(0x001F_0000u, map.GetProperty("routingFlagsMask").GetUInt32());
            }
            else
            {
                Assert.Equal(sizeof(ushort), map.GetProperty("tableEntrySize").GetInt32());
                Assert.Equal((uint)ushort.MaxValue,
                    map.GetProperty("explicitNoTargetRaw").GetUInt32());
            }

            foreach (var record in root.GetProperty("banks").EnumerateArray()
                         .SelectMany(static bank => bank.GetProperty("records").EnumerateArray()))
            {
                Assert.Equal(
                    record.GetProperty("aliasRaw").GetUInt32() & cueAliasMask,
                    record.GetProperty("compiledAliasResolution")
                        .GetProperty("lookupAlias")
                        .GetUInt32());
            }
        }
        var bankPaths = root.GetProperty("banks").EnumerateArray()
            .Select(static bank => bank.GetProperty("source").GetString()!)
            .ToArray();
        Assert.Equal(bankPaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(), bankPaths);

        if (rom == Thps1N64Rom)
        {
            Assert.Empty(bankPaths);
            return;
        }

        if (rom == Thps2N64Rom)
        {
            Assert.Contains("sfx/001.sfx.n64", bankPaths);
            Assert.Contains("sfx/003.sfx.n64", bankPaths);
        }
        else if (rom == Thps3N64Rom)
        {
            Assert.Contains("sfx/000.sfx.n64", bankPaths);
            Assert.Contains("sfx/022.sfx.n64", bankPaths);
        }
        else
        {
            Assert.Contains("sfx/000.sfx.n64", bankPaths);
            Assert.Contains("sfx/054.sfx.n64", bankPaths);
        }
    }

    private static byte[] BuildBank(byte loopFlag, byte note)
    {
        var data = new byte[20];
        data[0] = loopFlag;
        data[1] = 1;
        data[2] = 2;
        data[3] = note;
        data.AsSpan(16).Fill(0xFF);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-sfx-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
