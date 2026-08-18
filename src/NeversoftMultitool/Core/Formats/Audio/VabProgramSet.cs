using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     A VAB bank read the way the sequencer needs it: programs → tones →
///     decoded PCM with loop points, rather than <see cref="VabExtractor" />'s
///     flat per-sample export view.
/// </summary>
/// <remarks>
///     <para>
///         Program attributes live at <c>0x20 + slot×16</c> for all 128 slots
///         (tone count at +0, master volume at +1). The tone-attribute region
///         at 0x820 packs one 0x200 set per USED program (tone count &gt; 0)
///         in ascending slot order — <c>programCount</c> counts used
///         programs, and a bank is free to use HIGH slots: Apocalypse's music
///         banks put their 14 programs at slots 60–75, so indexing the tone
///         region by slot number reads past it into the size table.
///         (The SFX cue work's decomp-verified <c>program×0x200</c> walk is
///         the same rule seen through banks whose used slots happen to be
///         0..N−1.) Tone fields: volume +2, pan +3, centre note +4, fine
///         shift +5, note range +6/+7, ADSR words +16/+18, VAG index +22.
///     </para>
///     <para>
///         The VAG size table (256 × u16, ×8 bytes) follows the tone region
///         at <c>0x820 + programCount×0x200</c>, exactly as
///         <see cref="VabExtractor" /> reads it.
///     </para>
/// </remarks>
public sealed class VabProgramSet
{
    private const int ProgramTableOffset = 0x20;
    private const int ProgramEntrySize = 16;
    private const int ToneRegionOffset = 0x820;
    private const int ToneSetSize = 0x200;
    private const int ToneEntrySize = 32;
    private const int VagSizeTableEntries = 256;

    private readonly byte[] _data;
    private readonly int _vagDataOffset;
    private readonly int[] _vagOffsets;
    private readonly int[] _vagSizes;
    private readonly Dictionary<int, VabPcmSample?> _pcmCache = [];

    public required int ProgramCount { get; init; }
    public required int VagCount { get; init; }
    public required IReadOnlyList<VabProgram> Programs { get; init; }

    private VabProgramSet(byte[] data, int vagDataOffset, int[] vagOffsets, int[] vagSizes)
    {
        _data = data;
        _vagDataOffset = vagDataOffset;
        _vagOffsets = vagOffsets;
        _vagSizes = vagSizes;
    }

