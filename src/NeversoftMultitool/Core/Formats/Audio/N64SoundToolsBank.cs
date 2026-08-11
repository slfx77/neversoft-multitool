using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     A checked, inspection-only view of one paired Nintendo 64 Sound Tools
///     pointer bank (<c>.ptr</c>) and wave bank (<c>.wbk</c>).
///     <para>
///         This deliberately stops before sample-rate inference, cue/song
///         ownership, or audio decoding. Sound Tools PTR/WBK files are not SGI
///         <c>ALBankFile</c> CTL/TBL containers, although each PTR descriptor
///         embeds the stock ADPCM wavetable/book/loop shapes.
///     </para>
/// </summary>
public sealed record N64SoundToolsBank(
    N64SoundToolsPointerBank PointerBank,
    N64SoundToolsWaveBank WaveBank)
{
    private static ReadOnlySpan<byte> PointerMagic => "N64 PtrTablesV2\0"u8;
    private static ReadOnlySpan<byte> WaveMagic => "N64 WaveTables \0"u8;

    public const int PointerHeaderSize = 0x30;
    public const int WaveHeaderSize = 0x10;

    public static bool HasPointerMagic(ReadOnlySpan<byte> data) => data.StartsWith(PointerMagic);

    public static bool HasWaveMagic(ReadOnlySpan<byte> data) => data.StartsWith(WaveMagic);

    public static N64SoundToolsBank Parse(ReadOnlySpan<byte> pointerData, ReadOnlySpan<byte> waveData)
    {
        var pointerBank = ParsePointer(pointerData);
        var waveBank = ParseWaveBank(waveData, pointerBank.Waves);
        return new N64SoundToolsBank(pointerBank, waveBank);
    }

    /// <summary>
    ///     Parses and fully validates a serialized Sound Tools PTR bank without
    ///     requiring its WBK payload. This is sufficient for consumers that
    ///     need the descriptor index space but do not access encoded waves.
    /// </summary>
    public static N64SoundToolsPointerBank ParsePointer(ReadOnlySpan<byte> data)
    {
        var pointerBank = ParsePointerPayload(data);
        ValidatePointerWaveLayout(pointerBank.Waves);
        ValidateLoopRanges(pointerBank.Waves);
        return pointerBank;
    }

    private static N64SoundToolsPointerBank ParsePointerPayload(ReadOnlySpan<byte> data)
    {
        Require(data.Length >= PointerHeaderSize, "PTR header is truncated");
        Require(data[..PointerMagic.Length].SequenceEqual(PointerMagic),
            "PTR magic is not 'N64 PtrTablesV2\\0'");
        var headerFlagsRaw = ReadUInt32(data, 0x10, "PTR flags");
        Require(headerFlagsRaw == 0, "PTR on-disk flags are nonzero");
        var waveBankNameRaw = new uint[3];
        for (var i = 0; i < waveBankNameRaw.Length; i++)
        {
            waveBankNameRaw[i] = ReadUInt32(data, 0x14 + i * 4, $"PTR wbk_name word {i}");
            Require(waveBankNameRaw[i] == 0, $"PTR unused wbk_name word {i} is nonzero");
        }

        var waveCountRaw = ReadUInt32(data, 0x20, "PTR wave count");
        Require(waveCountRaw is > 0 and <= int.MaxValue, "PTR wave count is invalid");
        var waveCount = (int)waveCountRaw;
        var baseNoteOffset = ToOffset(ReadUInt32(data, 0x24, "PTR base-note table offset"),
            data.Length, "PTR base-note table offset");
        var fineTuneOffset = ToOffset(ReadUInt32(data, 0x28, "PTR fine-tune workspace offset"),
            data.Length, "PTR fine-tune workspace offset");
        var pointerTableOffset = ToOffset(ReadUInt32(data, 0x2C, "PTR descriptor-pointer table offset"),
            data.Length, "PTR descriptor-pointer table offset");

        Require(pointerTableOffset >= PointerHeaderSize,
            "PTR descriptor graph overlaps the header");
        Require(waveCount <= (pointerTableOffset - PointerHeaderSize) / 0xA0,
            "PTR wave count cannot fit in the descriptor graph");
        var expectedBaseNoteOffset = CheckedAdd(pointerTableOffset, CheckedMultiply(waveCount, 4,
            "PTR descriptor-pointer table size"), "PTR base-note table offset");
        Require(baseNoteOffset == expectedBaseNoteOffset,
            "PTR base-note table does not immediately follow the descriptor pointers");
        var baseNoteEnd = CheckedAdd(baseNoteOffset, waveCount, "PTR base-note table end");
        var expectedFineTuneOffset = Align4(baseNoteEnd);
        Require(fineTuneOffset == expectedFineTuneOffset,
            "PTR fine-tune workspace does not follow the aligned base-note table");
        var logicalSize = CheckedAdd(fineTuneOffset, CheckedMultiply(waveCount, 4,
            "PTR fine-tune workspace size"), "PTR logical size");
        Require(logicalSize <= data.Length, "PTR fine-tune workspace is truncated");
        var outerTailLength = data.Length - logicalSize;
        Require(outerTailLength <= 8, "PTR has more than eight bytes after its logical payload");
        Require(AllZero(data[logicalSize..]), "PTR outer payload tail is nonzero");

        var descriptorOffsets = new int[waveCount];
        for (var i = 0; i < waveCount; i++)
        {
            var offset = ToOffset(ReadUInt32(data,
                    CheckedAdd(pointerTableOffset, CheckedMultiply(i, 4, "PTR pointer index"),
                        "PTR pointer position"),
                    $"PTR descriptor pointer {i}"),
                pointerTableOffset,
                $"PTR descriptor pointer {i}");
            Require(offset % 16 == 0, $"PTR descriptor {i} is not 16-byte aligned");
            if (i == 0)
                Require(offset == PointerHeaderSize, "PTR first descriptor does not start at 0x30");
            else
                Require(offset > descriptorOffsets[i - 1], "PTR descriptor pointers are not strictly ascending");
            descriptorOffsets[i] = offset;
        }

        var waves = new N64SoundToolsWaveDescriptor[waveCount];
        for (var i = 0; i < waveCount; i++)
        {
            var descriptorOffset = descriptorOffsets[i];
            Require(descriptorOffset <= pointerTableOffset - 0xA0,
                $"PTR descriptor {i} is truncated");

            var waveBase = ReadUInt32(data, descriptorOffset, $"PTR descriptor {i} wave base");
            var waveLength = ReadUInt32(data, descriptorOffset + 4, $"PTR descriptor {i} wave length");
            var typeRaw = data[descriptorOffset + 8];
            var flagsRaw = data[descriptorOffset + 9];
            var padRaw = ReadUInt16(data, descriptorOffset + 10, $"PTR descriptor {i} pad");
            var loopOffsetRaw = ReadUInt32(data, descriptorOffset + 12, $"PTR descriptor {i} loop offset");
            var bookOffsetRaw = ReadUInt32(data, descriptorOffset + 16, $"PTR descriptor {i} book offset");
            var descriptorPadding = data.Slice(descriptorOffset + 0x14, 4).ToArray();

            Require(waveLength > 0, $"PTR descriptor {i} has an empty wave range");
            Require(typeRaw == 0, $"PTR descriptor {i} has unsupported type {typeRaw}");
            Require(flagsRaw == 0, $"PTR descriptor {i} has unsupported flags {flagsRaw}");
            Require(padRaw == 0, $"PTR descriptor {i} has a nonzero pad field");
            Require(AllZero(descriptorPadding), $"PTR descriptor {i} trailing pad is nonzero");

            var expectedBookOffset = descriptorOffset + 0x18;
            Require(bookOffsetRaw == (uint)expectedBookOffset,
                $"PTR descriptor {i} book pointer is not file-relative D+0x18");
            var order = ReadInt32(data, expectedBookOffset, $"PTR descriptor {i} book order");
            var predictorCount = ReadInt32(data, expectedBookOffset + 4,
                $"PTR descriptor {i} book predictor count");
            Require(order == 2, $"PTR descriptor {i} book order is not 2");
            Require(predictorCount == 4, $"PTR descriptor {i} predictor count is not 4");
            var coefficients = new short[64];
            for (var coefficient = 0; coefficient < coefficients.Length; coefficient++)
            {
                coefficients[coefficient] = ReadInt16(data,
                    expectedBookOffset + 8 + coefficient * 2,
                    $"PTR descriptor {i} book coefficient {coefficient}");
            }

            N64SoundToolsAdpcmLoop? loop = null;
            var rawRecordEnd = descriptorOffset + 0xA0;
            if (loopOffsetRaw != 0)
            {
                Require(loopOffsetRaw == (uint)rawRecordEnd,
                    $"PTR descriptor {i} loop pointer is not file-relative D+0xA0");
                Require(rawRecordEnd <= pointerTableOffset - 0x2C,
                    $"PTR descriptor {i} loop is truncated");
                var start = ReadUInt32(data, rawRecordEnd, $"PTR descriptor {i} loop start");
                var end = ReadUInt32(data, rawRecordEnd + 4, $"PTR descriptor {i} loop end");
                var countRaw = ReadUInt32(data, rawRecordEnd + 8, $"PTR descriptor {i} loop count");
                var state = new short[16];
                for (var stateIndex = 0; stateIndex < state.Length; stateIndex++)
                {
                    state[stateIndex] = ReadInt16(data,
                        rawRecordEnd + 12 + stateIndex * 2,
                        $"PTR descriptor {i} loop state {stateIndex}");
                }

                loop = new N64SoundToolsAdpcmLoop(start, end, countRaw, state);
                rawRecordEnd += 0x2C;
            }

            var expectedNext = i + 1 == waveCount ? rawRecordEnd : Align16(rawRecordEnd);
            var actualNext = i + 1 == waveCount ? pointerTableOffset : descriptorOffsets[i + 1];
            Require(actualNext == expectedNext,
                i + 1 == waveCount
                    ? $"PTR descriptor {i} does not end exactly at the pointer table"
                    : $"PTR descriptor {i} is not followed by the aligned next descriptor");
            var alignmentPadding = data[rawRecordEnd..actualNext].ToArray();
            Require(AllZero(alignmentPadding), $"PTR descriptor {i} alignment padding is nonzero");

            waves[i] = new N64SoundToolsWaveDescriptor(
                i,
                descriptorOffset,
                waveBase,
                waveLength,
                typeRaw,
                flagsRaw,
                padRaw,
                loopOffsetRaw,
                bookOffsetRaw,
                descriptorPadding,
                new N64SoundToolsAdpcmBook(order, predictorCount, coefficients),
                loop,
                alignmentPadding);
        }

        var baseNoteBytes = data.Slice(baseNoteOffset, waveCount).ToArray();
        var baseNotes = baseNoteBytes.Select(static raw => new N64SoundToolsBaseNote(
            raw,
            unchecked((sbyte)raw),
            unchecked((sbyte)((raw - 48) & 0xFF)))).ToArray();
        var baseNotePadding = data[baseNoteEnd..fineTuneOffset].ToArray();
        Require(AllZero(baseNotePadding), "PTR base-note-table alignment padding is nonzero");
        var fineTuneCells = new N64SoundToolsFineTuneCell[waveCount];
        for (var i = 0; i < fineTuneCells.Length; i++)
        {
            var raw = data.Slice(fineTuneOffset + i * 4, 4).ToArray();
            Require(AllZero(raw.AsSpan(1)),
                $"PTR fine-tune workspace cell {i} has nonzero low padding bytes");
            fineTuneCells[i] = new N64SoundToolsFineTuneCell(raw, unchecked((sbyte)raw[0]));
        }

        return new N64SoundToolsPointerBank(
            data.Length,
            logicalSize,
            headerFlagsRaw,
            waveBankNameRaw,
            pointerTableOffset,
            baseNoteOffset,
            fineTuneOffset,
            waves,
            baseNotes,
            baseNotePadding,
            fineTuneCells,
            data[logicalSize..].ToArray());
    }

    private static N64SoundToolsWaveBank ParseWaveBank(
        ReadOnlySpan<byte> data,
        IReadOnlyList<N64SoundToolsWaveDescriptor> waves)
    {
        Require(data.Length >= WaveHeaderSize, "WBK header is truncated");
        Require(data[..WaveMagic.Length].SequenceEqual(WaveMagic),
            "WBK magic is not 'N64 WaveTables \\0'");

        var waveAlignmentPadding = new byte[waves.Count][];
        long expectedBase = WaveHeaderSize;
        for (var i = 0; i < waves.Count; i++)
        {
            var wave = waves[i];
            Require(wave.WaveBase == expectedBase,
                $"WBK wave {i} base does not match the packed PTR range");
            Require(wave.WaveBase % 16 == 0, $"WBK wave {i} base is not 16-byte aligned");
            Require(wave.WaveLength % 9 == 0,
                $"WBK wave {i} byte length is not a whole number of 9-byte ADPCM frames");
            var end = (long)wave.WaveBase + wave.WaveLength;
            Require(end <= data.Length, $"WBK wave {i} range exceeds the file");

            var paddedEnd = i + 1 == waves.Count ? end : Align16(end);
            Require(paddedEnd <= data.Length, $"WBK wave {i} alignment padding is truncated");
            waveAlignmentPadding[i] = data[(int)end..(int)paddedEnd].ToArray();
            Require(AllZero(waveAlignmentPadding[i]), $"WBK wave {i} alignment padding is nonzero");
            expectedBase = paddedEnd;
        }

        var finalEnd = (long)waves[^1].WaveBase + waves[^1].WaveLength;
        var trailingLength = data.Length - finalEnd;
        Require(trailingLength is >= 0 and <= 15,
            "WBK has more than fifteen bytes after the final wave");
        var trailingPadding = data[(int)finalEnd..].ToArray();
        Require(AllZero(trailingPadding), "WBK trailing alignment padding is nonzero");

        return new N64SoundToolsWaveBank(data.Length, waveAlignmentPadding, trailingPadding);
    }

    private static void ValidateLoopRanges(IReadOnlyList<N64SoundToolsWaveDescriptor> waves)
    {
        foreach (var wave in waves)
        {
            if (wave.Loop is not { } loop)
                continue;
            var capacity = (long)(wave.WaveLength / 9) * 16;
            Require(loop.Start < loop.End,
                $"PTR descriptor {wave.Index} loop range is empty or reversed");
            Require(loop.End <= capacity,
                $"PTR descriptor {wave.Index} loop range exceeds its declared encoded-wave capacity");
        }
    }

    private static void ValidatePointerWaveLayout(
        IReadOnlyList<N64SoundToolsWaveDescriptor> waves)
    {
        long expectedBase = WaveHeaderSize;
        for (var i = 0; i < waves.Count; i++)
        {
            var wave = waves[i];
            Require(wave.WaveBase == expectedBase,
                $"PTR descriptor {i} wave base does not match canonical WBK packing");
            Require(wave.WaveBase % 16 == 0,
                $"PTR descriptor {i} wave base is not 16-byte aligned");
            Require(wave.WaveLength % 9 == 0,
                $"PTR descriptor {i} wave length is not a whole number of 9-byte ADPCM frames");
            var end = (long)wave.WaveBase + wave.WaveLength;
            if (i + 1 < waves.Count)
                expectedBase = Align16(end);
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, string field)
    {
        Require(offset >= 0 && offset <= data.Length - 4, $"{field} is truncated");
        return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset, string field) =>
        unchecked((int)ReadUInt32(data, offset, field));

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, string field)
    {
        Require(offset >= 0 && offset <= data.Length - 2, $"{field} is truncated");
        return BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset, string field) =>
        unchecked((short)ReadUInt16(data, offset, field));

    private static int ToOffset(uint value, int maximumInclusive, string field)
    {
        Require(value <= int.MaxValue && value <= maximumInclusive, $"{field} is out of range");
        return (int)value;
    }

    private static int CheckedMultiply(int left, int right, string field)
    {
        var value = (long)left * right;
        Require(value <= int.MaxValue, $"{field} overflows");
        return (int)value;
    }

    private static int CheckedAdd(int left, int right, string field)
    {
        var value = (long)left + right;
        Require(value <= int.MaxValue, $"{field} overflows");
        return (int)value;
    }

    private static int Align4(int value) => Align(value, 4);

    private static int Align16(int value) => Align(value, 16);

    private static long Align16(long value)
    {
        Require(value <= long.MaxValue - 15, "16-byte alignment overflows");
        return (value + 15) & ~15L;
    }

    private static int Align(int value, int alignment)
    {
        var aligned = ((long)value + alignment - 1) & ~(alignment - 1L);
        Require(aligned <= int.MaxValue, $"{alignment}-byte alignment overflows");
        return (int)aligned;
    }

    private static bool AllZero(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value != 0)
                return false;
        }

        return true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

