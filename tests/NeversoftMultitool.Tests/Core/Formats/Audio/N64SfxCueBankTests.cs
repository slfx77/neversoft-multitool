using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class N64SfxCueBankTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string Thps1N64Rom = "Tony Hawk's Pro Skater (USA).z64";
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2N64Rom = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string Thps3N64Build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
    private const string Thps3N64Rom = "Tony Hawk's Pro Skater 3 (USA).z64";
    private const string SpiderManN64Build = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderManN64Rom = "Spider-Man (USA).z64";

    public static TheoryData<string, string, int, int, int, int, int, int, int, int, string>
        CueCorpusExpectations() => new()
    {
        {
            Thps1N64Build, Thps1N64Rom,
            2_176, 0, 0, 0, 0, 0, 0, 0,
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"
        },
        {
            Thps2N64Build, Thps2N64Rom,
            3_995, 14, 671, 10_792, 32, 65, 497, 174,
            "BDCE333142A7AEB912444F74802F3A475BBE8CEE943F54683DF4DDCC00BE96EF"
        },
        {
            Thps3N64Build, Thps3N64Rom,
            3_346, 14, 572, 9_208, 32, 54, 372, 200,
            "0E934D22BA9FF7D7F569E016DE7CCF033D35C333116893F240EA1094B66B0A0C"
        },
        {
            SpiderManN64Build, SpiderManN64Rom,
            4_347, 55, 1_929, 31_084, 7, 53, 1_765, 164,
            "8030C60E9EDB23D4AC411EEEEEAC7EE882C0A4ED401BD8D8F9EB5F8FF6F6FEEA"
        }
    };

    [Fact]
    public void Parse_ExactBigEndianRecordsAndTerminator_PreservesEveryRawByte()
    {
        var data = BuildBank(
            new SyntheticRecord(0xFE, 0x12, 0x03, 0x3C, 0x1000, 0x2000, 0x0001_0203),
            new SyntheticRecord(0x7D, 0xA1, 0xB2, 0xC3, 0x7FFF, 0x8000, 0x89AB_CDEF));

        var bank = N64SfxCueBank.Parse(data);

        Assert.Equal(36, bank.SerializedSize);
        Assert.Equal(32, bank.TerminatorOffset);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, bank.TerminatorRaw);
        Assert.Equal("E5A5BC7160439ED17F0600EF9D8CE6DF77B75DB5AA291EC3AA1116E6C0EC7D7E",
            bank.SerializedSha256);
        Assert.Equal(2, bank.Records.Count);

        var first = bank.Records[0];
        Assert.Equal(0, first.Index);
        Assert.Equal(0, first.Offset);
        Assert.Equal(0xFE, first.LoopFlagRaw);
        Assert.Equal(0x12, first.ProgramRaw);
        Assert.Equal(0x03, first.CategoryRaw);
        Assert.Equal(0x3C, first.NoteRaw);
        Assert.Equal(0x1000, first.PitchRaw);
        Assert.Equal(0x2000, first.VolumeRaw);
        Assert.Equal(0x0001_0203u, first.AliasRaw);
        Assert.Equal(new byte[4], first.PadRaw);
        Assert.Equal("FE12033C100020000001020300000000", Convert.ToHexString(first.RecordRaw.ToArray()));

        var second = bank.Records[1];
        Assert.Equal(1, second.Index);
        Assert.Equal(16, second.Offset);
        Assert.Equal(0x7D, second.LoopFlagRaw);
        Assert.Equal(0xA1, second.ProgramRaw);
        Assert.Equal(0xB2, second.CategoryRaw);
        Assert.Equal(0xC3, second.NoteRaw);
        Assert.Equal(0x7FFF, second.PitchRaw);
        Assert.Equal(0x8000, second.VolumeRaw);
        Assert.Equal(0x89AB_CDEFu, second.AliasRaw);
        Assert.Equal("7DA1B2C37FFF800089ABCDEF00000000", Convert.ToHexString(second.RecordRaw.ToArray()));
    }

    [Fact]
    public void Parse_TruncationWrongShapeWrongTerminatorAndBadPadding_FailClosed()
    {
        var valid = BuildBank(
            new SyntheticRecord(0x00, 1, 2, 3, 4, 5, 6),
            new SyntheticRecord(0xFE, 7, 8, 9, 10, 11, 12));

        for (var length = 0; length < valid.Length; length++)
        {
            var truncated = valid.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => N64SfxCueBank.Parse(truncated));
        }

        var appended = new byte[valid.Length + 1];
        valid.CopyTo(appended, 0);
        Assert.Throws<InvalidDataException>(() => N64SfxCueBank.Parse(appended));

        var wrongTerminator = (byte[])valid.Clone();
        wrongTerminator[^1] = 0;
        Assert.Throws<InvalidDataException>(() => N64SfxCueBank.Parse(wrongTerminator));

        for (var record = 0; record < 2; record++)
        {
            for (var pad = 12; pad < 16; pad++)
            {
                var badPadding = (byte[])valid.Clone();
                badPadding[record * N64SfxCueBank.RecordSize + pad] = 1;
                var exception = Assert.Throws<InvalidDataException>(() => N64SfxCueBank.Parse(badPadding));
                Assert.Contains($"record {record}", exception.Message, StringComparison.Ordinal);
            }
        }

        Assert.False(N64SfxCueBank.TryParse(wrongTerminator, out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void Parse_EmptyBankIsValid_AndEarlyTerminatorRejectsUnreachableRecords()
    {
        var empty = N64SfxCueBank.Parse([0xFF, 0xFF, 0xFF, 0xFF]);
        Assert.Equal(4, empty.SerializedSize);
        Assert.Equal(0, empty.TerminatorOffset);
        Assert.Empty(empty.Records);

        var earlyTerminator = BuildBank(
            new SyntheticRecord(0xFF, 0xFF, 0xFF, 0xFF, 0x1234, 0x5678, 0x9ABC_DEF0),
            new SyntheticRecord(0x00, 1, 2, 3, 4, 5, 6));
        var exception = Assert.Throws<InvalidDataException>(() =>
            N64SfxCueBank.Parse(earlyTerminator));
        Assert.Contains("early FFFFFFFF terminator at record 0", exception.Message,
            StringComparison.Ordinal);
        Assert.False(N64SfxCueBank.TryParse(earlyTerminator, out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void Json_IsDeterministicRawAndExplicitlyDoesNotMapOrPlayCues()
    {
        var data = BuildBank(
            new SyntheticRecord(0xFE, 0x12, 0x03, 0x3C, 0x1000, 0x2000, 0x0001_0203));
        var bank = N64SfxCueBank.Parse(data);
        N64SfxCueBankSource[] sources = [new("sfx/007.sfx.n64", bank)];

        var first = N64SfxCueBankJsonExporter.Serialize(
            "cue.sfx.n64", N64SfxCueBankJsonExporter.ExplicitFileSelection, sources);
        var second = N64SfxCueBankJsonExporter.Serialize(
            "cue.sfx.n64", N64SfxCueBankJsonExporter.ExplicitFileSelection, sources);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(N64SfxCueBankJsonExporter.SchemaName, root.GetProperty("schema").GetString());
        Assert.Equal(N64SfxCueBankJsonExporter.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Neversoft N64 SFX cue tables", root.GetProperty("format").GetString());
        Assert.Equal("cue.sfx.n64", root.GetProperty("inputSource").GetString());
        Assert.Equal("explicitFile", root.GetProperty("selectionBasis").GetString());
        Assert.Equal(1, root.GetProperty("bankCount").GetInt32());
        Assert.Equal(1, root.GetProperty("recordCount").GetInt32());
        Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
        Assert.False(root.TryGetProperty("targetMappingStatus", out _));
        Assert.DoesNotContain("\"targetMappingStatus\"", first, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sampleRate").ValueKind);
        Assert.Equal("notApplied", root.GetProperty("pitchApplicationStatus").GetString());
        Assert.Equal("notExecuted", root.GetProperty("playbackStatus").GetString());

        var serializedBank = Assert.Single(root.GetProperty("banks").EnumerateArray());
        Assert.Equal("sfx/007.sfx.n64", serializedBank.GetProperty("source").GetString());
        Assert.Equal("E4DD581B2B0C090030C69A394E1046A34CB6D790C0ACB30D6DCE39B4B8E29A90",
            serializedBank.GetProperty("serializedSha256").GetString());
        Assert.Equal("bigEndian", serializedBank.GetProperty("byteOrder").GetString());
        Assert.Equal(16, serializedBank.GetProperty("recordSize").GetInt32());
        Assert.Equal(1, serializedBank.GetProperty("recordCount").GetInt32());
        Assert.Equal(16, serializedBank.GetProperty("terminatorOffset").GetInt32());
        Assert.Equal("FFFFFFFF", serializedBank.GetProperty("terminatorRawHex").GetString());

        var record = serializedBank.GetProperty("records")[0];
        Assert.Equal(0xFE, record.GetProperty("loopFlagRaw").GetInt32());
        Assert.Equal(0x1000, record.GetProperty("pitchRaw").GetInt32());
        Assert.Equal(0x0001_0203u, record.GetProperty("aliasRaw").GetUInt32());
        Assert.Equal([0, 0, 0, 0],
            record.GetProperty("padRaw").EnumerateArray().Select(static item => item.GetInt32()));
        Assert.Equal("FE12033C100020000001020300000000",
            record.GetProperty("recordRawHex").GetString());
        Assert.Equal("3E2815C2BABA4D4D3EB7E2C032E648268AFF5E8596401AE70C419D105DC4E339",
            record.GetProperty("recordSha256").GetString());
    }

    [Fact]
    public void Json_StateDependentUnknownCountTracksResolvedBankContext_NotGlobalRuleMetadata()
    {
        const string source = "sfx/001.sfx.n64";
        var bank = N64SfxCueBank.Parse(BuildBank(
            new SyntheticRecord(0x00, 1, 2, 3, 4, 5, 1)));
        N64SfxCueBankSource[] sources = [new(source, bank)];
        var effectBankBinding = N64SfxCueEffectBankBindingProvenance.Create(
            "testFixture",
            "effects.bfx.n64",
            [0x01, 0x02, 0x03],
            "sounds.ptr.n64",
            [0x04, 0x05]);
        var globalRule = new N64CompiledSfxDynamicAliasRule(
            1,
            "synthetic global selector",
            [
                new N64CompiledSfxDynamicAliasCase("selector == 0", 0),
                new N64CompiledSfxDynamicAliasCase("otherwise", null)
            ]);
        var dynamicRules = new Dictionary<uint, N64CompiledSfxDynamicAliasRule>
        {
            [1] = globalRule
        };

        N64CompiledSfxAliasMap BuildMap(
            IReadOnlyDictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>
                contexts) =>
            new(
                "synthetic build",
                new string('A', 64),
                0,
                4,
                new string('B', 64),
                4,
                new string('C', 64),
                1,
                1,
                sizeof(ushort),
                ushort.MaxValue,
                ushort.MaxValue,
                N64CompiledSfxAliasMapResolver.EffectIndexMask,
                N64CompiledSfxAliasMapResolver.RoutingFlagsMask,
                null,
                [],
                null,
                contexts,
                dynamicRules,
                [0, ushort.MaxValue]);

        var globalMap = BuildMap(new Dictionary<
            N64CompiledSfxCueContextKey,
            N64CompiledSfxCueContextResolution>());
        Assert.Throws<ArgumentException>(() => N64SfxCueBankJsonExporter.Serialize(
            "game.z64",
            N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
            sources,
            globalMap));
        Assert.Throws<ArgumentException>(() => N64SfxCueBankJsonExporter.Serialize(
            "game.z64",
            N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
            sources,
            compiledAliasMap: null,
            effectBankBinding: effectBankBinding));

        using (var global = JsonDocument.Parse(N64SfxCueBankJsonExporter.Serialize(
                   "game.z64",
                   N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
                   sources,
                   globalMap,
                   effectBankBinding)))
        {
            var root = global.RootElement;
            Assert.Equal("partialStateDependentOutcomeNotEstablished",
                root.GetProperty("cueMappingStatus").GetString());
            Assert.Equal(1, root.GetProperty("dynamicOverrideCount").GetInt32());
            Assert.Equal(1, root.GetProperty("stateDependentUnknownCount").GetInt32());
            Assert.Equal(0, root.GetProperty("outsidePinnedTableCount").GetInt32());
            var binding = root.GetProperty("compiledAliasMap")
                .GetProperty("effectBankBinding");
            Assert.Equal("testFixture", binding.GetProperty("bindingBasis").GetString());
            Assert.Equal("effects.bfx.n64", binding.GetProperty("bfxSource").GetString());
            Assert.Equal(3, binding.GetProperty("bfxSerializedSize").GetInt32());
            Assert.Equal(
                "039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81",
                binding.GetProperty("bfxSha256").GetString());
            Assert.Equal("sounds.ptr.n64", binding.GetProperty("pointerSource").GetString());
            Assert.Equal(2, binding.GetProperty("pointerSerializedSize").GetInt32());
            Assert.Equal(
                "2FA1B377BF67309F65E5E7BC9D924345CA648DEC4E601A398A9CB497DCBA3765",
                binding.GetProperty("pointerSha256").GetString());

            var resolution = root.GetProperty("banks")[0].GetProperty("records")[0]
                .GetProperty("compiledAliasResolution");
            Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
                resolution.GetProperty("status").GetString());
            var unknown = Assert.Single(
                resolution.GetProperty("stateDependentRule").GetProperty("cases").EnumerateArray(),
                static item => item.GetProperty("compiledTargetRaw").ValueKind == JsonValueKind.Null);
            Assert.Equal("runtimeOutcomeNotEstablished",
                unknown.GetProperty("outcome").GetString());
        }

        var contextualRule = new N64CompiledSfxDynamicAliasRule(
            1,
            "exact synthetic owner",
            [
                new N64CompiledSfxDynamicAliasCase("gate passes", 0),
                new N64CompiledSfxDynamicAliasCase("gate fails", ushort.MaxValue)
            ]);
        var key = new N64CompiledSfxCueContextKey(source, bank.SerializedSha256, 1);
        var contexts = new Dictionary<
            N64CompiledSfxCueContextKey,
            N64CompiledSfxCueContextResolution>
        {
            [key] = new(
                source,
                bank.SerializedSha256,
                1,
                N64CompiledSfxAliasMapResolver.ContextualOwnerBranchBasis,
                0,
                0,
                null,
                "synthetic owner state",
                null,
                contextualRule)
        };

        using var contextual = JsonDocument.Parse(N64SfxCueBankJsonExporter.Serialize(
            "game.z64",
            N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
            sources,
            BuildMap(contexts),
            effectBankBinding));
        var contextualRoot = contextual.RootElement;
        Assert.Equal("resolvedIncludingStateDependentChoicesAndExplicitNoTarget",
            contextualRoot.GetProperty("cueMappingStatus").GetString());
        Assert.Equal(1, contextualRoot.GetProperty("dynamicOverrideCount").GetInt32());
        Assert.Equal(0, contextualRoot.GetProperty("stateDependentUnknownCount").GetInt32());

        // The global fallback still contains its unknown branch; the aggregate
        // count is intentionally based on the rule selected for this exact bank.
        Assert.Contains(
            contextualRoot.GetProperty("compiledAliasMap").GetProperty("stateDependentRules")[0]
                .GetProperty("cases").EnumerateArray(),
            static item => item.GetProperty("compiledTargetRaw").ValueKind == JsonValueKind.Null);
        var contextualCases = contextualRoot.GetProperty("banks")[0].GetProperty("records")[0]
            .GetProperty("compiledAliasResolution").GetProperty("stateDependentRule")
            .GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(2, contextualCases.Length);
        Assert.DoesNotContain(contextualCases,
            static item => item.GetProperty("compiledTargetRaw").ValueKind == JsonValueKind.Null);
        Assert.Contains(contextualCases,
            static item => item.GetProperty("outcome").GetString() == "target" &&
                           item.GetProperty("compiledTargetRaw").GetUInt32() == 0);
        Assert.Contains(contextualCases,
            static item => item.GetProperty("outcome").GetString() == "explicitNoTarget" &&
                           item.GetProperty("compiledTargetRaw").GetUInt32() == ushort.MaxValue);
    }

    [Fact]
    public void Json_AggregateSortsFullPathsOrdinalAndRepresentsAZeroBankRom()
    {
        var bank = N64SfxCueBank.Parse(BuildBank(
            new SyntheticRecord(0x00, 1, 2, 3, 4, 5, 6)));
        var sortedJson = N64SfxCueBankJsonExporter.Serialize(
            "game.z64",
            N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
            [
                new N64SfxCueBankSource("sfx/010.sfx.n64", bank),
                new N64SfxCueBankSource("sfx/002.bin", bank)
            ]);

        using (var json = JsonDocument.Parse(sortedJson))
        {
            var root = json.RootElement;
            Assert.Equal("strictRomStructuralScan", root.GetProperty("selectionBasis").GetString());
            Assert.Equal(2, root.GetProperty("bankCount").GetInt32());
            Assert.Equal(2, root.GetProperty("recordCount").GetInt32());
            Assert.Equal(
                ["sfx/002.bin", "sfx/010.sfx.n64"],
                root.GetProperty("banks").EnumerateArray()
                    .Select(static item => item.GetProperty("source").GetString()));
        }

        var emptyJson = N64SfxCueBankJsonExporter.Serialize(
            "Tony Hawk's Pro Skater (USA).z64",
            N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
            []);
        using var empty = JsonDocument.Parse(emptyJson);
        Assert.Equal(0, empty.RootElement.GetProperty("bankCount").GetInt32());
        Assert.Equal(0, empty.RootElement.GetProperty("recordCount").GetInt32());
        Assert.Empty(empty.RootElement.GetProperty("banks").EnumerateArray());
    }

    [CorpusTheory]
    [MemberData(nameof(CueCorpusExpectations))]
    public void N64Roms_StrictParseEveryCarvedAssetAndPinTheRawCueCensus(
        string build,
        string rom,
        int expectedAssetCount,
        int expectedBankCount,
        int expectedRecordCount,
        int expectedSerializedBytes,
        int expectedMinRecords,
        int expectedMaxRecords,
        int expectedZeroFlags,
        int expectedFeFlags,
        string expectedAggregateSha256)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        Assert.Equal(expectedAssetCount, assets.Count);

        // Parse every asset independently rather than treating an extension as
        // proof. The carver now shares this exact predicate; this scan guards
        // against future taxonomy drift and unrelated false positives.
        var banks = new List<(string Path, N64SfxCueBank Bank)>();
        foreach (var asset in assets.OrderBy(static asset => asset.Path, StringComparer.Ordinal))
        {
            if (N64SfxCueBank.TryParse(asset.Data, out var bank))
                banks.Add((asset.Path, bank!));
        }

        Assert.Equal(expectedBankCount, banks.Count);
        Assert.Equal(expectedRecordCount, banks.Sum(static item => item.Bank.Records.Count));
        Assert.Equal(expectedSerializedBytes, banks.Sum(static item => item.Bank.SerializedSize));
        Assert.Equal(expectedMinRecords,
            banks.Count == 0 ? 0 : banks.Min(static item => item.Bank.Records.Count));
        Assert.Equal(expectedMaxRecords,
            banks.Count == 0 ? 0 : banks.Max(static item => item.Bank.Records.Count));
        Assert.All(banks, static item => Assert.StartsWith("sfx/", item.Path, StringComparison.Ordinal));

        var records = banks.SelectMany(static item => item.Bank.Records).ToArray();
        Assert.Equal(expectedZeroFlags, records.Count(static record => record.LoopFlagRaw == 0x00));
        Assert.Equal(expectedFeFlags, records.Count(static record => record.LoopFlagRaw == 0xFE));
        Assert.Equal(expectedRecordCount, expectedZeroFlags + expectedFeFlags);
        Assert.Equal(expectedRecordCount * 4, records.Sum(static record => record.PadRaw.Count));
        Assert.Equal(expectedRecordCount * 4,
            records.SelectMany(static record => record.PadRaw).Count(static value => value == 0));

        Assert.Equal(expectedBankCount, banks.Count(static item =>
            item.Bank.TerminatorRaw.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
        Assert.All(banks, static item =>
        {
            Assert.Equal(item.Bank.Records.Count * N64SfxCueBank.RecordSize + N64SfxCueBank.TerminatorSize,
                item.Bank.SerializedSize);
            Assert.Equal(item.Bank.SerializedSize - N64SfxCueBank.TerminatorSize, item.Bank.TerminatorOffset);
        });

        Assert.Equal(expectedBankCount, banks.Count(static item =>
            item.Path.EndsWith(".sfx.n64", StringComparison.Ordinal)));
        AssertRepresentativeBankHashes(rom, banks);
        Assert.Equal(expectedAggregateSha256, AggregatePathAndBankHashes(banks));
    }

    private static void AssertRepresentativeBankHashes(
        string rom,
        IReadOnlyList<(string Path, N64SfxCueBank Bank)> banks)
    {
        switch (rom)
        {
            case Thps1N64Rom:
                Assert.Empty(banks);
                break;
            case Thps2N64Rom:
                AssertBank(banks, "sfx/000.sfx.n64", 516, 32,
                    "CF92F19BD8DF34918DF69707E0C63BECC9FAB464A832F29A2D830C21577C8DB7");
                AssertBank(banks, "sfx/001.sfx.n64", 708, 44,
                    "87E7B5A93CC67E4719651C95D314ACD7978238E7150C3F6473BA21AF8E65C5DF");
                AssertBank(banks, "sfx/003.sfx.n64", 644, 40,
                    "A68FEB75325C3EDD423005268DE79F9F7EB070426041D76BB24D3B1854DC52E5");
                break;
            case Thps3N64Rom:
                AssertBank(banks, "sfx/000.sfx.n64", 516, 32,
                    "CF92F19BD8DF34918DF69707E0C63BECC9FAB464A832F29A2D830C21577C8DB7");
                break;
            case SpiderManN64Rom:
                AssertBank(banks, "sfx/000.sfx.n64", 724, 45,
                    "A44AA622B7CE97CDB9A92F06AAE6692760C2D095302784E9F9FCF1540AEE98B2");
                break;
            default:
                Assert.Fail($"No representative SFX cue bank pins are defined for {rom}");
                break;
        }
    }

    private static void AssertBank(
        IReadOnlyList<(string Path, N64SfxCueBank Bank)> banks,
        string expectedPath,
        int expectedSize,
        int expectedRecords,
        string expectedSha256)
    {
        var candidate = Assert.Single(banks, item => item.Path == expectedPath);
        Assert.Equal(expectedSize, candidate.Bank.SerializedSize);
        Assert.Equal(expectedRecords, candidate.Bank.Records.Count);
        Assert.Equal(expectedSha256, candidate.Bank.SerializedSha256);
    }

    private static string AggregatePathAndBankHashes(
        IEnumerable<(string Path, N64SfxCueBank Bank)> banks)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in banks.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(item.Path));
            aggregate.AppendData([0]);
            aggregate.AppendData(Convert.FromHexString(item.Bank.SerializedSha256));
        }

        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    private static byte[] BuildBank(params SyntheticRecord[] records)
    {
        var data = new byte[checked(records.Length * N64SfxCueBank.RecordSize + N64SfxCueBank.TerminatorSize)];
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            var offset = index * N64SfxCueBank.RecordSize;
            data[offset] = record.LoopFlagRaw;
            data[offset + 1] = record.ProgramRaw;
            data[offset + 2] = record.CategoryRaw;
            data[offset + 3] = record.NoteRaw;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4), record.PitchRaw);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 6), record.VolumeRaw);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 8), record.AliasRaw);
        }

        data.AsSpan(data.Length - N64SfxCueBank.TerminatorSize).Fill(0xFF);
        return data;
    }

    private readonly record struct SyntheticRecord(
        byte LoopFlagRaw,
        byte ProgramRaw,
        byte CategoryRaw,
        byte NoteRaw,
        ushort PitchRaw,
        ushort VolumeRaw,
        uint AliasRaw);
}