    public static VabProgramSet? Parse(byte[] data)
    {
        if (data.Length < ToneRegionOffset
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x56414270) // "pBAV"
        {
            return null;
        }

        var programCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x12));
        var vagCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x16));
        if (programCount == 0 || programCount > 128 || vagCount == 0)
            return null;

        var sizeTableOffset = ToneRegionOffset + programCount * ToneSetSize;
        if (sizeTableOffset + VagSizeTableEntries * 2 > data.Length)
            return null;

        var vagSizes = new int[VagSizeTableEntries];
        for (var i = 0; i < VagSizeTableEntries; i++)
        {
            vagSizes[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(sizeTableOffset + i * 2)) * 8;
        }

        // Mirror VabExtractor's accounting: bodies start after the size table,
        // and entry 0 is a leading skip, not a sample.
        var vagDataOffset = sizeTableOffset + VagSizeTableEntries * 2;
        var vagOffsets = new int[VagSizeTableEntries];
        var running = vagDataOffset + vagSizes[0];
        for (var v = 1; v < VagSizeTableEntries; v++)
        {
            vagOffsets[v] = running;
            running += vagSizes[v];
        }

        var programs = new VabProgram[128];
        var usedIndex = 0;
        for (var slot = 0; slot < 128; slot++)
        {
            var attrOffset = ProgramTableOffset + slot * ProgramEntrySize;
            var toneCount = data[attrOffset];
            var masterVolume = data[attrOffset + 1];
            var tones = new List<VabTone>();
            if (toneCount > 0 && usedIndex < programCount)
            {
                var toneSet = ToneRegionOffset + usedIndex * ToneSetSize;
                usedIndex++;
                for (var t = 0; t < Math.Min((int)toneCount, 16); t++)
                {
                    var tone = toneSet + t * ToneEntrySize;
                    if (tone + ToneEntrySize > data.Length)
                        break;

                    var vagIndex = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(tone + 22));
                    if (vagIndex <= 0 || vagIndex > vagCount)
                        continue;

                    tones.Add(new VabTone(
                        Volume: data[tone + 2],
                        Pan: data[tone + 3],
                        Centre: data[tone + 4],
                        Shift: data[tone + 5],
                        MinNote: data[tone + 6],
                        MaxNote: data[tone + 7],
                        Adsr1: BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tone + 16)),
                        Adsr2: BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tone + 18)),
                        VagIndex: vagIndex));
                }
            }

            programs[slot] = new VabProgram(masterVolume, tones);
        }

        return new VabProgramSet(data, vagDataOffset, vagOffsets, vagSizes)
        {
            ProgramCount = programCount,
            VagCount = vagCount,
            Programs = programs
        };
    }

    /// <summary>
    ///     Decoded PCM for a VAG (1-based), with SPU loop points, cached per
    ///     bank. Null when the sample is empty or out of range.
    /// </summary>
    public VabPcmSample? GetPcm(int vagIndex)
    {
        if (_pcmCache.TryGetValue(vagIndex, out var cached))
            return cached;

        VabPcmSample? sample = null;
        if (vagIndex >= 1 && vagIndex < VagSizeTableEntries && _vagSizes[vagIndex] > 0)
        {
            var offset = _vagOffsets[vagIndex];
            var size = _vagSizes[vagIndex];
            if (offset >= 0 && offset + size <= _data.Length)
                sample = DecodeWithLoop(_data.AsSpan(offset, size));
        }

        _pcmCache[vagIndex] = sample;
        return sample;
    }

    /// <summary>
    ///     SPU-ADPCM decode that also reports the loop the hardware would
    ///     take: block flag bit2 (0x04) marks the loop start, and an
    ///     end-of-stream block (bit0) with bit1 (0x02) set jumps back to it —
    ///     a sustained instrument holds by looping that region. One-shot
    ///     samples (end without the loop bit) report no loop.
    /// </summary>
    internal static VabPcmSample? DecodeWithLoop(ReadOnlySpan<byte> data)
    {
        var output = new List<short>(data.Length * 7 / 4);
        var loopStart = -1;
        var loops = false;
        int prev1 = 0, prev2 = 0;

        for (var offset = 0; offset + 16 <= data.Length; offset += 16)
        {
            var flags = data[offset + 1];
            if ((flags & SpuAdpcm.FlagLoopStart) != 0 && loopStart < 0)
                loopStart = output.Count;

            SpuAdpcm.DecodeBlock(data.Slice(offset, 16), ref prev1, ref prev2, output);

            if ((flags & SpuAdpcm.FlagEnd) != 0)
            {
                loops = (flags & SpuAdpcm.FlagLoop) != 0 && loopStart >= 0;
                break;
            }
        }

        if (output.Count == 0)
            return null;

        return new VabPcmSample(
            [.. output],
            loops ? loopStart : -1,
            loops ? output.Count : -1);
    }
}

/// <summary>A VAB program slot: master volume plus its usable tones.</summary>
public sealed record VabProgram(byte MasterVolume, IReadOnlyList<VabTone> Tones);

/// <summary>One tone attribute row, engine field names.</summary>
public readonly record struct VabTone(
    byte Volume,
    byte Pan,
    byte Centre,
    byte Shift,
    byte MinNote,
    byte MaxNote,
    ushort Adsr1,
    ushort Adsr2,
    short VagIndex);

/// <summary>Decoded sample data; loop bounds are -1 for one-shot samples.</summary>
public sealed record VabPcmSample(short[] Samples, int LoopStart, int LoopEnd)
{
    public bool Loops => LoopStart >= 0;
}