public sealed record N64SoundToolsPointerBank(
    int SerializedSize,
    int LogicalSize,
    uint FlagsRaw,
    IReadOnlyList<uint> WaveBankNameRaw,
    int DescriptorPointerTableOffset,
    int BaseNoteTableOffset,
    int FineTuneWorkspaceOffset,
    IReadOnlyList<N64SoundToolsWaveDescriptor> Waves,
    IReadOnlyList<N64SoundToolsBaseNote> BaseNotes,
    IReadOnlyList<byte> BaseNoteAlignmentPadding,
    IReadOnlyList<N64SoundToolsFineTuneCell> FineTuneCells,
    IReadOnlyList<byte> OuterTrailingPadding);

public sealed record N64SoundToolsBaseNote(
    byte Raw,
    sbyte CoarseTuneRawSigned,
    sbyte RuntimeBasePitchOffsetSemitones);

public sealed record N64SoundToolsFineTuneCell(
    IReadOnlyList<byte> RawBytes,
    sbyte FineTuneCents);

public sealed record N64SoundToolsWaveBank(
    int SerializedSize,
    IReadOnlyList<byte[]> WaveAlignmentPadding,
    IReadOnlyList<byte> TrailingPadding);

public sealed record N64SoundToolsWaveDescriptor(
    int Index,
    int DescriptorOffset,
    uint WaveBase,
    uint WaveLength,
    byte TypeRaw,
    byte FlagsRaw,
    ushort PadRaw,
    uint LoopOffset,
    uint BookOffset,
    IReadOnlyList<byte> DescriptorPaddingRaw,
    N64SoundToolsAdpcmBook Book,
    N64SoundToolsAdpcmLoop? Loop,
    IReadOnlyList<byte> DescriptorAlignmentPadding);

public sealed record N64SoundToolsAdpcmBook(
    int Order,
    int PredictorCount,
    IReadOnlyList<short> Coefficients);

public sealed record N64SoundToolsAdpcmLoop(
    uint Start,
    uint End,
    uint CountRaw,
    IReadOnlyList<short> State);
