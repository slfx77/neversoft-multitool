using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class N64SoundToolsBankTests(TestPaths paths)
{
    [Fact]
    public void Parse_ExactGraphAndWaveRanges_PreservesRawInspectionFields()
    {
        var (pointerData, waveData) = BuildPair(pointerTail: 6, waveTail: 1);

        var bank = N64SoundToolsBank.Parse(pointerData, waveData);

        Assert.Equal(pointerData.Length, bank.PointerBank.SerializedSize);
        Assert.Equal(pointerData.Length - 6, bank.PointerBank.LogicalSize);
        Assert.Equal(new byte[] { 0xF4, 0x00, 0xF5 },
            bank.PointerBank.BaseNotes.Select(static value => value.Raw));
        Assert.Equal(new sbyte[] { -12, 0, -11 },
            bank.PointerBank.BaseNotes.Select(static value => value.CoarseTuneRawSigned));
        Assert.Equal(new sbyte[] { -60, -48, -59 },
            bank.PointerBank.BaseNotes.Select(static value => value.RuntimeBasePitchOffsetSemitones));
        Assert.Single(bank.PointerBank.BaseNoteAlignmentPadding);
        Assert.Equal(new sbyte[] { 127, -128, 0 },
            bank.PointerBank.FineTuneCells.Select(static value => value.FineTuneCents));
        Assert.Equal(new byte[] { 0x80, 0, 0, 0 }, bank.PointerBank.FineTuneCells[1].RawBytes);
        Assert.Equal(0u, bank.PointerBank.FlagsRaw);
        Assert.Equal([0u, 0u, 0u], bank.PointerBank.WaveBankNameRaw);
        Assert.Equal(6, bank.PointerBank.OuterTrailingPadding.Count);

        var waves = bank.PointerBank.Waves;
        Assert.Equal(3, waves.Count);
        Assert.Equal(0x30, waves[0].DescriptorOffset);
        Assert.Equal(0xD0, waves[1].DescriptorOffset);
        Assert.Equal(0x1A0, waves[2].DescriptorOffset);
        Assert.Null(waves[0].Loop);
        Assert.Equal(4, waves[1].DescriptorAlignmentPadding.Count);
        Assert.Empty(waves[2].DescriptorAlignmentPadding);
        Assert.Equal(0x26C, bank.PointerBank.DescriptorPointerTableOffset);
        Assert.Equal(uint.MaxValue, waves[1].Loop!.CountRaw);
        Assert.Equal(2u, waves[2].Loop!.CountRaw);
        Assert.Equal(64, waves[2].Book.Coefficients.Count);

        Assert.Equal(waveData.Length, bank.WaveBank.SerializedSize);
        Assert.Equal(14, bank.WaveBank.WaveAlignmentPadding[0].Length);
        Assert.Equal(7, bank.WaveBank.WaveAlignmentPadding[1].Length);
        Assert.Empty(bank.WaveBank.WaveAlignmentPadding[2]);
        Assert.Single(bank.WaveBank.TrailingPadding);
    }

    [Fact]
    public void Parse_AnyTruncationBeforeLogicalEnds_IsRejected()
    {
        var (pointerData, waveData) = BuildPair(pointerTail: 0, waveTail: 0);

        for (var length = 0; length < pointerData.Length; length++)
        {
            var truncated = pointerData.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.Parse(truncated, waveData));
        }

        for (var length = 0; length < waveData.Length; length++)
        {
            var truncated = waveData.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.Parse(pointerData, truncated));
        }
    }

    [Fact]
    public void Parse_MalformedPointerGraphBooksLoopsAndPadding_FailClosed()
    {
        var mutations = new (string Name, Action<byte[]> Mutate)[]
        {
            ("magic", data => data[0] ^= 0xFF),
            ("flags", data => data[0x10] = 1),
            ("wbk_name", data => data[0x14] = 1),
            ("zero wave count", data => WriteU32(data, 0x20, 0)),
            ("wave count exceeds graph bound", data => WriteU32(data, 0x20, 4)),
            ("overflow wave count", data => WriteU32(data, 0x20, uint.MaxValue)),
            ("base-note offset", data => WriteU32(data, 0x24, ReadU32(data, 0x24) + 4)),
            ("base-note offset overflow", data => WriteU32(data, 0x24, uint.MaxValue)),
            ("fine-tune offset overflow", data => WriteU32(data, 0x28, uint.MaxValue)),
            ("pointer table overflow", data => WriteU32(data, 0x2C, uint.MaxValue)),
            ("descriptor pointer", data => WriteU32(data, 0x26C + 4, 0xD4)),
            ("duplicate descriptor pointer", data => WriteU32(data, 0x26C + 4, 0x30)),
            ("type", data => data[0x30 + 8] = 1),
            ("flags", data => data[0x30 + 9] = 1),
            ("descriptor ABI u16 pad", data => data[0x30 + 0x0A] = 1),
            ("descriptor trailing pad", data => data[0x30 + 0x14] = 1),
            ("book pointer", data => WriteU32(data, 0x30 + 0x10, 0x30 + 0x1C)),
            ("book order", data => WriteU32(data, 0x30 + 0x18, 3)),
            ("book predictor count", data => WriteU32(data, 0x30 + 0x1C, 3)),
            ("loop pointer", data => WriteU32(data, 0xD0 + 0x0C, 0xD0 + 0xA4)),
            ("descriptor alignment", data => data[0xD0 + 0xCC] = 1),
            ("base-note alignment", data => data[0x27B] = 1),
            ("fine-tune low pad 1", data => data[0x27C + 1] = 1),
            ("fine-tune low pad 2", data => data[0x27C + 2] = 1),
            ("fine-tune low pad 3", data => data[0x27C + 3] = 1),
            ("outer tail", data => data[^1] = 1),
            ("empty loop", data => WriteU32(data, 0xD0 + 0xA4, ReadU32(data, 0xD0 + 0xA0))),
            ("loop past wave", data => WriteU32(data, 0x1A0 + 0xA4, 33))
        };

        foreach (var (name, mutate) in mutations)
        {
            var (pointerData, waveData) = BuildPair(pointerTail: 6, waveTail: 1);
            mutate(pointerData);
            var exception = Record.Exception(() => N64SoundToolsBank.Parse(pointerData, waveData));
            Assert.IsType<InvalidDataException>(exception);
            Assert.False(string.IsNullOrWhiteSpace(exception!.Message), name);
        }

        var (validPointer, validWave) = BuildPair(pointerTail: 6, waveTail: 1);
        Array.Resize(ref validPointer, validPointer.Length + 3);
        Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.Parse(validPointer, validWave));
    }

    [Fact]
    public void Parse_MalformedWavePackingAndPadding_FailClosed()
    {
        var mutations = new (string Name, Action<byte[], byte[]> Mutate)[]
        {
            ("magic", (_, wave) => wave[0] ^= 0xFF),
            ("first base", (pointer, _) => WriteU32(pointer, 0x30, 0x20)),
            ("frame length", (pointer, _) => WriteU32(pointer, 0x30 + 4, 17)),
            ("inter-wave padding", (_, wave) => wave[0x22] = 1),
            ("tail", (_, wave) => wave[^1] = 1)
        };

        foreach (var (name, mutate) in mutations)
        {
            var (pointerData, waveData) = BuildPair(pointerTail: 6, waveTail: 1);
            mutate(pointerData, waveData);
            var exception = Record.Exception(() => N64SoundToolsBank.Parse(pointerData, waveData));
            Assert.IsType<InvalidDataException>(exception);
            Assert.False(string.IsNullOrWhiteSpace(exception!.Message), name);
        }

        var (validPointer, validWave) = BuildPair(pointerTail: 6, waveTail: 1);
        Array.Resize(ref validWave, validWave.Length + 15);
        Assert.Throws<InvalidDataException>(() => N64SoundToolsBank.Parse(validPointer, validWave));
    }

    [Fact]
    public void Json_IsDeterministicNumericAndExplicitlyLeavesSampleRateUnknown()
    {
        var (pointerData, waveData) = BuildPair(pointerTail: 6, waveTail: 1);
        var bank = N64SoundToolsBank.Parse(pointerData, waveData);

        var first = N64SoundToolsBankJsonExporter.Serialize("bank.ptr.n64", "waves.wbk", bank);
        var second = N64SoundToolsBankJsonExporter.Serialize("bank.ptr.n64", "waves.wbk", bank);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(N64SoundToolsBankJsonExporter.SchemaName, root.GetProperty("schema").GetString());
        Assert.Equal(N64SoundToolsBankJsonExporter.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("N64 Sound Tools PTR/WBK", root.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sampleRate").ValueKind);
        Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
        Assert.Equal(3, root.GetProperty("waveCount").GetInt32());
        Assert.Equal(2, root.GetProperty("loopCount").GetInt32());
        var baseNotes = root.GetProperty("pointerBank").GetProperty("baseNotes");
        Assert.Equal(JsonValueKind.Array, baseNotes.ValueKind);
        Assert.Equal(0xF4, baseNotes[0].GetProperty("raw").GetInt32());
        Assert.Equal(-12, baseNotes[0].GetProperty("coarseTuneRawSigned").GetInt32());
        Assert.Equal(-60, baseNotes[0].GetProperty("runtimeBasePitchOffsetSemitones").GetInt32());
        Assert.Equal(2u, root.GetProperty("waves")[2].GetProperty("loop")
            .GetProperty("countRaw").GetUInt32());
        Assert.Equal(2u, root.GetProperty("waves")[2].GetProperty("encodedFrameCount").GetUInt32());
        Assert.Equal(32, root.GetProperty("waves")[2].GetProperty("decodedSampleCapacity").GetInt64());
        Assert.Equal(0xF5, root.GetProperty("waves")[2].GetProperty("baseNoteRaw").GetByte());
        Assert.Equal(-11, root.GetProperty("waves")[2].GetProperty("coarseTuneRawSigned").GetSByte());
        Assert.Equal(-128, root.GetProperty("waves")[1].GetProperty("fineTuneCents").GetSByte());
        Assert.Equal(0x80, root.GetProperty("waves")[1].GetProperty("detuneCellRaw")[0].GetInt32());
        Assert.Equal(-49.28,
            root.GetProperty("waves")[1].GetProperty("runtimePitchOffsetSemitones").GetDouble(),
            precision: 12);
    }

    [Fact]
    public void Command_StandalonePointerRequiresExplicitWaveAndWritesManifest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "NsMultitool_N64SoundTools_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var (pointerData, waveData) = BuildPair(pointerTail: 6, waveTail: 1);
            var pointerPath = Path.Combine(tempRoot, "bank.ptr.n64");
            var wavePath = Path.Combine(tempRoot, "waves.wbk");
            var jsonPath = Path.Combine(tempRoot, "inspection.json");
            File.WriteAllBytes(pointerPath, pointerData);
            File.WriteAllBytes(wavePath, waveData);

            Assert.Equal(1, N64AudioInspectCommand.Execute(pointerPath, null, jsonPath));
            Assert.False(File.Exists(jsonPath));
            Assert.Equal(0, N64AudioInspectCommand.Execute(pointerPath, wavePath, jsonPath));
            Assert.True(File.Exists(jsonPath));

            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
            Assert.Equal("bank.ptr.n64", json.RootElement.GetProperty("pointerSource").GetString());
            Assert.Equal("waves.wbk", json.RootElement.GetProperty("waveSource").GetString());

            var mismatchedWave = Path.Combine(tempRoot, "mismatched.wbk");
            var mismatchedJson = Path.Combine(tempRoot, "mismatched.json");
            var badWaveData = waveData.ToArray();
            badWaveData[0x22] = 1;
            File.WriteAllBytes(mismatchedWave, badWaveData);
            Assert.Equal(1, N64AudioInspectCommand.Execute(pointerPath, mismatchedWave, mismatchedJson));
            Assert.False(File.Exists(mismatchedJson));
            Assert.Equal(1, N64AudioInspectCommand.Execute(pointerPath, wavePath, "\0"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void RomPairSelection_RequiresExactlyOneCandidateOfEachMagic()
    {
        var (pointerData, waveData) = BuildPair(pointerTail: 6, waveTail: 1);
        var pointer = new N64AssetCarver.CarvedAsset("a/shared.ptr.n64", pointerData);
        var wave = new N64AssetCarver.CarvedAsset("audio/000.bin", waveData);

        Assert.Throws<InvalidDataException>(() => N64AudioInspectCommand.SelectCarvedPair([]));
        Assert.Throws<InvalidDataException>(() => N64AudioInspectCommand.SelectCarvedPair([pointer]));
        Assert.Throws<InvalidDataException>(() => N64AudioInspectCommand.SelectCarvedPair([wave]));

        var selected = N64AudioInspectCommand.SelectCarvedPair([pointer, wave]);
        Assert.Same(pointerData, selected.PointerData);
        Assert.Same(waveData, selected.WaveData);
        Assert.Equal("shared.ptr.n64", selected.PointerSource);
        Assert.Equal("000.bin", selected.WaveSource);

        var duplicatePointer = new N64AssetCarver.CarvedAsset("b/shared.ptr.n64", pointerData.ToArray());
        var duplicateWave = new N64AssetCarver.CarvedAsset("other/000.bin", waveData.ToArray());
        Assert.Throws<InvalidDataException>(() =>
            N64AudioInspectCommand.SelectCarvedPair([pointer, duplicatePointer, wave]));
        Assert.Throws<InvalidDataException>(() =>
            N64AudioInspectCommand.SelectCarvedPair([pointer, wave, duplicateWave]));
    }

    [CorpusFact]
    public void Parse_RomCorpus_ContentPairsExactlyAndPinsCensusAndRouteParity()
    {
        CorpusExpected[] expectations =
        [
            new("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
                "Tony Hawk's Pro Skater (USA).z64", "015.ptr.n64", 143, 33,
                25_800, 2_640_242, 0, 1, 0x5FC0, 0x61FC, 0x628C, "F4:143",
                "A45EFBBE10BB2F54BD626ADE1D95B1032544A20562A7C2353570E9DB2146F2EF",
                "16243E88669DE368C9B8A807DD1A7477807C2122BE4333D35A66F22604B00172"),
            new("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
                "Tony Hawk's Pro Skater 2 (USA).z64", "001.ptr.n64", 379, 72,
                67_556, 7_215_274, 0, 0, 0xFA90, 0x1007C, 0x101F8, "00:379",
                "562583623FDA18068DBD4CBC5CEFFA20CCBBFF2669A74DDDC936E940E6D8FE40",
                "E5A60BEC5A29D6DE79A8CC9D9189829498F3BA4658A5FD7E366FBF3380199D58"),
            new("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
                "Tony Hawk's Pro Skater 3 (USA).z64", "001.ptr.n64", 257, 50,
                45_884, 6_643_404, 0, 6, 0xAA30, 0xAE34, 0xAF38, "00:257",
                "5FD316BA0838791035D99201D479A21F880D41FC42B9E5BF8C7DF76440A27150",
                "1540FA6B42569689A001501F3021255450BB889ED21C72FBED52D2706E223E0D"),
            new("Spider-Man (2000-11-21, N64 - Final)",
                "Spider-Man (USA).z64", "001.ptr.n64", 996, 165,
                176_294, 14_032_866, 6, 1, 0x28D9C, 0x29D2C, 0x2A110, "F4:510,F5:486",
                "E466A46E88B3C94B4779459DE15C2D1D95DAE7AE2133BE338F3718FC97EB9140",
                "BD3B492AC36FD015F102A6634134DE5430F05038AB2BAE8E9CF7312336F0980E")
        ];

        var totalWaves = 0;
        var totalLoops = 0;
        string? thps1RomPath = null;
        N64SoundToolsInputSources? thps1Sources = null;
        foreach (var expected in expectations)
        {
            var romPath = paths.FindSampleFile(expected.BuildName, expected.RomName);
            Assert.SkipWhen(romPath == null, $"{expected.BuildName} ROM sample not available");

            // This is the production ROM route: carving must find exactly one
            // PTR magic and one WBK magic, with no path-name pairing fallback.
            var sources = N64AudioInspectCommand.ResolveSources(romPath!, wavePath: null);
            Assert.Equal(expected.PointerLeaf, sources.PointerSource);
            Assert.Equal("000.bin", sources.WaveSource);
            Assert.Equal(expected.PointerSha256, Convert.ToHexString(SHA256.HashData(sources.PointerData)));
            Assert.Equal(expected.WaveSha256, Convert.ToHexString(SHA256.HashData(sources.WaveData)));

            var bank = N64SoundToolsBank.Parse(sources.PointerData, sources.WaveData);
            var pointer = bank.PointerBank;
            Assert.Equal(expected.Waves, pointer.Waves.Count);
            Assert.Equal(expected.Loops, pointer.Waves.Count(static item => item.Loop != null));
            Assert.Equal(expected.PointerSize, pointer.SerializedSize);
            Assert.Equal(expected.WaveSize, bank.WaveBank.SerializedSize);
            Assert.Equal(expected.PointerTail, pointer.OuterTrailingPadding.Count);
            Assert.Equal(expected.WaveTail, bank.WaveBank.TrailingPadding.Count);
            Assert.Equal(expected.PointerTable, pointer.DescriptorPointerTableOffset);
            Assert.Equal(expected.BaseNoteTable, pointer.BaseNoteTableOffset);
            Assert.Equal(expected.FineTuneWorkspace, pointer.FineTuneWorkspaceOffset);
            Assert.Equal(expected.BaseNoteHistogramText,
                ByteHistogram(pointer.BaseNotes.Select(static value => value.Raw)));
            Assert.All(pointer.BaseNotes, static value => Assert.Equal(
                unchecked((sbyte)value.Raw), value.CoarseTuneRawSigned));
            Assert.All(pointer.BaseNotes, static value => Assert.Equal(
                unchecked((sbyte)((value.Raw - 48) & 0xFF)),
                value.RuntimeBasePitchOffsetSemitones));
            Assert.All(pointer.FineTuneCells, static value =>
            {
                Assert.Equal(0, value.FineTuneCents);
                Assert.Equal(new byte[] { 0, 0, 0, 0 }, value.RawBytes);
            });
            Assert.Equal(0u, pointer.FlagsRaw);
            Assert.Equal([0u, 0u, 0u], pointer.WaveBankNameRaw);
            Assert.All(pointer.Waves, static item => Assert.Equal((byte)0, item.TypeRaw));
            Assert.All(pointer.Waves, static item => Assert.Equal((byte)0, item.FlagsRaw));
            Assert.All(pointer.Waves, static item => Assert.Equal(2, item.Book.Order));
            Assert.All(pointer.Waves, static item => Assert.Equal(4, item.Book.PredictorCount));
            Assert.All(pointer.Waves.Where(static item => item.Loop != null), static item =>
            {
                Assert.Equal(uint.MaxValue, item.Loop!.CountRaw);
                Assert.True(item.Loop.Start < item.Loop.End);
                Assert.True(item.Loop.End <= (long)(item.WaveLength / 9) * 16);
            });

            totalWaves += pointer.Waves.Count;
            totalLoops += pointer.Waves.Count(static item => item.Loop != null);
            if (expected.RomName.StartsWith("Tony Hawk's Pro Skater (", StringComparison.Ordinal))
            {
                thps1RomPath = romPath;
                thps1Sources = sources;
            }

            if (expected.RomName.StartsWith("Spider-Man", StringComparison.Ordinal))
            {
                var last = pointer.Waves[^1];
                Assert.Equal(0x28CD0, last.DescriptorOffset);
                Assert.NotNull(last.Loop);
                Assert.Equal(0x28D9C, pointer.DescriptorPointerTableOffset);
                Assert.Equal(0x0C, pointer.DescriptorPointerTableOffset & 0x0F);
                Assert.Equal(last.DescriptorOffset + 0xCC, pointer.DescriptorPointerTableOffset);
                Assert.Equal(0x2B0A0, pointer.LogicalSize);
                Assert.Equal(0x2B0A6, pointer.SerializedSize);
            }
        }

        Assert.Equal(1_775, totalWaves);
        Assert.Equal(320, totalLoops);
        Assert.NotNull(thps1RomPath);
        Assert.NotNull(thps1Sources);

        // The ROM and explicit standalone command routes produce byte-identical
        // schemas when fed the exact same carved leaves; provenance is normalized
        // to leaf names rather than route-specific ROM::path strings.
        var tempRoot = Path.Combine(Path.GetTempPath(), "NsMultitool_N64SoundToolsCorpus_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var pointerPath = Path.Combine(tempRoot, thps1Sources!.PointerSource);
            var wavePath = Path.Combine(tempRoot, thps1Sources.WaveSource);
            var romJson = Path.Combine(tempRoot, "rom.json");
            var explicitJson = Path.Combine(tempRoot, "explicit.json");
            File.WriteAllBytes(pointerPath, thps1Sources.PointerData);
            File.WriteAllBytes(wavePath, thps1Sources.WaveData);
            Assert.Equal(0, N64AudioInspectCommand.Execute(thps1RomPath!, null, romJson));
            Assert.Equal(0, N64AudioInspectCommand.Execute(pointerPath, wavePath, explicitJson));
            Assert.Equal(File.ReadAllBytes(romJson), File.ReadAllBytes(explicitJson));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    internal static (byte[] Pointer, byte[] Wave) BuildPair(int pointerTail, int waveTail)
    {
        const int waveCount = 3;
        const int firstDescriptor = 0x30;
        const int secondDescriptor = 0xD0;
        const int thirdDescriptor = 0x1A0;
        const int pointerTable = 0x26C; // final looped record ends raw at D+0xCC, without align16
        const int baseNoteTable = pointerTable + waveCount * 4;
        const int fineTuneWorkspace = 0x27C;
        const int logicalPointerSize = fineTuneWorkspace + waveCount * 4;

        var pointer = new byte[logicalPointerSize + pointerTail];
        "N64 PtrTablesV2\0"u8.CopyTo(pointer);
        WriteU32(pointer, 0x20, waveCount);
        WriteU32(pointer, 0x24, baseNoteTable);
        WriteU32(pointer, 0x28, fineTuneWorkspace);
        WriteU32(pointer, 0x2C, pointerTable);
        WriteDescriptor(pointer, firstDescriptor, 0x10, 18, loop: null, coefficientSeed: 0);
        WriteDescriptor(pointer, secondDescriptor, 0x30, 9,
            new SyntheticLoop(0, 16, uint.MaxValue), coefficientSeed: 100);
        WriteDescriptor(pointer, thirdDescriptor, 0x40, 18,
            new SyntheticLoop(4, 32, 2), coefficientSeed: 200);
        WriteU32(pointer, pointerTable, firstDescriptor);
        WriteU32(pointer, pointerTable + 4, secondDescriptor);
        WriteU32(pointer, pointerTable + 8, thirdDescriptor);
        pointer[baseNoteTable] = 0xF4;
        pointer[baseNoteTable + 1] = 0x00;
        pointer[baseNoteTable + 2] = 0xF5;
        pointer[fineTuneWorkspace] = 0x7F;
        pointer[fineTuneWorkspace + 4] = 0x80;

        const int lastWaveEnd = 0x52;
        var wave = new byte[lastWaveEnd + waveTail];
        "N64 WaveTables \0"u8.CopyTo(wave);
        wave.AsSpan(0x10, 18).Fill(0x11);
        wave.AsSpan(0x30, 9).Fill(0x22);
        wave.AsSpan(0x40, 18).Fill(0x33);
        return (pointer, wave);
    }

    private static void WriteDescriptor(
        byte[] data,
        int offset,
        uint waveBase,
        uint waveLength,
        SyntheticLoop? loop,
        int coefficientSeed)
    {
        WriteU32(data, offset, waveBase);
        WriteU32(data, offset + 4, waveLength);
        WriteU32(data, offset + 0x0C, loop is null ? 0u : (uint)(offset + 0xA0));
        WriteU32(data, offset + 0x10, (uint)(offset + 0x18));
        WriteU32(data, offset + 0x18, 2);
        WriteU32(data, offset + 0x1C, 4);
        for (var i = 0; i < 64; i++)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset + 0x20 + i * 2),
                (short)(coefficientSeed + i));

        if (loop is not { } value)
            return;
        WriteU32(data, offset + 0xA0, value.Start);
        WriteU32(data, offset + 0xA4, value.End);
        WriteU32(data, offset + 0xA8, value.Count);
        for (var i = 0; i < 16; i++)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset + 0xAC + i * 2), (short)(-i));
    }

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));

    private static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);

    private static string ByteHistogram(IEnumerable<byte> values) =>
        string.Join(",", values
            .GroupBy(static value => value)
            .OrderBy(static group => group.Key)
            .Select(static group => $"{group.Key:X2}:{group.Count()}"));

    private readonly record struct SyntheticLoop(uint Start, uint End, uint Count);

    private sealed record CorpusExpected(
        string BuildName,
        string RomName,
        string PointerLeaf,
        int Waves,
        int Loops,
        int PointerSize,
        int WaveSize,
        int PointerTail,
        int WaveTail,
        int PointerTable,
        int BaseNoteTable,
        int FineTuneWorkspace,
        string BaseNoteHistogramText,
        string PointerSha256,
        string WaveSha256);
}
