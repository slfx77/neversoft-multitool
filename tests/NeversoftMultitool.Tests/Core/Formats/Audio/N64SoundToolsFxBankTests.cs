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
    private const int Opcode81HandlerLength = 48;
    private const string Opcode81HandlerHex =
        "90A6000030C200801040000624A5000190A3000024A5000130C2007F0002120000623025" +
        "A48600AE03E0000800A01021";
    private const string Opcode81HandlerSha256 =
        "667809B33945016234DBBDEF6F2E9CD72FCCDF2B0A2B240F5004DCA256673834";
    private const int Thps2LocalToPointerJoinOffset = 0xBE3E0;
    private const int Thps2LocalToPointerJoinLength = 76;
    private const string Thps2LocalToPointerJoinHex =
        "8E220074963000AE10400004000000008C430020080353CA001010408E2200788C430014" +
        "001010400043102194500000922200D71440001B240200018E83002C0010108000431021" +
        "8C420000";
    private const string Thps2LocalToPointerJoinSha256 =
        "1E9BCF39BACBE900616BC0C44793966D9371A58EA5139A55226BEBC2A40E13BE";
    private const int Thps2DispatchTableBaseConstructionOffset = 0xBCD18;
    private const string Thps2DispatchTableBaseConstructionHex = "3C0280102454E4E0";
    private const int Thps2DispatcherOffset = 0xBCD48;
    private const string Thps2DispatcherHex =
        "8E05FFD080A2000090A300000441000F24A20001240200AB146200040220202190A20001" +
        "10560039000000003062007F00021080005410218C4200000040F80924A50001";
    private const string Thps2DispatcherSha256 =
        "E6F56CDCA9797E999D322C8E1DE68ADAD618E14736D7A9D2009D3DCF7080A9B8";
    private const int Thps2DispatchTableOffset = 0xE79C0;
    private const string Thps2FirstDispatchEntriesHex = "800D2AE0800D2B00";
    private const int SpiderOpcode95TableEntryOffset = 0xF2AF8;
    private const int SpiderOpcode95HandlerOffset = 0xC4B5C;
    private const string SpiderOpcode95HandlerHeadHex = "0080302190A2000024A50001";
    private const string SpiderOpcode95HandlerHeadSha256 =
        "5B3A41A639E80FE0920F9123F3FE835BBD5C8BFD9B0A3C64F6861F33955E93E8";
    private const int Opcode80HandlerLength = 0x20;
    private const string Opcode80HandlerHex =
        "00001021AC800038AC800034AC800074AC800078AC80004403E00008AC800008";
    private const string Opcode80HandlerSha256 =
        "4A09AD2D7D57BE3533402565DD7002DDF02E665E5A5F00049118A9E7F9F591CC";
    private const int Opcode84HandlerLength = 0x128;
    private const int Opcode95HandlerLength = 0xA8;
    private const int Opcode96HandlerLength = 0x9C;
    private const string Opcode96HandlerHex =
        "908200DB2446FFFF0086382190E30120240200FF106200072462FFFFA0E20120304200FF" +
        "1440000300000000A08600DB2406FFFF04C0001700061080008210218C4300F08C4500E0" +
        "AC8300388C42010000861821AC82003490620124A08200BC90620128C482006C44820000" +
        "468000200006104000821021E48000709443011046020002A48300A294420118A48200A4" +
        "E480002403E0000800A01021";
    private const string Opcode96HandlerSha256 =
        "CEFAAB116C146565DB314A58042E2578B18D625CD193E5F11D233246088DA3EC";
    private const int Opcode9CHandlerLength = 0x14;
    private const string Opcode9CHandlerHex =
        "90A2000000021042A08200BD03E0000824A20001";
    private const string Opcode9CHandlerSha256 =
        "68AAF91DBA53410A35413F6671F46119F3AB7047E9AB1233D0BF02DA33E7B474";
    private const int OpcodeA6HandlerLength = 0x10;
    private const string OpcodeA6HandlerHex =
        "90A20000A08200BC03E0000824A20001";
    private const string OpcodeA6HandlerSha256 =
        "0C5D4970CA0ED79EEA132B3E6B1001D9CD90B2597485E54C3018DCF1A9A60D70";
    private const int BfxProcessLoopSliceLength = 0x4C;
    private const int InitialNoteLengthSliceLength = 0xFC;
    private const int RestSentinelSliceLength = 12;
    private const string RestSentinelSliceHex = "240200601242007C00000000";
    private const string RestSentinelSliceSha256 =
        "4EFFC9E390269AAB4D81E687C03560CA9B165F7F16820E86617BC755B0354A87";
    private const int IndefiniteLengthGateSliceLength = 0x40;
    private const int ChannelInitializerLength = 0x12C;
    private const int EffectStartPrefixLength = 0x84;
    // Official Nintendo 64 Sound Tools WIN95 v3.14 source provenance. These
    // hashes license the opcode/field names used by the raw cartridge oracles.
    private const string SoundTools314IsoSha256 =
        "FC75FCBABC5EA146CAE6AC05286E2C1F2E27F8417035F923B16BE3FA058B0190";
    private const string SoundTools314ArchiveSha256 =
        "F0F8B9A0D5C921CA208CABB6794B22D8077810140116D66CD3BB9B0D98CE5E9F";
    private const string SoundTools314PlayerSha256 =
        "F3E3416B265E10D5ED178F3BAD2C4ECF7A59AA775D1F33288FA22F2D90C5F371";
    private const string SoundTools314PlayerCommandsSha256 =
        "D9D694D288749ECC1E361FAE46226FA6A5B8A964CBFD153B638E61EE84B855AA";
    private const string SoundTools314DataHeaderSha256 =
        "3BE46F7BFB00800E7E61318FCAC61D0516129A5C369B62D69C68B64DF875623E";
    private const int NormalizedInitialEventCorpusLength = 48_857;
    private const string NormalizedInitialEventCorpusSha256 =
        "55FCF87C581C757475DE680BFBFFC94B7C12413B49B84B772E40E668051326DA";

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
    public void InitialWaveResolver_DecodesRecognizedPrefixesAndJoinsPointerDescriptors()
    {
        var pointerBank = BuildPointerBank();
        byte[][] payloads =
        [
            [0x81, 0x7F],
            [0x81, 0x80, 0x80],
            [0x81, 0x80, 0xFF],
            [0x81, 0x81, 0x00],
            [0x95, 0xFF, 0x81, 0x3B],
            [0x00, 0x81, 0x01]
        ];
        var localWaveMap = Enumerable.Range(0, 257)
            .Select(static index => (ushort)(index % 4))
            .ToArray();
        var bank = N64SoundToolsFxBank.Parse(
            BuildFxData(payloads, localWaveMap), pointerBank);

        var expected = new (int LocalWaveIndex, string Basis, int PrefixByteLength)[]
        {
            (127, N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis, 2),
            (128, N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis, 3),
            (255, N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis, 3),
            (256, N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis, 3),
            (59, N64SoundToolsFxInitialWaveResolver.LeadingOpcode95OneByteThen81Basis, 4)
        };

        for (var i = 0; i < expected.Length; i++)
        {
            var actual = Assert.IsType<N64SoundToolsFxInitialWaveBinding>(
                N64SoundToolsFxInitialWaveResolver.Resolve(
                    bank, pointerBank, bank.Components[i]));
            Assert.Equal(i, actual.ComponentIndex);
            Assert.Equal(expected[i].Basis, actual.Basis);
            Assert.Equal(expected[i].PrefixByteLength, actual.PrefixByteLength);
            Assert.Equal(expected[i].LocalWaveIndex, actual.LocalWaveIndex);
            Assert.Equal((ushort)(expected[i].LocalWaveIndex % 4), actual.PointerWaveIndex);
            Assert.Same(pointerBank.Waves[actual.PointerWaveIndex],
                actual.PointerWaveDescriptor);
            Assert.Equal(actual.PointerWaveIndex, actual.PointerWaveDescriptor.Index);
        }

        Assert.Null(N64SoundToolsFxInitialWaveResolver.Resolve(
            bank, pointerBank, bank.Components[^1]));
    }

    [Fact]
    public void InitialWaveResolver_UnrecognizedTruncatedAndOutOfRangeStayUnresolved()
    {
        var pointerBank = BuildPointerBank();
        byte[][] payloads =
        [
            [0x81],
            [0x81, 0x80],
            [0x95, 0xFF],
            [0x95, 0xFF, 0x81],
            [0x95, 0xFF, 0x81, 0x80],
            [0x95, 0xFF, 0x84, 0x81, 0x00],
            [0x84, 0x81, 0x00],
            [0x81, 0x02]
        ];
        var data = BuildFxData(payloads, [0, 1]);

        Assert.True(N64SoundToolsFxBank.TryParse(data, pointerBank, out var bank));
        Assert.NotNull(bank);
        Assert.All(bank.Components, component =>
            Assert.Null(N64SoundToolsFxInitialWaveResolver.Resolve(
                bank, pointerBank, component)));
    }

    [Fact]
    public void InitialEventResolver_DecodesRawFieldsAndClassifiesOnlyExactContinuations()
    {
        var pointerBank = BuildPointerBank();
        byte[][] payloads =
        [
            [0x81, 0x03,
                0x84, 10, 20, 30, 40, 50, 60, 70,
                0x9C, 0xFF, 0xA6, 0xFE, 0x30, 0x7F,
                0x80],
            [0x81, 0x01,
                0x84, 1, 2, 3, 4, 5, 6, 7,
                0x9C, 0x7E, 0xA6, 0x80, 0x2C, 0xFF, 0xFF,
                0x80, 0xE2],
            [0x95, 0xFF, 0x81, 0x02,
                0x84, 11, 22, 33, 44, 55, 66, 77,
                0x9C, 0x7F, 0xA6, 0x7F, 0x30, 0x01,
                0x96, 0x80],
            [0x81, 0x00,
                0x84, 0, 0, 0, 0, 0, 0, 0,
                0x9C, 0, 0xA6, 0, 0x60, 0]
        ];
        var bank = N64SoundToolsFxBank.Parse(
            BuildFxData(payloads, [0, 1, 2, 3]), pointerBank);

        var finite = Assert.IsType<N64SoundToolsFxInitialEvent>(
            N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[0]));
        Assert.Equal(0, finite.ComponentIndex);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.InterpreterProvenInitialEventBasis,
            finite.Basis);
        Assert.Equal(16, finite.EncodedByteLength);
        Assert.Equal(16, finite.EndOffset);
        Assert.Null(finite.LeadingLoopCountRaw);
        Assert.Equal(new N64SoundToolsFxEnvelope(10, 20, 30, 40, 50, 60, 70),
            finite.Envelope);
        Assert.Equal(0xFF, finite.PanOperandRaw);
        Assert.Equal(0x7F, finite.RuntimePan);
        Assert.Equal(0xFE, finite.VolumeOperandRaw);
        Assert.Equal(0x30, finite.NoteValueRaw);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.NoteKind, finite.NoteKind);
        Assert.Equal(0x7F, finite.LengthRaw);
        Assert.Equal(1, finite.LengthEncodingByteLength);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.FiniteLengthMode, finite.LengthMode);

        var indefinite = Assert.IsType<N64SoundToolsFxInitialEvent>(
            N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[1]));
        Assert.Equal(17, indefinite.EncodedByteLength);
        Assert.Equal(17, indefinite.EndOffset);
        Assert.Null(indefinite.LeadingLoopCountRaw);
        Assert.Equal(0x7E, indefinite.PanOperandRaw);
        Assert.Equal(0x3F, indefinite.RuntimePan);
        Assert.Equal(0x7FFF, indefinite.LengthRaw);
        Assert.Equal(2, indefinite.LengthEncodingByteLength);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.IndefiniteLengthMode,
            indefinite.LengthMode);

        var repeated = Assert.IsType<N64SoundToolsFxInitialEvent>(
            N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[2]));
        Assert.Equal(18, repeated.EncodedByteLength);
        Assert.Equal(18, repeated.EndOffset);
        Assert.Equal((byte)0xFF, repeated.LeadingLoopCountRaw);

        var rest = Assert.IsType<N64SoundToolsFxInitialEvent>(
            N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[3]));
        Assert.Equal(0x60, rest.NoteValueRaw);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.RestKind, rest.NoteKind);
        Assert.Equal(0, rest.LengthRaw);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.FiniteLengthMode, rest.LengthMode);

        var finiteContinuation = Assert.IsType<N64SoundToolsFxContinuation>(
            N64SoundToolsFxContinuationResolver.Resolve(
                bank, pointerBank, bank.Components[0]));
        Assert.Equal(N64SoundToolsFxContinuationResolver.StopAfterFiniteEventClassification,
            finiteContinuation.Classification);
        Assert.Equal(1, finiteContinuation.RecognizedByteLength);
        Assert.Null(finiteContinuation.UninterpretedAfterStopOffset);
        Assert.Null(finiteContinuation.UninterpretedAfterStopRaw);

        var indefiniteContinuation = Assert.IsType<N64SoundToolsFxContinuation>(
            N64SoundToolsFxContinuationResolver.Resolve(
                bank, pointerBank, bank.Components[1]));
        Assert.Equal(
            N64SoundToolsFxContinuationResolver.StopUnreachableWhileIndefiniteClassification,
            indefiniteContinuation.Classification);
        Assert.Equal(1, indefiniteContinuation.RecognizedByteLength);
        Assert.Equal(18, indefiniteContinuation.UninterpretedAfterStopOffset);
        Assert.Equal(new byte[] { 0xE2 }, indefiniteContinuation.UninterpretedAfterStopRaw);

        var infiniteContinuation = Assert.IsType<N64SoundToolsFxContinuation>(
            N64SoundToolsFxContinuationResolver.Resolve(
                bank, pointerBank, bank.Components[2]));
        Assert.Equal(N64SoundToolsFxContinuationResolver.InfiniteRepeatClassification,
            infiniteContinuation.Classification);
        Assert.Equal(2, infiniteContinuation.RecognizedByteLength);
        Assert.Null(infiniteContinuation.UninterpretedAfterStopOffset);
        Assert.Null(infiniteContinuation.UninterpretedAfterStopRaw);

        Assert.Null(N64SoundToolsFxContinuationResolver.Resolve(
            bank, pointerBank, bank.Components[3]));
    }

    [Fact]
    public void InitialEventResolver_TruncationWrongTokensAndNonExactSuffixesStayUnresolved()
    {
        var pointerBank = BuildPointerBank();
        byte[] canonical =
        [
            0x81, 0x00,
            0x84, 1, 2, 3, 4, 5, 6, 7,
            0x9C, 0x7F, 0xA6, 0x7F, 0x30, 0x01
        ];

        N64SoundToolsFxBank ParsePayload(byte[] payload)
        {
            byte[][] components = (payload.Length & 1) == 0
                ? [payload]
                : [payload, [0x00]];
            return N64SoundToolsFxBank.Parse(
                BuildFxData(components, [0, 1, 2, 3]), pointerBank);
        }

        for (var length = 1; length < canonical.Length; length++)
        {
            var bank = ParsePayload(canonical.AsSpan(0, length).ToArray());
            Assert.Null(N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[0]));
        }

        foreach (var offset in new[] { 2, 10, 12 })
        {
            var wrongToken = canonical.ToArray();
            wrongToken[offset] = 0x00;
            var bank = ParsePayload(wrongToken);
            Assert.Null(N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[0]));
        }

        var highBitNote = canonical.ToArray();
        highBitNote[14] = 0x80;
        var highBitNoteBank = ParsePayload(highBitNote);
        Assert.Null(N64SoundToolsFxInitialEventResolver.Resolve(
            highBitNoteBank, pointerBank, highBitNoteBank.Components[0]));

        var zeroNote = canonical.ToArray();
        zeroNote[14] = 0x00;
        var zeroNoteBank = ParsePayload(zeroNote);
        var zeroNoteEvent = Assert.IsType<N64SoundToolsFxInitialEvent>(
            N64SoundToolsFxInitialEventResolver.Resolve(
                zeroNoteBank, pointerBank, zeroNoteBank.Components[0]));
        Assert.Equal(0, zeroNoteEvent.NoteValueRaw);
        Assert.Equal(N64SoundToolsFxInitialEventResolver.NoteKind,
            zeroNoteEvent.NoteKind);

        var truncatedPackedLength = canonical.ToArray();
        truncatedPackedLength[^1] = 0x80;
        var truncatedLengthBank = ParsePayload(truncatedPackedLength);
        Assert.Null(N64SoundToolsFxInitialEventResolver.Resolve(
            truncatedLengthBank, pointerBank, truncatedLengthBank.Components[0]));

        var outOfRangeInitialWave = canonical.ToArray();
        outOfRangeInitialWave[1] = 0x04;
        var outOfRangeBank = ParsePayload(outOfRangeInitialWave);
        Assert.Null(N64SoundToolsFxInitialEventResolver.Resolve(
            outOfRangeBank, pointerBank, outOfRangeBank.Components[0]));
        Assert.Null(N64SoundToolsFxContinuationResolver.Resolve(
            outOfRangeBank, pointerBank, outOfRangeBank.Components[0]));

        byte[] laterGrammar = [0x81, 0x00, 0x00, .. canonical.AsSpan(2).ToArray()];
        var laterGrammarBank = ParsePayload(laterGrammar);
        Assert.Null(N64SoundToolsFxInitialEventResolver.Resolve(
            laterGrammarBank, pointerBank, laterGrammarBank.Components[0]));

        byte[][] nonExactSuffixes =
        [
            [.. canonical, 0xE2],
            [.. canonical, 0x80, 0x00],
            [.. canonical, 0x80, 0xE2, 0x00],
            [.. canonical, 0x96, 0x80],
            [0x95, 0x01, 0x81, 0x00, .. canonical.AsSpan(2).ToArray(), 0x96, 0x80],
            [0x95, 0xFF, 0x81, 0x00, .. canonical.AsSpan(2).ToArray(), 0x80]
        ];
        foreach (var payload in nonExactSuffixes)
        {
            var bank = ParsePayload(payload);
            Assert.NotNull(N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, bank.Components[0]));
            Assert.Null(N64SoundToolsFxContinuationResolver.Resolve(
                bank, pointerBank, bank.Components[0]));
        }
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
        Assert.Equal(3, N64SoundToolsFxBankJsonExporter.CurrentSchemaVersion);
        Assert.Equal(N64SoundToolsFxBankJsonExporter.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("magic").ValueKind);
        Assert.Equal("opaqueBeyondInitialEvent", root.GetProperty("bytecodeStatus").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sampleRate").ValueKind);
        Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("pitchApplicationStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("loopSchedulingStatus").GetString());
        Assert.Equal("notExecuted", root.GetProperty("playbackStatus").GetString());
        Assert.Equal("unresolved", root.GetProperty("initialWaveBindingStatus").GetString());
        Assert.Equal(0, root.GetProperty("resolvedInitialWaveBindingCount").GetInt32());
        Assert.Equal("unresolved", root.GetProperty("initialEventStatus").GetString());
        Assert.Equal(0, root.GetProperty("resolvedInitialEventCount").GetInt32());
        Assert.Equal("unresolved",
            root.GetProperty("continuationClassificationStatus").GetString());
        Assert.Equal(0, root.GetProperty("classifiedContinuationCount").GetInt32());
        Assert.Equal("callerSupplied", root.GetProperty("pointerBindingBasis").GetString());
        Assert.Equal(4, root.GetProperty("pointerWaveCount").GetInt32());
        Assert.Equal("AA", root.GetProperty("components")[0].GetProperty("opaqueDataRawHex").GetString());
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("components")[0].GetProperty("initialWaveBinding").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("components")[0].GetProperty("initialEvent").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("components")[0].GetProperty("continuation").ValueKind);
        Assert.Equal(3, root.GetProperty("localWaveMap")[0].GetProperty("pointerWaveIndex").GetInt32());
        Assert.Equal(1, root.GetProperty("localWaveMap")[1].GetProperty("pointerWaveIndex").GetInt32());
    }

    [Fact]
    public void Json_V3AddsPartialInitialEventAndContinuationWithoutChangingV2BindingOrRawBytes()
    {
        var pointerBank = BuildPointerBank();
        var bank = N64SoundToolsFxBank.Parse(
            BuildFxData(
            [
                [0x81, 0x03,
                    0x84, 10, 20, 30, 40, 50, 60, 70,
                    0x9C, 0xFF, 0xA6, 0xFE, 0x30, 0x7F,
                    0x80],
                [0x00, 0x81, 0x00]
            ], [0, 1, 2, 3]),
            pointerBank);

        using var json = JsonDocument.Parse(N64SoundToolsFxBankJsonExporter.Serialize(
            "effects.bfx", "bank.ptr.n64", N64SoundToolsFxInputResolver.CallerSuppliedBinding,
            bank, pointerBank));
        var root = json.RootElement;

        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("partial", root.GetProperty("initialWaveBindingStatus").GetString());
        Assert.Equal(1, root.GetProperty("resolvedInitialWaveBindingCount").GetInt32());
        Assert.Equal("partial", root.GetProperty("initialEventStatus").GetString());
        Assert.Equal(1, root.GetProperty("resolvedInitialEventCount").GetInt32());
        Assert.Equal("partial", root.GetProperty("continuationClassificationStatus").GetString());
        Assert.Equal(1, root.GetProperty("classifiedContinuationCount").GetInt32());
        Assert.Equal("opaqueBeyondInitialEvent", root.GetProperty("bytecodeStatus").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sampleRate").ValueKind);
        Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("pitchApplicationStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("loopSchedulingStatus").GetString());
        Assert.Equal("notExecuted", root.GetProperty("playbackStatus").GetString());

        var components = root.GetProperty("components");
        Assert.Equal("8103840A141E28323C469CFFA6FE307F80",
            components[0].GetProperty("opaqueDataRawHex").GetString());
        var binding = components[0].GetProperty("initialWaveBinding");
        Assert.Equal(N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis,
            binding.GetProperty("basis").GetString());
        Assert.Equal(2, binding.GetProperty("prefixByteLength").GetInt32());
        Assert.Equal(3, binding.GetProperty("localWaveIndex").GetInt32());
        Assert.Equal(3, binding.GetProperty("pointerWaveIndex").GetInt32());
        Assert.Equal(0x210, binding.GetProperty("pointerDescriptorOffset").GetInt32());

        var initialEvent = components[0].GetProperty("initialEvent");
        Assert.Equal(N64SoundToolsFxInitialEventResolver.InterpreterProvenInitialEventBasis,
            initialEvent.GetProperty("basis").GetString());
        Assert.Equal(16, initialEvent.GetProperty("encodedByteLength").GetInt32());
        Assert.Equal(16, initialEvent.GetProperty("endOffset").GetInt32());
        Assert.Equal(JsonValueKind.Null,
            initialEvent.GetProperty("leadingLoopCountRaw").ValueKind);
        var envelope = initialEvent.GetProperty("envelope");
        Assert.Equal(10, envelope.GetProperty("speedRaw").GetInt32());
        Assert.Equal(20, envelope.GetProperty("initialVolumeRaw").GetInt32());
        Assert.Equal(30, envelope.GetProperty("attackSpeedRaw").GetInt32());
        Assert.Equal(40, envelope.GetProperty("peakVolumeRaw").GetInt32());
        Assert.Equal(50, envelope.GetProperty("decaySpeedRaw").GetInt32());
        Assert.Equal(60, envelope.GetProperty("sustainVolumeRaw").GetInt32());
        Assert.Equal(70, envelope.GetProperty("releaseSpeedRaw").GetInt32());
        Assert.Equal(0xFF, initialEvent.GetProperty("panOperandRaw").GetInt32());
        Assert.Equal(0x7F, initialEvent.GetProperty("runtimePan").GetInt32());
        Assert.Equal(0xFE, initialEvent.GetProperty("volumeOperandRaw").GetInt32());
        Assert.Equal(0x30, initialEvent.GetProperty("noteValueRaw").GetInt32());
        Assert.Equal("note", initialEvent.GetProperty("noteKind").GetString());
        Assert.Equal(0x7F, initialEvent.GetProperty("lengthRaw").GetInt32());
        Assert.Equal(1, initialEvent.GetProperty("lengthEncodingByteLength").GetInt32());
        Assert.Equal("finite", initialEvent.GetProperty("lengthMode").GetString());

        var continuation = components[0].GetProperty("continuation");
        Assert.Equal("stopAfterFiniteEvent",
            continuation.GetProperty("classification").GetString());
        Assert.Equal(1, continuation.GetProperty("recognizedByteLength").GetInt32());
        Assert.Equal(JsonValueKind.Null,
            continuation.GetProperty("uninterpretedAfterStopOffset").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            continuation.GetProperty("uninterpretedAfterStopRawHex").ValueKind);

        Assert.Equal("008100", components[1].GetProperty("opaqueDataRawHex").GetString());
        Assert.Equal(JsonValueKind.Null,
            components[1].GetProperty("initialWaveBinding").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            components[1].GetProperty("initialEvent").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            components[1].GetProperty("continuation").ValueKind);
    }

    [Fact]
    public void Json_V3PreservesOnlyE2AsUninterpretedAfterIndefiniteUnreachableStop()
    {
        var pointerBank = BuildPointerBank();
        var bank = N64SoundToolsFxBank.Parse(
            BuildFxData(
            [
                [0x81, 0x01,
                    0x84, 1, 0x7F, 1, 0x7F, 1, 0x7F, 0x10,
                    0x9C, 0x7F, 0xA6, 0x7F, 0x30, 0xFF, 0xFF,
                    0x80, 0xE2],
                [0x00]
            ], [0, 1, 2, 3]),
            pointerBank);

        using var json = JsonDocument.Parse(N64SoundToolsFxBankJsonExporter.Serialize(
            "effects.bfx", "bank.ptr.n64", N64SoundToolsFxInputResolver.CallerSuppliedBinding,
            bank, pointerBank));
        var component = json.RootElement.GetProperty("components")[0];
        var initialEvent = component.GetProperty("initialEvent");
        Assert.Equal(17, initialEvent.GetProperty("endOffset").GetInt32());
        Assert.Equal(0x7FFF, initialEvent.GetProperty("lengthRaw").GetInt32());
        Assert.Equal("indefinite", initialEvent.GetProperty("lengthMode").GetString());

        var continuation = component.GetProperty("continuation");
        Assert.Equal("stopUnreachableWhileIndefinite",
            continuation.GetProperty("classification").GetString());
        Assert.Equal(1, continuation.GetProperty("recognizedByteLength").GetInt32());
        Assert.Equal(18,
            continuation.GetProperty("uninterpretedAfterStopOffset").GetInt32());
        Assert.Equal("E2",
            continuation.GetProperty("uninterpretedAfterStopRawHex").GetString());
    }

    [Fact]
    public void Json_V3ReportsExactFfWrapperAsInfiniteRepeatWithoutSchedulingPlayback()
    {
        var pointerBank = BuildPointerBank();
        var bank = N64SoundToolsFxBank.Parse(
            BuildFxData(
            [
                [0x95, 0xFF, 0x81, 0x02,
                    0x84, 1, 0x7F, 1, 0x7F, 1, 0x7F, 0x10,
                    0x9C, 0x7F, 0xA6, 0x7F, 0x2C, 0x01,
                    0x96, 0x80]
            ], [0, 1, 2, 3]),
            pointerBank);

        using var json = JsonDocument.Parse(N64SoundToolsFxBankJsonExporter.Serialize(
            "effects.bfx", "bank.ptr.n64", N64SoundToolsFxInputResolver.CallerSuppliedBinding,
            bank, pointerBank));
        var root = json.RootElement;
        Assert.Equal("resolved", root.GetProperty("initialEventStatus").GetString());
        Assert.Equal("classified",
            root.GetProperty("continuationClassificationStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("loopSchedulingStatus").GetString());
        Assert.Equal("notExecuted", root.GetProperty("playbackStatus").GetString());

        var component = root.GetProperty("components")[0];
        var initialEvent = component.GetProperty("initialEvent");
        Assert.Equal(0xFF, initialEvent.GetProperty("leadingLoopCountRaw").GetInt32());
        Assert.Equal(18, initialEvent.GetProperty("encodedByteLength").GetInt32());
        var continuation = component.GetProperty("continuation");
        Assert.Equal("infiniteRepeat",
            continuation.GetProperty("classification").GetString());
        Assert.Equal(2, continuation.GetProperty("recognizedByteLength").GetInt32());
        Assert.Equal(JsonValueKind.Null,
            continuation.GetProperty("uninterpretedAfterStopOffset").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            continuation.GetProperty("uninterpretedAfterStopRawHex").ValueKind);
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
    public void Command_OutputCanonicalAliasesCannotOverwriteStandaloneSources()
    {
        using var temp = new TempDirectory();
        var fxData = Convert.FromHexString(SyntheticHex);
        var pointerData = BuildPointerData();
        var fxPath = Path.Combine(temp.Path, "effects.bfx");
        var pointerPath = Path.Combine(temp.Path, "bank.ptr.n64");
        File.WriteAllBytes(fxPath, fxData);
        File.WriteAllBytes(pointerPath, pointerData);

        var fxAlias = Path.Combine(temp.Path, ".", Path.GetFileName(fxPath));
        Assert.Equal(1, N64AudioFxInspectCommand.Execute(fxPath, pointerPath, fxAlias));
        Assert.Equal(fxData, File.ReadAllBytes(fxPath));
        Assert.Equal(pointerData, File.ReadAllBytes(pointerPath));

        var pointerAlias = Path.Combine(temp.Path, ".", Path.GetFileName(pointerPath));
        Assert.Equal(1, N64AudioFxInspectCommand.Execute(fxPath, pointerPath, pointerAlias));
        Assert.Equal(fxData, File.ReadAllBytes(fxPath));
        Assert.Equal(pointerData, File.ReadAllBytes(pointerPath));
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

    [Fact]
    public void CarverPostPass_UniqueValidatedBinIsRenamedInPlace()
    {
        var noiseData = new byte[256];
        var fxData = Convert.FromHexString(SyntheticHex);
        var pointerData = BuildPointerData();
        var assets = new List<N64AssetCarver.CarvedAsset>
        {
            new("other/noise.bin", noiseData),
            new("misc/000.bin", fxData),
            new("misc/001.ptr.n64", pointerData)
        };

        N64AssetCarver.NameSoundToolsFxBank(assets);

        Assert.Equal(3, assets.Count);
        Assert.Equal("other/noise.bin", assets[0].Path);
        Assert.Equal("misc/000.bfx.n64", assets[1].Path);
        Assert.Equal("misc/001.ptr.n64", assets[2].Path);
        Assert.Same(noiseData, assets[0].Data);
        Assert.Same(fxData, assets[1].Data);
        Assert.Same(pointerData, assets[2].Data);
    }

    [Fact]
    public void CarverPostPass_MissingAmbiguousMalformedCollidingOrNonBinCasesStayUntyped()
    {
        var pointerData = BuildPointerData();
        var fxData = Convert.FromHexString(SyntheticHex);
        var noiseData = new byte[256];
        var malformedPointer = pointerData.ToArray();
        WriteU32(malformedPointer, 0x20, 0);
        var malformedFx = fxData.ToArray();
        WriteU16(malformedFx, 0x2A, 4);

        var cases = new (string Name, N64AssetCarver.CarvedAsset[] Assets)[]
        {
            ("no PTR", [new("misc/000.bin", fxData)]),
            ("no BFX", [
                new("misc/001.ptr.n64", pointerData),
                new("misc/000.bin", noiseData)
            ]),
            ("multiple PTRs", [
                new("misc/000.bin", fxData),
                new("misc/001.ptr.n64", pointerData),
                new("other/001.ptr.n64", pointerData.ToArray())
            ]),
            ("multiple BFX candidates", [
                new("misc/001.ptr.n64", pointerData),
                new("misc/000.bin", fxData),
                new("other/000.bin", fxData.ToArray())
            ]),
            ("malformed PTR", [
                new("misc/001.ptr.n64", malformedPointer),
                new("misc/000.bin", fxData)
            ]),
            ("malformed BFX", [
                new("misc/001.ptr.n64", pointerData),
                new("misc/000.bin", malformedFx)
            ]),
            ("target collision", [
                new("misc/001.ptr.n64", pointerData),
                new("misc/000.bin", fxData),
                new("MISC/000.BFX.N64", noiseData)
            ]),
            ("candidate is not .bin", [
                new("misc/001.ptr.n64", pointerData),
                new("misc/000.raw", fxData)
            ])
        };

        foreach (var (name, seed) in cases)
        {
            var assets = seed.ToList();
            var before = assets.ToArray();

            N64AssetCarver.NameSoundToolsFxBank(assets);

            Assert.Equal(before.Length, assets.Count);
            for (var i = 0; i < before.Length; i++)
            {
                Assert.True(string.Equals(before[i].Path, assets[i].Path,
                    StringComparison.Ordinal), $"{name} renamed asset {i}");
                Assert.True(ReferenceEquals(before[i].Data, assets[i].Data),
                    $"{name} replaced asset {i} data");
            }
        }
    }

    [CorpusFact]
    public void RomCorpus_StructuralSingletonsExactConsumptionCensusAndRouteParity()
    {
        // Audited provenance ledger only; the external source kit is not a live
        // test dependency. Raw cartridge slices below are the executable gate.
        var sourceOracle = new (string Source, long ByteLength, string Sha256)[]
        {
            ("https://ultra64.ca/files/software/nintendo/" +
             "Nintendo_64_Sound_Tools_WIN95_v3.14/" +
             "Nintendo_64_Sound_Tools_WIN95_v3.14.iso", 12_310_528,
                SoundTools314IsoSha256),
            ("ISO:SGI/ST314.TGZ", 4_108_086, SoundTools314ArchiveSha256),
            ("ST314.TGZ:usr/src/PR/libsrc/libsoundtoolssc/player.c",
                40_887, SoundTools314PlayerSha256),
            ("ST314.TGZ:usr/src/PR/libsrc/libsoundtoolssc/player_commands.c",
                17_078, SoundTools314PlayerCommandsSha256),
            ("ST314.TGZ:usr/src/PR/libsrc/libsoundtoolssc/libmus_data.h",
                4_286, SoundTools314DataHeaderSha256)
        };
        Assert.All(sourceOracle, static source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Source));
            Assert.True(source.ByteLength > 0);
            Assert.Matches("^[0-9A-F]{64}$", source.Sha256);
        });
        Assert.Equal(sourceOracle.Length,
            sourceOracle.Select(static source => source.Sha256).Distinct().Count());

        CorpusExpected[] expectations =
        [
            new("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
                "Tony Hawk's Pro Skater (USA).z64", 2_176,
                0x80016990u, 0x9DFE0, 0x800B4970u,
                "F18F25795709791B62CD3282798991F7AD967AADE0CCD4CA316179212732A153",
                "audiobanks/014.bfx.n64", "audiobanks/015.ptr.n64", 143,
                0x12A2, 178, 178, 108, 0x5A8, 0x11CA, "17:98,18:80",
                "00:13,4B:1,7F:164", 0, 107,
                178, 0, 108, 0, 70, 0, 107,
                "C7528AC45A3FD17E17374F1A2F04FDB71292DEAEC86211A849A35D3E85B0E713",
                "BDBE49080074E330000A88462CB94E83DAE53D8B33FC315BBAF806D158BA4AB4",
                "DB9E946DAA42C610C2F89FC6D174CB9DA4233B6160CEE66A2EAF103BA93D500D",
                "695AEE2CAFC61F870DD9B41591EA148560D0ECA36E84F2E7ADDD6DA26C2D3BD7",
                5_196, "781AB641EBDB5E600B29254B4933D50DD80829317EE2317CF6EEE9F4CDF1C609",
                new InterpreterExpected(
                    0xDCA24, 0x9DFC0,
                    0x9E040, "1569624858F1D246EE5799D5F4F12E3063D44D815953B40BA45450B829CEEB0C",
                    0x9E4AC, "A3862163CC65748A4BEC317021482D04B313D13033B944096D97E600542CB70A",
                    0x9E554, 0x9E654, 0x9E83C,
                    0xA00E0, 0x800B6A70u,
                    "471871FA60FABA0427C6E2B213BEB2F0E87C3A5B793992CD4C9DE55E39A40E5D",
                    0xA012C, "FCCE79690FEFFBDC4B649FEAAED2575F37AA24C00EC9521C53B3D2295458FA0C",
                    0xA025C,
                    0x9FEA8, "ECA7F06E382F9DBFB67F945D2B0980907A628EE77C5135B43675545E4009A0A5",
                    0xA1124, "5BAE2B85F38810CC9FA5F133D54376F77DCF916E452D4FA4CA4DA67399A1E212",
                    0xA144C, "CD00949474473E09999A819187381062BBD9C29FE9A6AFC7BD8A8C140C725DDF")),
            new("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
                "Tony Hawk's Pro Skater 2 (USA).z64", 3_962,
                0x80016B20u, 0xBBFE0, 0x800D2B00u,
                "C3FB620AA6FA679671EEE7A340DBD7E96ECAF3C3559B57A120561850D4476787",
                "misc/000.bfx.n64", "misc/001.ptr.n64", 379,
                0x2378, 322, 322, 322, 0xA28, 0x20F4, "17:60,18:162,19:100",
                "7F:322", 0, 321,
                322, 0, 322, 322, 0, 0, 321,
                "717A46C5BC4A11CC6412CAF08308EC4AE6A5681CB32AE8B8D063B2ADFD0003AB",
                "F6E38CD4E7356330372993EA1DF97B519AFF57AD082B36A462269862706ACA52",
                "C1DA19D0D91A6AADAF45BE0980902E4D08765261B5D2E4166A9EAA74F2E81BDD",
                "FE5734E20EDCE3C0A30FE3E154C7B5E24CC94C2A9517708E8D82573DC3630E7D",
                9_372, "8E655AECF74968EA7827A2688AC18D8ECE73C213066A4705CA757440EA09018F",
                new InterpreterExpected(
                    0xE79C0, 0xBBFC0,
                    0xBC040, "3240A19B75D556C269877822F75E492ADE7424319A039EE0883E889D39F800D4",
                    0xBC4AC, "80BF8BB62BB8E21A9D4BF97EDAF5A200FD2123D26C58266F24372FD949E4ADA0",
                    0xBC554, 0xBC654, 0xBC83C,
                    0xBE1C4, 0x800D4CE4u,
                    "0A51589B83F90C146B544D57D49CC26475BB1DBF0278992C37978E4FB04FFAB8",
                    0xBE210, "E4ADC93D7B4C6C0D0F4EF82CD6F8B537CEBCFA1F55838B8AF3A708562AB88A12",
                    0xBE340,
                    0xBDF8C, "36913802894EB04CBC72F7231F2DA42FEDD08E4696D64749469070EABD9E11D5",
                    0xBF208, "B042D96FA5A04BF1F1A994A1114E963AC882AF6C32F313BB6FAE456349B11FAC",
                    0xBF530, "9D9D4C70545857AEB1BCA4C01CA984534FCBC11F4F3285FAC6CFED1D37C0F3B9")),
            new("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
                "Tony Hawk's Pro Skater 3 (USA).z64", 3_313,
                0x80016B20u, 0xC0CF0, 0x800D7810u,
                "E0CF7950E722EEB59555EF01D027771502C739ECB6CF47ED09522128B477249B",
                "misc/000.bfx.n64", "misc/001.ptr.n64", 257,
                0x142A, 186, 186, 186, 0x5E8, 0x12B6, "17:95,18:66,19:25",
                "48:1,50:1,66:1,67:1,69:1,6D:1,72:1,7F:179", 71, 256,
                186, 0, 186, 186, 0, 0, 185,
                "38892972AE1C8E6BE89EF0391963A81F387D2035EEFEE545D44C419EC6BD72BE",
                "4E7D0E1EDA5ACE7DFDBA88A28B3B0EE06BFF16D42CD77FD6AFF24EB71676E2D1",
                "728750C62D3438060FAAABEC2CB80897D08AA48096C624C6709588402EDCE191",
                "6366A56CBB05CDA813F5E92B7552364F84629649ABDED37804F3EBD561636CAE",
                5_428, "30118148B77EA61319DF48D78A264831800F51F6127A93467CBEE0CDDB0581A3",
                new InterpreterExpected(
                    0xEB7E0, 0xC0CD0,
                    0xC0D50, "CFBAEFE7428BDB59889D36150BB966D74AC464EA0E7E8A0FD7822F3DAADBA4CC",
                    0xC11BC, "4839C382BA11DE9233E1914F027B5D1759DDFA9760C9C4C9A76FDCC29FF21EF9",
                    0xC1264, 0xC1364, 0xC154C,
                    0xC2ED4, 0x800D99F4u,
                    "A5D6FEE97C513595595AF9FD895A95DD1D380EB8FDD55CB59704A551720F9DC2",
                    0xC2F20, "72A0F8D714FCDD598257B38CA3C6DAB7848538E0DA28BB98091FEC8A8C2EFE5F",
                    0xC3050,
                    0xC2C9C, "E28D2B1128F071425A4EAC4BA964D5631390B527A54959F5283B4541BA12E54A",
                    0xC3F18, "BDEF4D5B6982A62EA7B8EF41C91FA52553E703401B6F8527B30EDC5BAA3521AD",
                    0xC4240, "0EDAA4F9FC97D0390BD6FD91E8DE2F525149AB7D03AEF05E82CAC6D45F316549")),
            new("Spider-Man (2000-11-21, N64 - Final)",
                "Spider-Man (USA).z64", 4_286,
                0x80016AE0u, 0xC4690, 0x800DB170u,
                "1D3ED3384F45ADA2EBF6CB0666DDC7FEC4C4FFDB6FDB993B8D566AA3CD4F3867",
                "audiobanks/000.bfx.n64", "audiobanks/001.ptr.n64", 996,
                0x6ECE, 994, 994, 992, 0x1F28, 0x670E,
                "17:70,18:343,19:579,20:1,21:1",
                "64:1,6E:1,78:5,7A:1,7D:2,7F:984", 0, 995,
                993, 1, 992, 70, 2, 0, 991,
                "911ED097EC9349CBE78109D60B5EC175BE69F37BDE6FA909C012C567B9C8E5C1",
                "49F1F8790C423F56322AD13609A09D52789A5342557333E4C3419021268526DC",
                "58B85CAC59E117F241561A99144054F4F8339DC97D80C52ABB07A62CAFD9E359",
                "FFE94660EBA49F25F2C568592FCA105FD8578CFA14D816214F009F7A787E2008",
                28_861, "79E21B8D0993A5A5793A6B892F714ACCEB6938D942AE03FF4E19D909B3590D23",
                new InterpreterExpected(
                    0xF2AA4, 0xC4670,
                    0xC46F0, "9E5A9BC3594AADFE2E4CBC23FD906E875C557BF9A07D647D299B2AC667804889",
                    0xC4B5C, "CC16E69410DDC6101EF4EE86A835E3DC658B266484C5D4F4CDE2A7341C9D13EE",
                    0xC4C04, 0xC4D04, 0xC4EEC,
                    0xC6898, 0x800DD378u,
                    "CC1D741703E51776A8DC0C194FDA5634D683822E9B3C5F660CCA5A5FB6560E71",
                    0xC68E4, "0AD44F840D4FB61516ED9CEEA7512DA53FF43C8E5B61C9BE4C2D71C572E0A3B9",
                    0xC6A14,
                    0xC6660, "988F7457BF38814671ECE437BCD2CE7603A60741FDA82CEA42E8E423461849E0",
                    0xC78DC, "DFF6D397039A1DE5368FF5A13891EB4388C76FFF55B15A1CFDE4CF7CFD36C7FA",
                    0xC7C04, "6A536B911BCFA32C223293AB11591D2AEEE53602E54DCFA9541F97AA6F567651"))
        ];

        var totalAssets = 0;
        var totalComponents = 0;
        var totalEffects = 0;
        var totalLocalWaves = 0;
        var totalOpaqueBytes = 0;
        var totalNotFourAligned = 0;
        var totalOdd = 0;
        var totalResolvedInitialWaveBindings = 0;
        var totalDirectInitialWavePrefixes = 0;
        var totalWrappedInitialWavePrefixes = 0;
        var totalUniqueInitialWaves = 0;
        var totalExtraInitialWaveReferences = 0;
        var totalResolvedInitialEvents = 0;
        var totalClassifiedContinuations = 0;
        var initialEventsByLengthMode = new List<string>();
        var continuationClassifications = new List<string>();
        var initialEventNotes = new List<byte>();
        var initialEventVolumes = new List<byte>();
        var initialEventLengths = new List<int>();
        var initialEventLengthEncodingByteLengths = new List<int>();
        var initialEventEnvelopeSignatures = new List<string>();
        var normalizedInitialEventCorpus = new List<byte>();
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

            var bootAsset = Assert.Single(assets, static asset =>
                asset.Path == "boot.bin");
            Assert.Equal(expected.BootSha256, Hash(bootAsset.Data));
            Assert.Equal(expected.Opcode81HandlerAddress,
                expected.BootRamBase + (uint)expected.Opcode81HandlerOffset);
            var opcode81Handler = bootAsset.Data.AsSpan(
                expected.Opcode81HandlerOffset, Opcode81HandlerLength).ToArray();
            // The shared handler reads one byte, conditionally reads a second,
            // combines 7 high bits with 8 low bits, and stores local wave at +0xAE.
            Assert.Equal(Opcode81HandlerHex, Convert.ToHexString(opcode81Handler));
            Assert.Equal(Opcode81HandlerSha256, Hash(opcode81Handler));

            var interpreter = expected.Interpreter;
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0x80,
                expected.BootRamBase + (uint)interpreter.Opcode80HandlerOffset);
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0x81,
                expected.Opcode81HandlerAddress);
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0x84,
                expected.BootRamBase + (uint)interpreter.Opcode84HandlerOffset);
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0x95,
                expected.BootRamBase + (uint)interpreter.Opcode95HandlerOffset);
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0x96,
                expected.BootRamBase + (uint)interpreter.Opcode96HandlerOffset);
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0x9C,
                expected.BootRamBase + (uint)interpreter.Opcode9CHandlerOffset);
            AssertDispatchEntry(bootAsset.Data, interpreter.DispatchTableOffset, 0xA6,
                expected.BootRamBase + (uint)interpreter.OpcodeA6HandlerOffset);

            var opcode80Handler = AssertPinnedSlice(
                bootAsset.Data, interpreter.Opcode80HandlerOffset,
                Opcode80HandlerLength, Opcode80HandlerSha256, Opcode80HandlerHex);
            // Fstop returns NULL and clears continuous streams, song/BFX ownership,
            // the handle, and the pending wave. Later bytes cannot be reached.
            Assert.Equal(0x00001021u, ReadWord(opcode80Handler, 0x00));
            Assert.Equal(0xAC800074u, ReadWord(opcode80Handler, 0x0C));
            Assert.Equal(0xAC800078u, ReadWord(opcode80Handler, 0x10));
            Assert.Equal(0x03E00008u, ReadWord(opcode80Handler, 0x18));

            var opcode84Handler = AssertPinnedSlice(
                bootAsset.Data, interpreter.Opcode84HandlerOffset,
                Opcode84HandlerLength, interpreter.Opcode84HandlerSha256);
            // Fdefa consumes seven u8 operands in order and stores their raw
            // speed/volume values in the channel before computing envelope deltas.
            Assert.Equal(0x90A60000u, ReadWord(opcode84Handler, 0x00));
            Assert.Equal(0xA08600BFu, ReadWord(opcode84Handler, 0x48));
            Assert.Equal(0x90A20000u, ReadWord(opcode84Handler, 0x50));
            Assert.Equal(0xA08200C0u, ReadWord(opcode84Handler, 0x58));
            Assert.Equal(0x90A60000u, ReadWord(opcode84Handler, 0x5C));
            Assert.Equal(0xA08600C6u, ReadWord(opcode84Handler, 0x68));
            Assert.Equal(0x90A20000u, ReadWord(opcode84Handler, 0x6C));
            Assert.Equal(0xA08200C1u, ReadWord(opcode84Handler, 0x70));
            Assert.Equal(0x90A60000u, ReadWord(opcode84Handler, 0xB0));
            Assert.Equal(0xA08600C7u, ReadWord(opcode84Handler, 0xB8));
            Assert.Equal(0x90A20000u, ReadWord(opcode84Handler, 0xBC));
            Assert.Equal(0xA08200C2u, ReadWord(opcode84Handler, 0xC0));
            Assert.Equal(0x90A60000u, ReadWord(opcode84Handler, 0xFC));
            Assert.Equal(0xA08600C8u, ReadWord(opcode84Handler, 0x11C));

            var opcode95Handler = AssertPinnedSlice(
                bootAsset.Data, interpreter.Opcode95HandlerOffset,
                Opcode95HandlerLength, interpreter.Opcode95HandlerSha256);
            // Ffor consumes exactly one count operand and records the following
            // bytecode address as the loop head.
            Assert.Equal(0x00803021u, ReadWord(opcode95Handler, 0x00));
            Assert.Equal(0x90A20000u, ReadWord(opcode95Handler, 0x04));
            Assert.Equal(0x24A50001u, ReadWord(opcode95Handler, 0x08));
            Assert.Equal(0x90C700DBu, ReadWord(opcode95Handler, 0x0C));
            Assert.Equal(0xA0820120u, ReadWord(opcode95Handler, 0x1C));
            Assert.Equal(0xAC4500E0u, ReadWord(opcode95Handler, 0x28));

            var opcode96Handler = AssertPinnedSlice(
                bootAsset.Data, interpreter.Opcode96HandlerOffset,
                Opcode96HandlerLength, Opcode96HandlerSha256, Opcode96HandlerHex);
            // Fnext compares the saved u8 count with 0xFF. The branch bypasses
            // the decrement store for 0xFF and restores the saved loop pointer.
            Assert.Equal(0x90E30120u, ReadWord(opcode96Handler, 0x0C));
            Assert.Equal(0x240200FFu, ReadWord(opcode96Handler, 0x10));
            Assert.Equal(0x10620007u, ReadWord(opcode96Handler, 0x14));
            Assert.Equal(0xA0E20120u, ReadWord(opcode96Handler, 0x1C));
            Assert.Equal(0x14400003u, ReadWord(opcode96Handler, 0x24));
            Assert.Equal(0x04C00017u, ReadWord(opcode96Handler, 0x34));
            Assert.Equal(0x8C4500E0u, ReadWord(opcode96Handler, 0x44));

            var opcode9CHandler = AssertPinnedSlice(
                bootAsset.Data, interpreter.Opcode9CHandlerOffset,
                Opcode9CHandlerLength, Opcode9CHandlerSha256, Opcode9CHandlerHex);
            // Fpan consumes one u8 operand, divides it by two with an integer
            // shift, and stores the runtime pan.
            Assert.Equal(0x90A20000u, ReadWord(opcode9CHandler, 0x00));
            Assert.Equal(0x00021042u, ReadWord(opcode9CHandler, 0x04));
            Assert.Equal(0xA08200BDu, ReadWord(opcode9CHandler, 0x08));

            var opcodeA6Handler = AssertPinnedSlice(
                bootAsset.Data, interpreter.OpcodeA6HandlerOffset,
                OpcodeA6HandlerLength, OpcodeA6HandlerSha256, OpcodeA6HandlerHex);
            // Fvolume consumes one u8 operand and stores it without conversion.
            Assert.Equal(0x90A20000u, ReadWord(opcodeA6Handler, 0x00));
            Assert.Equal(0xA08200BCu, ReadWord(opcodeA6Handler, 0x04));

            var bfxProcessLoop = AssertPinnedSlice(
                bootAsset.Data, interpreter.BfxProcessLoopSliceOffset,
                BfxProcessLoopSliceLength, interpreter.BfxProcessLoopSliceSha256);
            Assert.Equal(interpreter.BfxProcessLoopRamAddress,
                expected.BootRamBase + (uint)interpreter.BfxProcessLoopSliceOffset);
            // The BFX note path reads cp->pdata, dispatches every high-bit token
            // through the same opcode&0x7F table, accepts each handler's returned
            // pointer, and leaves the loop when opcode 0x80 returns NULL.
            Assert.Equal(0x8E250004u, ReadWord(bfxProcessLoop, 0x00));
            Assert.Equal(0x10A0000Fu, ReadWord(bfxProcessLoop, 0x04));
            Assert.Equal(expected.BootRamBase + (uint)interpreter.DispatchTableOffset,
                DecodeLuiAddiuAddress(
                    ReadWord(bfxProcessLoop, 0x08),
                    ReadWord(bfxProcessLoop, 0x0C)));
            Assert.Equal(0x80A20000u, ReadWord(bfxProcessLoop, 0x10));
            Assert.Equal(0x0441000Bu, ReadWord(bfxProcessLoop, 0x14));
            Assert.Equal(0x30C2007Fu, ReadWord(bfxProcessLoop, 0x20));
            Assert.Equal(0x00021080u, ReadWord(bfxProcessLoop, 0x24));
            Assert.Equal(0x8C420000u, ReadWord(bfxProcessLoop, 0x2C));
            Assert.Equal(0x0040F809u, ReadWord(bfxProcessLoop, 0x30));
            Assert.Equal(0x24A50001u, ReadWord(bfxProcessLoop, 0x34));
            Assert.Equal(0x00402821u, ReadWord(bfxProcessLoop, 0x38));
            Assert.Equal(0x14A0FFF4u, ReadWord(bfxProcessLoop, 0x3C));
            Assert.Equal(0x10A000D7u, ReadWord(bfxProcessLoop, 0x44));
            Assert.Equal(0xAE250004u, ReadWord(bfxProcessLoop, 0x48));
            Assert.Equal(interpreter.InitialNoteLengthSliceOffset,
                interpreter.BfxProcessLoopSliceOffset + BfxProcessLoopSliceLength);

            var initialNoteLength = AssertPinnedSlice(
                bootAsset.Data, interpreter.InitialNoteLengthSliceOffset,
                InitialNoteLengthSliceLength, interpreter.InitialNoteLengthSliceSha256);
            // Fresh FX channels have velocity and fixed-length modes disabled.
            // The note parser therefore consumes one note byte below 0x80, then a
            // one- or two-byte packed 15-bit length and adds length<<8 to the clock.
            Assert.Equal(0x922200D2u, ReadWord(initialNoteLength, 0x04));
            Assert.Equal(0x90720000u, ReadWord(initialNoteLength, 0x10));
            Assert.Equal(0x962300ACu, ReadWord(initialNoteLength, 0x58));
            Assert.Equal(0x8E240004u, ReadWord(initialNoteLength, 0x94));
            Assert.Equal(0x90860000u, ReadWord(initialNoteLength, 0x98));
            Assert.Equal(0x00061600u, ReadWord(initialNoteLength, 0xA0));
            Assert.Equal(0x04400004u, ReadWord(initialNoteLength, 0xA4));
            Assert.Equal(0x30C200FFu, ReadWord(initialNoteLength, 0xAC));
            Assert.Equal(0xA622009Au, ReadWord(initialNoteLength, 0xB4));
            Assert.Equal(0x90830000u, ReadWord(initialNoteLength, 0xB8));
            Assert.Equal(0x30C2007Fu, ReadWord(initialNoteLength, 0xC4));
            Assert.Equal(0x00021200u, ReadWord(initialNoteLength, 0xC8));
            Assert.Equal(0x00621821u, ReadWord(initialNoteLength, 0xCC));
            Assert.Equal(0xA623009Au, ReadWord(initialNoteLength, 0xD0));
            Assert.Equal(0x9623009Au, ReadWord(initialNoteLength, 0xD8));
            Assert.Equal(0x00031A00u, ReadWord(initialNoteLength, 0xEC));
            Assert.Equal(0x00431021u, ReadWord(initialNoteLength, 0xF4));
            Assert.Equal(0xAE22003Cu, ReadWord(initialNoteLength, 0xF8));

            Assert.Equal(interpreter.InitialNoteLengthSliceOffset + 0x130,
                interpreter.RestSentinelSliceOffset);
            var restSentinel = AssertPinnedSlice(
                bootAsset.Data, interpreter.RestSentinelSliceOffset,
                RestSentinelSliceLength, RestSentinelSliceSha256, RestSentinelSliceHex);
            // The note path classifies raw 0x60 as a rest sentinel.
            Assert.Equal(0x24020060u, ReadWord(restSentinel, 0x00));
            Assert.Equal(0x1242007Cu, ReadWord(restSentinel, 0x04));

            var indefiniteLengthGate = AssertPinnedSlice(
                bootAsset.Data, interpreter.IndefiniteLengthGateSliceOffset,
                IndefiniteLengthGateSliceLength, interpreter.IndefiniteLengthGateSliceSha256);
            // The channel scheduler advances its clock, but length 0x7FFF branches
            // around note expiry and the GetNewNote call. It is an indefinite
            // sentinel, not a duration.
            Assert.Equal(0x9603FFF2u, ReadWord(indefiniteLengthGate, 0x00));
            Assert.Equal(0x9603FFF0u, ReadWord(indefiniteLengthGate, 0x0C));
            Assert.Equal(0x24027FFFu, ReadWord(indefiniteLengthGate, 0x10));
            Assert.Equal(0x10620012u, ReadWord(indefiniteLengthGate, 0x14));
            Assert.Equal(0x0C000000u,
                ReadWord(indefiniteLengthGate, 0x38) & 0xFC000000u);
            var getNewNoteAddress = expected.BootRamBase +
                (uint)interpreter.InitialNoteLengthSliceOffset - 0x70u;
            Assert.Equal(EncodeJal(getNewNoteAddress),
                ReadWord(indefiniteLengthGate, 0x38));

            var channelInitializer = AssertPinnedSlice(
                bootAsset.Data, interpreter.ChannelInitializerOffset,
                ChannelInitializerLength, interpreter.ChannelInitializerSha256);
            // The initializer preserves only the playing flag, zeroes every byte
            // of the 0x130-byte channel, and establishes the FX parser defaults.
            Assert.Equal(0x90E800C9u, ReadWord(channelInitializer, 0x04));
            Assert.Equal(0xACE00004u, ReadWord(channelInitializer, 0x0C));
            Assert.Equal(0xA0800000u, ReadWord(channelInitializer, 0x10));
            Assert.Equal(0x2C620130u, ReadWord(channelInitializer, 0x18));
            Assert.Equal(0x1440FFFCu, ReadWord(channelInitializer, 0x1C));
            Assert.Equal(0xA0E300D3u, ReadWord(channelInitializer, 0x98));
            Assert.Equal(0xA0E300BCu, ReadWord(channelInitializer, 0x9C));
            Assert.Equal(0xA0E400BFu, ReadWord(channelInitializer, 0xA0));
            Assert.Equal(0xA0E200BDu, ReadWord(channelInitializer, 0xC4));
            Assert.Equal(0xA4E4009Au, ReadWord(channelInitializer, 0xB4));
            Assert.Equal(0xA0E800C9u, ReadWord(channelInitializer, 0x128));

            var effectStartPrefix = AssertPinnedSlice(
                bootAsset.Data, interpreter.EffectStartPrefixOffset,
                EffectStartPrefixLength, interpreter.EffectStartPrefixSha256);
            // Every effect start calls that initializer before setting fx_addr and
            // installing header->effects[number].fxdata as pbase/pdata.
            var channelInitializerAddress = expected.BootRamBase +
                (uint)interpreter.ChannelInitializerOffset;
            Assert.Equal(EncodeJal(channelInitializerAddress),
                ReadWord(effectStartPrefix, 0x34));
            Assert.Equal(0xAE740078u, ReadWord(effectStartPrefix, 0x48));
            Assert.Equal(0x8C420018u, ReadWord(effectStartPrefix, 0x78));
            Assert.Equal(0xAE620080u, ReadWord(effectStartPrefix, 0x7C));
            Assert.Equal(0xAE620004u, ReadWord(effectStartPrefix, 0x80));

            if (expected.RomName.StartsWith("Tony Hawk's Pro Skater 2 ",
                    StringComparison.Ordinal))
            {
                Assert.Equal(0x800D3838u,
                    expected.BootRamBase + (uint)Thps2DispatchTableBaseConstructionOffset);
                Assert.Equal(Thps2DispatchTableBaseConstructionHex,
                    Convert.ToHexString(bootAsset.Data.AsSpan(
                        Thps2DispatchTableBaseConstructionOffset, 8)));

                Assert.Equal(0x800D3868u,
                    expected.BootRamBase + (uint)Thps2DispatcherOffset);
                var dispatcher = bootAsset.Data.AsSpan(
                    Thps2DispatcherOffset, 68).ToArray();
                Assert.Equal(Thps2DispatcherHex, Convert.ToHexString(dispatcher));
                Assert.Equal(Thps2DispatcherSha256, Hash(dispatcher));

                // The pinned base construction yields 0x800FE4E0. The dispatcher
                // masks an opcode by 0x7F and indexes this table by four bytes.
                Assert.Equal(0x800FE4E0u,
                    expected.BootRamBase + (uint)Thps2DispatchTableOffset);
                var firstDispatchEntries = bootAsset.Data.AsSpan(
                    Thps2DispatchTableOffset, 8).ToArray();
                Assert.Equal(Thps2FirstDispatchEntriesHex,
                    Convert.ToHexString(firstDispatchEntries));
                Assert.Equal(1, 0x81 & 0x7F);
                Assert.Equal(expected.Opcode81HandlerAddress,
                    BinaryPrimitives.ReadUInt32BigEndian(
                        firstDispatchEntries.AsSpan((0x81 & 0x7F) * 4)));

                Assert.Equal(0x800D4F00u,
                    expected.BootRamBase + (uint)Thps2LocalToPointerJoinOffset);
                var localToPointerJoin = bootAsset.Data.AsSpan(
                    Thps2LocalToPointerJoinOffset,
                    Thps2LocalToPointerJoinLength).ToArray();
                // THPS2 loads the local-wave field at state+0xAE written by the
                // opcode-0x81 handler, then uses it to index BFX+0x14's u16 map
                // and PTR+0x2C's exact descriptor-pointer table.
                Assert.Equal(Thps2LocalToPointerJoinHex,
                    Convert.ToHexString(localToPointerJoin));
                Assert.Equal(Thps2LocalToPointerJoinSha256, Hash(localToPointerJoin));
            }

            if (expected.RomName.StartsWith("Spider-Man", StringComparison.Ordinal))
            {
                Assert.Equal(0x15, 0x95 & 0x7F);
                Assert.Equal(0x800DB63Cu,
                    BinaryPrimitives.ReadUInt32BigEndian(bootAsset.Data.AsSpan(
                        SpiderOpcode95TableEntryOffset, 4)));
                Assert.Equal(0x800DB63Cu,
                    expected.BootRamBase + (uint)SpiderOpcode95HandlerOffset);
                var opcode95HandlerHead = bootAsset.Data.AsSpan(
                    SpiderOpcode95HandlerOffset, 12).ToArray();
                // The opcode-0x95 handler moves the state, consumes exactly one
                // operand byte, and advances to the following opcode (0x81 here).
                Assert.Equal(SpiderOpcode95HandlerHeadHex,
                    Convert.ToHexString(opcode95HandlerHead));
                Assert.Equal(SpiderOpcode95HandlerHeadSha256, Hash(opcode95HandlerHead));
            }

            var pointerAsset = Assert.Single(assets, static asset =>
                N64SoundToolsBank.HasPointerMagic(asset.Data));
            var pointerBank = N64SoundToolsBank.ParsePointer(pointerAsset.Data);
            var candidates = assets.Where(asset =>
                N64SoundToolsFxBank.TryParse(asset.Data, pointerBank, out _)).ToArray();
            var candidate = Assert.Single(candidates);
            Assert.Single(assets, static asset =>
                asset.Path.EndsWith(".bfx.n64", StringComparison.Ordinal));
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

            var initialWaveBindings = bank.Components
                .Select(component => Assert.IsType<N64SoundToolsFxInitialWaveBinding>(
                    N64SoundToolsFxInitialWaveResolver.Resolve(bank, pointerBank, component)))
                .ToArray();
            var uniqueInitialWaves = initialWaveBindings
                .Select(static binding => binding.LocalWaveIndex)
                .Distinct()
                .Order()
                .ToArray();
            Assert.Equal(expected.DirectPrefixComponents, initialWaveBindings.Count(static binding =>
                binding.Basis == N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis));
            Assert.Equal(expected.WrappedPrefixComponents, initialWaveBindings.Count(static binding =>
                binding.Basis ==
                    N64SoundToolsFxInitialWaveResolver.LeadingOpcode95OneByteThen81Basis));
            Assert.Equal(expected.UniqueInitialWaves, uniqueInitialWaves.Length);
            Assert.Equal(expected.IdentityInitialWaveBindings, initialWaveBindings.Count(static binding =>
                binding.ComponentIndex == binding.LocalWaveIndex));
            Assert.Equal(expected.ExtraInitialWaveReferences,
                initialWaveBindings.Length - uniqueInitialWaves.Length);
            Assert.Equal(expected.InitialLocalMinimum, uniqueInitialWaves.Min());
            Assert.Equal(expected.InitialLocalMaximum, uniqueInitialWaves.Max());
            Assert.Equal(Enumerable.Range(0, bank.LocalWaveCount), uniqueInitialWaves);
            Assert.All(initialWaveBindings, binding =>
            {
                Assert.Equal(binding.PointerWaveIndex, binding.PointerWaveDescriptor.Index);
                Assert.Same(pointerBank.Waves[binding.PointerWaveIndex],
                    binding.PointerWaveDescriptor);
                Assert.Equal(bank.LocalWaveMap[binding.LocalWaveIndex].PointerWaveIndex,
                    binding.PointerWaveIndex);
            });

            var initialEvents = bank.Components
                .Select(component => Assert.IsType<N64SoundToolsFxInitialEvent>(
                    N64SoundToolsFxInitialEventResolver.Resolve(
                        bank, pointerBank, component)))
                .ToArray();
            var continuations = bank.Components
                .Select(component => Assert.IsType<N64SoundToolsFxContinuation>(
                    N64SoundToolsFxContinuationResolver.Resolve(
                        bank, pointerBank, component)))
                .ToArray();
            Assert.Equal(bank.ComponentCount, initialEvents.Length);
            Assert.Equal(bank.ComponentCount, continuations.Length);
            Assert.All(initialEvents, initialEvent =>
            {
                Assert.Equal(
                    N64SoundToolsFxInitialEventResolver.InterpreterProvenInitialEventBasis,
                    initialEvent.Basis);
                Assert.Equal(initialEvent.EndOffset, initialEvent.EncodedByteLength);
                Assert.InRange(initialEvent.EndOffset, 1,
                    bank.Components[initialEvent.ComponentIndex].OpaqueData.Count);
                Assert.Equal(0x7F, initialEvent.PanOperandRaw);
                Assert.Equal(0x3F, initialEvent.RuntimePan);
                Assert.Equal(N64SoundToolsFxInitialEventResolver.NoteKind,
                    initialEvent.NoteKind);
            });
            Assert.Equal(expected.ExpectedVolumeOperandHistogram,
                ByteHistogram(initialEvents.Select(static initialEvent =>
                    initialEvent.VolumeOperandRaw)));
            Assert.Equal(expected.WrappedPrefixComponents,
                initialEvents.Count(static initialEvent =>
                    initialEvent.LeadingLoopCountRaw != null));
            Assert.All(initialEvents.Where(static initialEvent =>
                    initialEvent.LeadingLoopCountRaw != null),
                static initialEvent => Assert.Equal((byte)0xFF,
                    initialEvent.LeadingLoopCountRaw));

            var normalizedInitialEventBuild = NormalizeInitialEventBuild(
                expected.WholeSha256, initialEvents, continuations);
            Assert.Equal(expected.NormalizedInitialEventByteLength,
                normalizedInitialEventBuild.Length);
            Assert.Equal(expected.NormalizedInitialEventSha256,
                Hash(normalizedInitialEventBuild));
            normalizedInitialEventCorpus.AddRange(normalizedInitialEventBuild);

            totalResolvedInitialEvents += initialEvents.Length;
            totalClassifiedContinuations += continuations.Length;
            initialEventsByLengthMode.AddRange(initialEvents.Select(static initialEvent =>
                initialEvent.LengthMode));
            continuationClassifications.AddRange(continuations.Select(static continuation =>
                continuation.Classification));
            initialEventNotes.AddRange(initialEvents.Select(static initialEvent =>
                initialEvent.NoteValueRaw));
            initialEventVolumes.AddRange(initialEvents.Select(static initialEvent =>
                initialEvent.VolumeOperandRaw));
            initialEventLengths.AddRange(initialEvents.Select(static initialEvent =>
                initialEvent.LengthRaw));
            initialEventLengthEncodingByteLengths.AddRange(initialEvents.Select(static initialEvent =>
                initialEvent.LengthEncodingByteLength));
            initialEventEnvelopeSignatures.AddRange(initialEvents.Select(static initialEvent =>
                string.Join(",",
                    initialEvent.Envelope.SpeedRaw,
                    initialEvent.Envelope.InitialVolumeRaw,
                    initialEvent.Envelope.AttackSpeedRaw,
                    initialEvent.Envelope.PeakVolumeRaw,
                    initialEvent.Envelope.DecaySpeedRaw,
                    initialEvent.Envelope.SustainVolumeRaw,
                    initialEvent.Envelope.ReleaseSpeedRaw)));

            if (expected.RomName.StartsWith("Spider-Man", StringComparison.Ordinal))
            {
                Assert.Equal(334, bank.LocalWaveMap[334].PointerWaveIndex);
                Assert.Equal(339, bank.LocalWaveMap[335].PointerWaveIndex);
                Assert.Equal(new byte[] { 0xFF, 0xFF, 0x80, 0xE2 },
                    bank.Components[^1].OpaqueData.TakeLast(4));
                Assert.Equal(
                    N64SoundToolsFxInitialWaveResolver.LeadingOpcode95OneByteThen81Basis,
                    initialWaveBindings[59].Basis);
                Assert.Equal(59, initialWaveBindings[59].LocalWaveIndex);
                Assert.Equal(423, initialWaveBindings[419].LocalWaveIndex);
                Assert.Equal(423, initialWaveBindings[451].LocalWaveIndex);
                Assert.Equal(425, initialWaveBindings[421].LocalWaveIndex);
                Assert.Equal(425, initialWaveBindings[447].LocalWaveIndex);

                var wrappedEvent = initialEvents[59];
                Assert.Equal(0x2333, bank.Components[59].FxDataOffset);
                Assert.Equal(21, bank.Components[59].OpaqueData.Count);
                Assert.Equal(0x2343, bank.Components[59].FxDataOffset + 0x10);
                Assert.Equal("95FF813B84017F017F017F109C7FA67F3080B29680",
                    Convert.ToHexString(bank.Components[59].OpaqueData.ToArray()));
                Assert.Equal((byte)0xFF, wrappedEvent.LeadingLoopCountRaw);
                Assert.Equal(0x30, wrappedEvent.NoteValueRaw);
                Assert.Equal(N64SoundToolsFxContinuationResolver.InfiniteRepeatClassification,
                    continuations[59].Classification);
                Assert.Equal(2, continuations[59].RecognizedByteLength);

                var alternateNoteEvent = initialEvents[43];
                Assert.Equal(43, alternateNoteEvent.ComponentIndex);
                Assert.Equal(0x2215, bank.Components[43].FxDataOffset);
                Assert.Equal(18, bank.Components[43].OpaqueData.Count);
                Assert.Equal(0x2223, bank.Components[43].FxDataOffset + 0x0E);
                Assert.Equal("812B84017F017F017F109C7FA67F2C811880",
                    Convert.ToHexString(bank.Components[43].OpaqueData.ToArray()));
                Assert.Equal(0x2C, alternateNoteEvent.NoteValueRaw);
                Assert.Equal(0x118, alternateNoteEvent.LengthRaw);
                Assert.Equal(2, alternateNoteEvent.LengthEncodingByteLength);
                Assert.Equal(N64SoundToolsFxContinuationResolver.StopAfterFiniteEventClassification,
                    continuations[43].Classification);

                var trailingE2 = continuations[^1];
                Assert.Equal(
                    N64SoundToolsFxContinuationResolver.StopUnreachableWhileIndefiniteClassification,
                    trailingE2.Classification);
                Assert.Equal(1, trailingE2.RecognizedByteLength);
                Assert.Equal(initialEvents[^1].EndOffset + 1,
                    trailingE2.UninterpretedAfterStopOffset);
                Assert.Equal(new byte[] { 0xE2 }, trailingE2.UninterpretedAfterStopRaw);
            }

            totalComponents += bank.ComponentCount;
            totalEffects += bank.EffectCount;
            totalLocalWaves += bank.LocalWaveCount;
            totalOpaqueBytes += bank.OpaqueComponentRegionRaw.Count;
            totalNotFourAligned += bank.Components.Count(static component =>
                component.FxDataOffset % 4 != 0);
            totalOdd += bank.Components.Count(static component =>
                (component.FxDataOffset & 1) != 0);
            totalResolvedInitialWaveBindings += initialWaveBindings.Length;
            totalDirectInitialWavePrefixes += expected.DirectPrefixComponents;
            totalWrappedInitialWavePrefixes += expected.WrappedPrefixComponents;
            totalUniqueInitialWaves += uniqueInitialWaves.Length;
            totalExtraInitialWaveReferences += expected.ExtraInitialWaveReferences;

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
        Assert.Equal(1_680, totalResolvedInitialWaveBindings);
        Assert.Equal(1_679, totalDirectInitialWavePrefixes);
        Assert.Equal(1, totalWrappedInitialWavePrefixes);
        Assert.Equal(1_608, totalUniqueInitialWaves);
        Assert.Equal(72, totalExtraInitialWaveReferences);
        Assert.Equal(1_680, totalResolvedInitialEvents);
        Assert.Equal(1_680, totalClassifiedContinuations);
        Assert.Equal(1_340, initialEventsByLengthMode.Count(static mode =>
            mode == N64SoundToolsFxInitialEventResolver.FiniteLengthMode));
        Assert.Equal(340, initialEventsByLengthMode.Count(static mode =>
            mode == N64SoundToolsFxInitialEventResolver.IndefiniteLengthMode));
        Assert.Equal(1_339, continuationClassifications.Count(static classification =>
            classification ==
            N64SoundToolsFxContinuationResolver.StopAfterFiniteEventClassification));
        Assert.Equal(340, continuationClassifications.Count(static classification =>
            classification ==
            N64SoundToolsFxContinuationResolver.StopUnreachableWhileIndefiniteClassification));
        Assert.Equal(1, continuationClassifications.Count(static classification =>
            classification == N64SoundToolsFxContinuationResolver.InfiniteRepeatClassification));
        Assert.Equal(1_679, initialEventNotes.Count(static note => note == 0x30));
        Assert.Equal(1, initialEventNotes.Count(static note => note == 0x2C));
        Assert.Equal(
            "00:13,48:1,4B:1,50:1,64:1,66:1,67:1,69:1,6D:1,6E:1," +
            "72:1,78:5,7A:1,7D:2,7F:1649",
            ByteHistogram(initialEventVolumes));
        Assert.Equal(736, initialEventLengthEncodingByteLengths.Count(static length => length == 1));
        Assert.Equal(944, initialEventLengthEncodingByteLengths.Count(static length => length == 2));
        Assert.Equal(340, initialEventLengths.Count(static length => length == 0x7FFF));
        var envelopeHistogram = initialEventEnvelopeSignatures
            .GroupBy(static signature => signature)
            .ToDictionary(static group => group.Key, static group => group.Count(),
                StringComparer.Ordinal);
        Assert.Equal(2, envelopeHistogram.Count);
        Assert.Equal(1_666, envelopeHistogram["1,127,1,127,1,127,16"]);
        Assert.Equal(14, envelopeHistogram["1,127,1,127,32,64,16"]);
        Assert.Equal(NormalizedInitialEventCorpusLength,
            normalizedInitialEventCorpus.Count);
        Assert.Equal(NormalizedInitialEventCorpusSha256,
            Hash(normalizedInitialEventCorpus));
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
        Assert.Equal(3, romJson["schemaVersion"]!.GetValue<int>());
        Assert.Equal("resolved", romJson["initialEventStatus"]!.GetValue<string>());
        Assert.Equal(178, romJson["resolvedInitialEventCount"]!.GetValue<int>());
        Assert.Equal("classified",
            romJson["continuationClassificationStatus"]!.GetValue<string>());
        Assert.Equal(178, romJson["classifiedContinuationCount"]!.GetValue<int>());
        Assert.Equal("romUniqueSingleton", romJson["pointerBindingBasis"]!.GetValue<string>());
        Assert.Equal("callerSupplied", explicitJson["pointerBindingBasis"]!.GetValue<string>());
        romJson["pointerBindingBasis"] = "normalized";
        explicitJson["pointerBindingBasis"] = "normalized";
        Assert.True(JsonNode.DeepEquals(romJson, explicitJson));
    }

    private static byte[] BuildFxData(byte[][] componentPayloads, ushort[] localWaveMap)
    {
        if (componentPayloads.Length == 0 || componentPayloads.Any(static payload => payload.Length == 0))
            throw new ArgumentException("Synthetic BFX components must be nonempty.", nameof(componentPayloads));
        if (localWaveMap.Length == 0)
            throw new ArgumentException("Synthetic BFX local-wave map must be nonempty.", nameof(localWaveMap));

        var componentDataOffset = N64SoundToolsFxBank.HeaderSize +
            componentPayloads.Length * N64SoundToolsFxBank.ComponentEntrySize;
        var waveTableOffset = componentDataOffset + componentPayloads.Sum(static payload => payload.Length);
        if ((waveTableOffset & 1) != 0)
            throw new ArgumentException("Synthetic BFX component region must have even length.",
                nameof(componentPayloads));

        var data = new byte[waveTableOffset + localWaveMap.Length * 2];
        WriteI32(data, 0x00, componentPayloads.Length);
        WriteI32(data, 0x04, componentPayloads.Length);
        WriteI32(data, 0x08, localWaveMap.Length);
        WriteU32(data, 0x14, (uint)waveTableOffset);

        var componentOffset = componentDataOffset;
        for (var i = 0; i < componentPayloads.Length; i++)
        {
            var entryOffset = N64SoundToolsFxBank.HeaderSize +
                i * N64SoundToolsFxBank.ComponentEntrySize;
            WriteU32(data, entryOffset, (uint)componentOffset);
            WriteI32(data, entryOffset + 4, 100);
            componentPayloads[i].CopyTo(data, componentOffset);
            componentOffset += componentPayloads[i].Length;
        }

        for (var i = 0; i < localWaveMap.Length; i++)
            WriteU16(data, waveTableOffset + i * 2, localWaveMap[i]);

        return data;
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

    private static string ByteHistogram(IEnumerable<byte> values) =>
        string.Join(",", values
            .GroupBy(static value => value)
            .OrderBy(static group => group.Key)
            .Select(static group => $"{group.Key:X2}:{group.Count()}"));

    private static byte[] NormalizeInitialEventBuild(
        string wholeFxBankSha256,
        N64SoundToolsFxInitialEvent[] initialEvents,
        N64SoundToolsFxContinuation[] continuations)
    {
        if (initialEvents.Length != continuations.Length)
            throw new ArgumentException("Initial-event and continuation counts differ.");

        // Culture-free corpus contract: raw 32-byte whole-BFX SHA and u16 BE
        // component count, followed by a fixed 29-byte record per component
        // (plus any preserved uninterpreted tail bytes). Each record first
        // writes its index, encoded length, and end offset as big-endian u16s.
        // It then writes loop presence/raw, env[7], pan/runtime-pan/volume/note,
        // note kind, packed length/width/mode, continuation class/width, and a
        // big-endian tail offset (FFFF for null), tail length, and tail bytes.
        var normalized = new List<byte>(34 + initialEvents.Length * 29 + 1);
        normalized.AddRange(Convert.FromHexString(wholeFxBankSha256));
        AppendUInt16BigEndian(normalized, initialEvents.Length);
        for (var i = 0; i < initialEvents.Length; i++)
        {
            var initialEvent = initialEvents[i];
            var continuation = continuations[i];
            AppendUInt16BigEndian(normalized, initialEvent.ComponentIndex);
            AppendUInt16BigEndian(normalized, initialEvent.EncodedByteLength);
            AppendUInt16BigEndian(normalized, initialEvent.EndOffset);
            normalized.Add(initialEvent.LeadingLoopCountRaw == null ? (byte)0 : (byte)1);
            normalized.Add(initialEvent.LeadingLoopCountRaw ?? (byte)0);
            normalized.Add(initialEvent.Envelope.SpeedRaw);
            normalized.Add(initialEvent.Envelope.InitialVolumeRaw);
            normalized.Add(initialEvent.Envelope.AttackSpeedRaw);
            normalized.Add(initialEvent.Envelope.PeakVolumeRaw);
            normalized.Add(initialEvent.Envelope.DecaySpeedRaw);
            normalized.Add(initialEvent.Envelope.SustainVolumeRaw);
            normalized.Add(initialEvent.Envelope.ReleaseSpeedRaw);
            normalized.Add(initialEvent.PanOperandRaw);
            normalized.Add(initialEvent.RuntimePan);
            normalized.Add(initialEvent.VolumeOperandRaw);
            normalized.Add(initialEvent.NoteValueRaw);
            normalized.Add(initialEvent.NoteKind switch
            {
                N64SoundToolsFxInitialEventResolver.NoteKind => (byte)0,
                N64SoundToolsFxInitialEventResolver.RestKind => (byte)1,
                _ => throw new InvalidDataException("Unknown normalized note kind")
            });
            AppendUInt16BigEndian(normalized, initialEvent.LengthRaw);
            normalized.Add(checked((byte)initialEvent.LengthEncodingByteLength));
            normalized.Add(initialEvent.LengthMode switch
            {
                N64SoundToolsFxInitialEventResolver.FiniteLengthMode => (byte)0,
                N64SoundToolsFxInitialEventResolver.IndefiniteLengthMode => (byte)1,
                _ => throw new InvalidDataException("Unknown normalized length mode")
            });
            normalized.Add(continuation.Classification switch
            {
                N64SoundToolsFxContinuationResolver.StopAfterFiniteEventClassification => (byte)0,
                N64SoundToolsFxContinuationResolver.StopUnreachableWhileIndefiniteClassification => (byte)1,
                N64SoundToolsFxContinuationResolver.InfiniteRepeatClassification => (byte)2,
                _ => throw new InvalidDataException("Unknown normalized continuation classification")
            });
            normalized.Add(checked((byte)continuation.RecognizedByteLength));
            AppendUInt16BigEndian(normalized,
                continuation.UninterpretedAfterStopOffset ?? ushort.MaxValue);
            var tail = continuation.UninterpretedAfterStopRaw?.ToArray() ?? [];
            normalized.Add(checked((byte)tail.Length));
            normalized.AddRange(tail);
        }

        return normalized.ToArray();
    }

    private static void AppendUInt16BigEndian(List<byte> destination, int value)
    {
        if ((uint)value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        destination.Add((byte)(value >> 8));
        destination.Add((byte)value);
    }

    private static string Hash(IEnumerable<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data.ToArray()));

    private static byte[] AssertPinnedSlice(
        byte[] data,
        int offset,
        int length,
        string expectedSha256,
        string? expectedHex = null)
    {
        Assert.InRange(offset, 0, data.Length - length);
        var slice = data.AsSpan(offset, length).ToArray();
        if (expectedHex != null)
            Assert.Equal(expectedHex, Convert.ToHexString(slice));
        Assert.Equal(expectedSha256, Hash(slice));
        return slice;
    }

    private static void AssertDispatchEntry(
        byte[] boot,
        int tableOffset,
        byte opcode,
        uint expectedHandlerAddress)
    {
        var entryOffset = checked(tableOffset + (opcode & 0x7F) * sizeof(uint));
        Assert.Equal(expectedHandlerAddress, ReadWord(boot, entryOffset));
    }

    private static uint ReadWord(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, sizeof(uint)));

    private static uint DecodeLuiAddiuAddress(uint lui, uint addiu) =>
        unchecked((uint)((int)((lui & 0xFFFFu) << 16) + (short)(addiu & 0xFFFFu)));

    private static uint EncodeJal(uint address) =>
        0x0C000000u | ((address >> 2) & 0x03FFFFFFu);

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
        uint BootRamBase,
        int Opcode81HandlerOffset,
        uint Opcode81HandlerAddress,
        string BootSha256,
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
        string ExpectedVolumeOperandHistogram,
        ushort MapMinimum,
        ushort MapMaximum,
        int DirectPrefixComponents,
        int WrappedPrefixComponents,
        int UniqueInitialWaves,
        int IdentityInitialWaveBindings,
        int ExtraInitialWaveReferences,
        int InitialLocalMinimum,
        int InitialLocalMaximum,
        string WholeSha256,
        string EntryTableSha256,
        string OpaqueSha256,
        string MapSha256,
        int NormalizedInitialEventByteLength,
        string NormalizedInitialEventSha256,
        InterpreterExpected Interpreter);

    private sealed record InterpreterExpected(
        int DispatchTableOffset,
        int Opcode80HandlerOffset,
        int Opcode84HandlerOffset,
        string Opcode84HandlerSha256,
        int Opcode95HandlerOffset,
        string Opcode95HandlerSha256,
        int Opcode96HandlerOffset,
        int Opcode9CHandlerOffset,
        int OpcodeA6HandlerOffset,
        int BfxProcessLoopSliceOffset,
        uint BfxProcessLoopRamAddress,
        string BfxProcessLoopSliceSha256,
        int InitialNoteLengthSliceOffset,
        string InitialNoteLengthSliceSha256,
        int RestSentinelSliceOffset,
        int IndefiniteLengthGateSliceOffset,
        string IndefiniteLengthGateSliceSha256,
        int ChannelInitializerOffset,
        string ChannelInitializerSha256,
        int EffectStartPrefixOffset,
        string EffectStartPrefixSha256);
}
