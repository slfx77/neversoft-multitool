using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class N64SoundToolsFxBankTests(TestPaths paths)
{
    private const string SyntheticHex =
        "00000002000000010000000200000000000000000000002A" +
        "00000028FFFFFFFF0000002900000064AABB00030001";

    [Fact]
    public void Parse_ExactSynthetic_PreservesOpaqueSlicesAndLocalPointerMap()
    {
        var pointerBank = BuildPointerBank();
        var data = Convert.FromHexString(SyntheticHex);
        Assert.Equal(46, data.Length);
        Assert.Equal(
            "D096CF40D3FCA3F76042B13D5B207ABC07432085F46ABDC2F10D55081FFA2EF3",
            Hash(data));

        var bank = N64SoundToolsFxBank.Parse(data, pointerBank);

        Assert.Equal(2, bank.ComponentCount);
        Assert.Equal(1, bank.EffectCount);
        Assert.Equal(2, bank.LocalWaveCount);
        Assert.Equal(0u, bank.FlagsRaw);
        Assert.Equal(0u, bank.PointerBankAddressRaw);
        Assert.Equal(0x28, bank.ComponentDataOffset);
        Assert.Equal(0x2A, bank.WaveTableOffset);
        Assert.Equal(data.AsSpan(0x18, 16).ToArray(), bank.ComponentEntryTableRaw);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, bank.OpaqueComponentRegionRaw);
        Assert.Equal(new byte[] { 0, 3, 0, 1 }, bank.LocalWaveMapRaw);

        Assert.Equal(0x28, bank.Components[0].FxDataOffset);
        Assert.Equal(-1, bank.Components[0].DefaultPriority);
        Assert.Equal(new byte[] { 0xAA }, bank.Components[0].OpaqueData);
        Assert.Equal(0x29, bank.Components[1].FxDataOffset);
        Assert.Equal(100, bank.Components[1].DefaultPriority);
        Assert.Equal(new byte[] { 0xBB }, bank.Components[1].OpaqueData);
        Assert.Equal(new ushort[] { 3, 1 },
            bank.LocalWaveMap.Select(static binding => binding.PointerWaveIndex));
    }

    [Fact]
    public void Parse_TruncationCountsOffsetsAndMapBounds_FailClosed()
    {
        var pointerBank = BuildPointerBank();
        var valid = Convert.FromHexString(SyntheticHex);
        for (var length = 0; length < valid.Length; length++)
        {
            var truncated = valid.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => N64SoundToolsFxBank.Parse(truncated, pointerBank));
        }

        var mutations = new (string Name, Action<byte[]> Mutate)[]
        {
            ("zero components", data => WriteI32(data, 0x00, 0)),
            ("negative components", data => WriteI32(data, 0x00, -1)),
            ("component arithmetic overflow", data => WriteI32(data, 0x00, int.MaxValue)),
            ("zero effects", data => WriteI32(data, 0x04, 0)),
            ("negative effects", data => WriteI32(data, 0x04, -1)),
            ("effects exceed components", data => WriteI32(data, 0x04, 3)),
            ("zero local waves", data => WriteI32(data, 0x08, 0)),
            ("negative local waves", data => WriteI32(data, 0x08, -1)),
            ("local-wave arithmetic overflow", data => WriteI32(data, 0x08, int.MaxValue)),
            ("runtime flags", data => WriteU32(data, 0x0C, 1)),
            ("runtime PTR address", data => WriteU32(data, 0x10, 0x80000000)),
            ("odd map offset", data => WriteU32(data, 0x14, 0x29)),
            ("backward map offset", data => WriteU32(data, 0x14, 0x26)),
            ("map offset overflow", data => WriteU32(data, 0x14, uint.MaxValue)),
            ("first component gap", data => WriteU32(data, 0x18, 0x29)),
            ("first component before region", data => WriteU32(data, 0x18, 0x27)),
            ("component offset overflow", data => WriteU32(data, 0x20, uint.MaxValue)),
            ("duplicate component start", data => WriteU32(data, 0x20, 0x28)),
            ("backward component start", data => WriteU32(data, 0x20, 0x27)),
            ("component starts at map", data => WriteU32(data, 0x20, 0x2A)),
            ("map target equals PTR count", data => WriteU16(data, 0x2A, 4))
        };

        foreach (var (name, mutate) in mutations)
        {
            var data = valid.ToArray();
            mutate(data);
            var exception = Record.Exception(() => N64SoundToolsFxBank.Parse(data, pointerBank));
            Assert.IsType<InvalidDataException>(exception);
            Assert.False(string.IsNullOrWhiteSpace(exception!.Message), name);
            Assert.False(N64SoundToolsFxBank.TryParse(data, pointerBank, out var rejected), name);
            Assert.Null(rejected);
        }

        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsFxBank.Parse([.. valid, 0], pointerBank));
    }

    [Fact]
    public void Parse_OpaqueBytesPrioritiesAndDuplicateMapTargets_AreNotSemanticGates()
    {
        var data = Convert.FromHexString(SyntheticHex);
        WriteI32(data, 0x1C, int.MinValue);
        WriteI32(data, 0x24, int.MaxValue);
        data[0x28] = 0;
        data[0x29] = 0xFF;
        WriteU16(data, 0x2A, 2);
        WriteU16(data, 0x2C, 2);

        var bank = N64SoundToolsFxBank.Parse(data, BuildPointerBank());

        Assert.Equal(int.MinValue, bank.Components[0].DefaultPriority);
        Assert.Equal(int.MaxValue, bank.Components[1].DefaultPriority);
        Assert.Equal(new byte[] { 0 }, bank.Components[0].OpaqueData);
        Assert.Equal(new byte[] { 0xFF }, bank.Components[1].OpaqueData);
        Assert.Equal(new ushort[] { 2, 2 },
            bank.LocalWaveMap.Select(static binding => binding.PointerWaveIndex));
    }

    [Fact]
    public void ParsePointer_StillValidatesTheCompleteDescriptorGraphAndLoopRanges()
    {
        var (pointerData, _) = N64SoundToolsBankTests.BuildPair(pointerTail: 6, waveTail: 1);
        var pointerBank = N64SoundToolsBank.ParsePointer(pointerData);
        Assert.Equal(3, pointerBank.Waves.Count);
        Assert.Equal(2, pointerBank.Waves.Count(static wave => wave.Loop != null));

        var badBook = pointerData.ToArray();
        WriteI32(badBook, 0x30 + 0x18, 3);
        Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.ParsePointer(badBook));

        var badLoopRange = pointerData.ToArray();
        WriteU32(badLoopRange, 0x1A0 + 0xA4, 33);
        Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.ParsePointer(badLoopRange));

        var badLength = pointerData.ToArray();
        WriteU32(badLength, 0x30 + 4, 10);
        Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.ParsePointer(badLength));

        var mispackedBase = pointerData.ToArray();
        WriteU32(mispackedBase, 0xD0, 0x40);
        Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.ParsePointer(mispackedBase));
    }

    [Fact]
    public void Json_IsDeterministicExplicitlyOpaqueAndPreservesRawComponentBytes()
    {
        var pointerBank = BuildPointerBank();
        var bank = N64SoundToolsFxBank.Parse(Convert.FromHexString(SyntheticHex), pointerBank);

        var first = N64SoundToolsFxBankJsonExporter.Serialize(
            "effects.bfx", "bank.ptr.n64", N64SoundToolsFxInputResolver.CallerSuppliedBinding,
            bank, pointerBank);
        var second = N64SoundToolsFxBankJsonExporter.Serialize(
            "effects.bfx", "bank.ptr.n64", N64SoundToolsFxInputResolver.CallerSuppliedBinding,
            bank, pointerBank);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(N64SoundToolsFxBankJsonExporter.SchemaName, root.GetProperty("schema").GetString());
        Assert.Equal(N64SoundToolsFxBankJsonExporter.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("magic").ValueKind);
        Assert.Equal("opaque", root.GetProperty("bytecodeStatus").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sampleRate").ValueKind);
        Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
        Assert.Equal("callerSupplied", root.GetProperty("pointerBindingBasis").GetString());
        Assert.Equal(4, root.GetProperty("pointerWaveCount").GetInt32());
        Assert.Equal("AA", root.GetProperty("components")[0].GetProperty("opaqueDataRawHex").GetString());
        Assert.Equal(3, root.GetProperty("localWaveMap")[0].GetProperty("pointerWaveIndex").GetInt32());
        Assert.Equal(1, root.GetProperty("localWaveMap")[1].GetProperty("pointerWaveIndex").GetInt32());
    }

    [Fact]
    public void ResolverAndCommand_StandaloneRequireExplicitValidatedPointerAndNoPartialJson()
    {
        using var temp = new TempDirectory();
        var fxData = Convert.FromHexString(SyntheticHex);
        var pointerData = BuildPointerData();
        var fxPath = Path.Combine(temp.Path, "effects.bfx");
        var pointerPath = Path.Combine(temp.Path, "bank.ptr.n64");
        var jsonPath = Path.Combine(temp.Path, "effects.json");
        File.WriteAllBytes(fxPath, fxData);
        File.WriteAllBytes(pointerPath, pointerData);

        Assert.Equal(1, N64AudioFxInspectCommand.Execute(fxPath, null, jsonPath));
        Assert.False(File.Exists(jsonPath));
        Assert.Equal(0, N64AudioFxInspectCommand.Execute(fxPath, pointerPath, jsonPath));
        Assert.True(File.Exists(jsonPath));
        using (var json = JsonDocument.Parse(File.ReadAllText(jsonPath)))
        {
            Assert.Equal("effects.bfx", json.RootElement.GetProperty("fxBankSource").GetString());
            Assert.Equal("bank.ptr.n64", json.RootElement.GetProperty("pointerSource").GetString());
            Assert.Equal("callerSupplied",
                json.RootElement.GetProperty("pointerBindingBasis").GetString());
        }

        var invalidPointer = pointerData.ToArray();
        WriteU32(invalidPointer, 0x30 + 4, 10);
        var invalidPointerPath = Path.Combine(temp.Path, "invalid.ptr.n64");
        var invalidPointerOutput = Path.Combine(temp.Path, "invalid-pointer.json");
        File.WriteAllBytes(invalidPointerPath, invalidPointer);
        Assert.Equal(1,
            N64AudioFxInspectCommand.Execute(fxPath, invalidPointerPath, invalidPointerOutput));
        Assert.False(File.Exists(invalidPointerOutput));

        var malformed = fxData.ToArray();
        WriteU16(malformed, 0x2A, 4);
        var malformedPath = Path.Combine(temp.Path, "malformed.bfx");
        var absentOutput = Path.Combine(temp.Path, "malformed-absent.json");
        var sentinelPath = Path.Combine(temp.Path, "sentinel.json");
        byte[] sentinel = [0x51, 0x52, 0x53];
        File.WriteAllBytes(malformedPath, malformed);
        Assert.Equal(1,
            N64AudioFxInspectCommand.Execute(malformedPath, pointerPath, absentOutput));
        Assert.False(File.Exists(absentOutput));
        File.WriteAllBytes(sentinelPath, sentinel);
        Assert.Equal(1,
            N64AudioFxInspectCommand.Execute(malformedPath, pointerPath, sentinelPath));
        Assert.Equal(sentinel, File.ReadAllBytes(sentinelPath));
        Assert.Equal(1, N64AudioFxInspectCommand.Execute(fxPath, pointerPath, "\0"));
    }

    [Fact]
    public void RomStructuralSelection_RequiresOnePointerAndOneFullPredicateMatch()
    {
        var pointerData = BuildPointerData();
        var fxData = Convert.FromHexString(SyntheticHex);
        var pointer = new N64AssetCarver.CarvedAsset("a/bank.ptr.n64", pointerData);
        var fx = new N64AssetCarver.CarvedAsset("other/effects.bin", fxData);
        var unrelated = new N64AssetCarver.CarvedAsset("other/noise.bin", new byte[256]);

        Assert.Throws<InvalidDataException>(() => N64AudioFxInspectCommand.SelectCarvedSources([]));
        Assert.Throws<InvalidDataException>(() =>
            N64AudioFxInspectCommand.SelectCarvedSources([pointer, unrelated]));

        var selected = N64AudioFxInspectCommand.SelectCarvedSources([unrelated, fx, pointer]);
        Assert.Same(fxData, selected.FxBankData);
        Assert.Same(pointerData, selected.PointerData);
        Assert.Equal("effects.bin", selected.FxBankSource);
        Assert.Equal("bank.ptr.n64", selected.PointerSource);
        Assert.Equal("romUniqueSingleton", selected.PointerBindingBasis);

        var duplicatePointer = new N64AssetCarver.CarvedAsset("b/bank.ptr.n64", pointerData.ToArray());
        var duplicateFx = new N64AssetCarver.CarvedAsset("duplicate/effects.bin", fxData.ToArray());
        Assert.Throws<InvalidDataException>(() =>
            N64AudioFxInspectCommand.SelectCarvedSources([pointer, duplicatePointer, fx]));
        Assert.Throws<InvalidDataException>(() =>
            N64AudioFxInspectCommand.SelectCarvedSources([pointer, fx, duplicateFx]));
    }

    [CorpusFact]
    public void RomCorpus_StructuralSingletonsExactConsumptionCensusAndRouteParity()
    {
        CorpusExpected[] expectations =
        [
            new("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
                "Tony Hawk's Pro Skater (USA).z64", 2_176,
                "audiobanks/014.bin", "audiobanks/015.ptr.n64", 143,
                0x12A2, 178, 178, 108, 0x5A8, 0x11CA, "17:98,18:80", 0, 107,
                "C7528AC45A3FD17E17374F1A2F04FDB71292DEAEC86211A849A35D3E85B0E713",
                "BDBE49080074E330000A88462CB94E83DAE53D8B33FC315BBAF806D158BA4AB4",
                "DB9E946DAA42C610C2F89FC6D174CB9DA4233B6160CEE66A2EAF103BA93D500D",
                "695AEE2CAFC61F870DD9B41591EA148560D0ECA36E84F2E7ADDD6DA26C2D3BD7"),
            new("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
                "Tony Hawk's Pro Skater 2 (USA).z64", 3_962,
                "misc/000.bin", "misc/001.ptr.n64", 379,
                0x2378, 322, 322, 322, 0xA28, 0x20F4, "17:60,18:162,19:100", 0, 321,
                "717A46C5BC4A11CC6412CAF08308EC4AE6A5681CB32AE8B8D063B2ADFD0003AB",
                "F6E38CD4E7356330372993EA1DF97B519AFF57AD082B36A462269862706ACA52",
                "C1DA19D0D91A6AADAF45BE0980902E4D08765261B5D2E4166A9EAA74F2E81BDD",
                "FE5734E20EDCE3C0A30FE3E154C7B5E24CC94C2A9517708E8D82573DC3630E7D"),
            new("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
                "Tony Hawk's Pro Skater 3 (USA).z64", 3_313,
                "misc/000.bin", "misc/001.ptr.n64", 257,
                0x142A, 186, 186, 186, 0x5E8, 0x12B6, "17:95,18:66,19:25", 71, 256,
                "38892972AE1C8E6BE89EF0391963A81F387D2035EEFEE545D44C419EC6BD72BE",
                "4E7D0E1EDA5ACE7DFDBA88A28B3B0EE06BFF16D42CD77FD6AFF24EB71676E2D1",
                "728750C62D3438060FAAABEC2CB80897D08AA48096C624C6709588402EDCE191",
                "6366A56CBB05CDA813F5E92B7552364F84629649ABDED37804F3EBD561636CAE"),
            new("Spider-Man (2000-11-21, N64 - Final)",
                "Spider-Man (USA).z64", 4_286,
                "audiobanks/000.bin", "audiobanks/001.ptr.n64", 996,
                0x6ECE, 994, 994, 992, 0x1F28, 0x670E,
                "17:70,18:343,19:579,20:1,21:1", 0, 995,
                "911ED097EC9349CBE78109D60B5EC175BE69F37BDE6FA909C012C567B9C8E5C1",
                "49F1F8790C423F56322AD13609A09D52789A5342557333E4C3419021268526DC",
                "58B85CAC59E117F241561A99144054F4F8339DC97D80C52ABB07A62CAFD9E359",
                "FFE94660EBA49F25F2C568592FCA105FD8578CFA14D816214F009F7A787E2008")
        ];

        var totalAssets = 0;
        var totalComponents = 0;
        var totalEffects = 0;
        var totalLocalWaves = 0;
        var totalOpaqueBytes = 0;
        var totalNotFourAligned = 0;
        var totalOdd = 0;
        string? parityRomPath = null;
        N64SoundToolsFxInputSources? paritySources = null;

        foreach (var expected in expectations)
        {
            var romPath = paths.FindSampleFile(expected.BuildName, expected.RomName);
            Assert.SkipWhen(romPath == null, $"{expected.BuildName} ROM sample not available");
            var rom = File.ReadAllBytes(romPath!);
            Assert.True(N64AssetCarver.TryCarve(rom, out var assets));
            Assert.Equal(expected.AssetCount, assets.Count);
            totalAssets += assets.Count;

            var pointerAsset = Assert.Single(assets, static asset =>
                N64SoundToolsBank.HasPointerMagic(asset.Data));
            var pointerBank = N64SoundToolsBank.ParsePointer(pointerAsset.Data);
            var candidates = assets.Where(asset =>
                N64SoundToolsFxBank.TryParse(asset.Data, pointerBank, out _)).ToArray();
            var candidate = Assert.Single(candidates);
            var sources = N64AudioFxInspectCommand.SelectCarvedSources(assets);
            var bank = sources.FxBank;

            Assert.Equal(expected.FxPath, candidate.Path.Replace('\\', '/'));
            Assert.Equal(expected.PointerPath, pointerAsset.Path.Replace('\\', '/'));
            Assert.Equal(Path.GetFileName(expected.FxPath), sources.FxBankSource);
            Assert.Equal(Path.GetFileName(expected.PointerPath), sources.PointerSource);
            Assert.Equal("romUniqueSingleton", sources.PointerBindingBasis);
            Assert.Equal(expected.PointerWaves, pointerBank.Waves.Count);
            Assert.Equal(expected.WholeSha256, Hash(sources.FxBankData));
            Assert.Equal(expected.EntryTableSha256, Hash(bank.ComponentEntryTableRaw));
            Assert.Equal(expected.OpaqueSha256, Hash(bank.OpaqueComponentRegionRaw));
            Assert.Equal(expected.MapSha256, Hash(bank.LocalWaveMapRaw));
            Assert.Equal(expected.SerializedSize, bank.SerializedSize);
            Assert.Equal(expected.Components, bank.ComponentCount);
            Assert.Equal(expected.Effects, bank.EffectCount);
            Assert.Equal(expected.LocalWaves, bank.LocalWaveCount);
            Assert.Equal(expected.HeaderEnd, bank.ComponentDataOffset);
            Assert.Equal(expected.MapOffset, bank.WaveTableOffset);
            Assert.Equal(expected.ExpectedLengthHistogram, LengthHistogram(bank.Components));
            Assert.Equal(expected.MapMinimum,
                bank.LocalWaveMap.Min(static binding => binding.PointerWaveIndex));
            Assert.Equal(expected.MapMaximum,
                bank.LocalWaveMap.Max(static binding => binding.PointerWaveIndex));
            Assert.All(bank.Components, static component => Assert.Equal(100, component.DefaultPriority));

            if (expected.RomName.StartsWith("Spider-Man", StringComparison.Ordinal))
            {
                Assert.Equal(334, bank.LocalWaveMap[334].PointerWaveIndex);
                Assert.Equal(339, bank.LocalWaveMap[335].PointerWaveIndex);
                Assert.Equal(new byte[] { 0xFF, 0xFF, 0x80, 0xE2 },
                    bank.Components[^1].OpaqueData.TakeLast(4));
            }

            totalComponents += bank.ComponentCount;
            totalEffects += bank.EffectCount;
            totalLocalWaves += bank.LocalWaveCount;
            totalOpaqueBytes += bank.OpaqueComponentRegionRaw.Count;
            totalNotFourAligned += bank.Components.Count(static component =>
                component.FxDataOffset % 4 != 0);
            totalOdd += bank.Components.Count(static component =>
                (component.FxDataOffset & 1) != 0);

            if (expected.RomName.StartsWith("Tony Hawk's Pro Skater (", StringComparison.Ordinal))
            {
                parityRomPath = romPath;
                paritySources = sources;
            }
        }

        Assert.Equal(13_737, totalAssets);
        Assert.Equal(1_680, totalComponents);
        Assert.Equal(1_680, totalEffects);
        Assert.Equal(1_608, totalLocalWaves);
        Assert.Equal(30_626, totalOpaqueBytes);
        Assert.Equal(1_250, totalNotFourAligned);
        Assert.Equal(839, totalOdd);
        Assert.NotNull(parityRomPath);
        Assert.NotNull(paritySources);

        using var temp = new TempDirectory();
        var fxPath = Path.Combine(temp.Path, paritySources!.FxBankSource);
        var pointerPath = Path.Combine(temp.Path, paritySources.PointerSource);
        var romJsonPath = Path.Combine(temp.Path, "rom.json");
        var explicitJsonPath = Path.Combine(temp.Path, "explicit.json");
        File.WriteAllBytes(fxPath, paritySources.FxBankData);
        File.WriteAllBytes(pointerPath, paritySources.PointerData);
        Assert.Equal(0, N64AudioFxInspectCommand.Execute(parityRomPath!, null, romJsonPath));
        Assert.Equal(0,
            N64AudioFxInspectCommand.Execute(fxPath, pointerPath, explicitJsonPath));

        var romJson = JsonNode.Parse(File.ReadAllText(romJsonPath))!.AsObject();
        var explicitJson = JsonNode.Parse(File.ReadAllText(explicitJsonPath))!.AsObject();
        Assert.Equal("romUniqueSingleton", romJson["pointerBindingBasis"]!.GetValue<string>());
        Assert.Equal("callerSupplied", explicitJson["pointerBindingBasis"]!.GetValue<string>());
        romJson["pointerBindingBasis"] = "normalized";
        explicitJson["pointerBindingBasis"] = "normalized";
        Assert.True(JsonNode.DeepEquals(romJson, explicitJson));
    }

    private static N64SoundToolsPointerBank BuildPointerBank() =>
        N64SoundToolsBank.ParsePointer(BuildPointerData());

    private static byte[] BuildPointerData()
    {
        const int waveCount = 4;
        const int pointerTableOffset = 0x2B0;
        const int baseNoteOffset = 0x2C0;
        const int fineTuneOffset = 0x2C4;
        const int logicalSize = 0x2D4;
        var pointer = new byte[logicalSize];
        "N64 PtrTablesV2\0"u8.CopyTo(pointer);
        WriteU32(pointer, 0x20, waveCount);
        WriteU32(pointer, 0x24, baseNoteOffset);
        WriteU32(pointer, 0x28, fineTuneOffset);
        WriteU32(pointer, 0x2C, pointerTableOffset);

        for (var i = 0; i < waveCount; i++)
        {
            var descriptor = 0x30 + i * 0xA0;
            WriteU32(pointer, descriptor, (uint)(0x10 + i * 0x10));
            WriteU32(pointer, descriptor + 4, 9);
            WriteU32(pointer, descriptor + 0x10, (uint)(descriptor + 0x18));
            WriteI32(pointer, descriptor + 0x18, 2);
            WriteI32(pointer, descriptor + 0x1C, 4);
            WriteU32(pointer, pointerTableOffset + i * 4, (uint)descriptor);
        }

        return pointer;
    }

    private static string LengthHistogram(IEnumerable<N64SoundToolsFxComponent> components) =>
        string.Join(",", components
            .GroupBy(static component => component.OpaqueData.Count)
            .OrderBy(static group => group.Key)
            .Select(static group => $"{group.Key}:{group.Count()}"));

    private static string Hash(IEnumerable<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data.ToArray()));

    private static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);

    private static void WriteI32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);

    private static void WriteU16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-fx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed record CorpusExpected(
        string BuildName,
        string RomName,
        int AssetCount,
        string FxPath,
        string PointerPath,
        int PointerWaves,
        int SerializedSize,
        int Components,
        int Effects,
        int LocalWaves,
        int HeaderEnd,
        int MapOffset,
        string ExpectedLengthHistogram,
        ushort MapMinimum,
        ushort MapMaximum,
        string WholeSha256,
        string EntryTableSha256,
        string OpaqueSha256,
        string MapSha256);
}
