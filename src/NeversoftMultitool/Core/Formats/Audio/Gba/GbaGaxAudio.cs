using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     Extracts PCM sound samples from a Game Boy Advance ROM that uses Shin'en's
///     GAX Sound Engine (the Vicarious Visions Tony Hawk GBA line, plus many other
///     GBA titles). GAX has no file magic and no master sample table at a fixed
///     offset, but each <b>wave set</b> is an array of
///     <c>{u32 romAddress, u32 size}</c> records. Its generation-specific null
///     marker, valid in-ROM ranges, monotonic sample pools and bounded empty slots
///     make the complete sparse table self-validating without game-specific
///     addresses. <see cref="TryFindWaveSet" /> retains the original longest-packed-
///     run locator for the compact GAX 1.x song scanner; new extraction code uses
///     <see cref="FindWaveSets" /> to expose every bank and preserve slot indices.
///
///     Samples are mono, uncompressed PCM8 (native GBA DirectSound): GAX 1/2 use
///     signed bytes and GAX 3 uses unsigned bytes centred at 0x80. This class
///     widens both conventions to PCM16 for standard WAV output. The mixing rate
///     is not carried per sample, so callers supply a playback rate (11025 Hz is a
///     useful inspection default).
/// </summary>
public static class GbaGaxAudio
{
    private const uint RomBase = 0x08000000;
    private const int MinRunLength = 16;      // guards against coincidental short runs
    private const int MaxSampleSize = 0x20000; // 128 KB; real samples are far smaller
    private const int MinBankSampleCount = 8;
    private const int MaxBankSlots = 4096;
    private const int MaxConsecutiveEmptySlots = 32;
    public const int DefaultSampleRate = 11025;

    // The version token that follows changed across builds: early ("v1.99d")
    // carries a leading 'v', later ("3.05A") does not — so the fingerprint stops
    // before it. The trailing space keeps it from matching unrelated text.
    private static ReadOnlySpan<byte> EngineSignature => "GAX Sound Engine "u8;

    public readonly record struct GaxSample(int Index, uint Address, int Size);

    public enum GaxPcmEncoding
    {
        Signed8,
        Unsigned8
    }

    /// <summary>
    ///     One complete GAX sample table. <see cref="GaxSample.Index" /> is the
    ///     original one-based table slot, so unused <c>{0,0}</c> slots remain
    ///     observable as gaps instead of renumbering instrument references.
    /// </summary>
    public sealed record GaxWaveSet(
        int Index,
        int TableOffset,
        int SlotCount,
        GaxPcmEncoding Encoding,
        IReadOnlyList<GaxSample> Samples);

    /// <summary>True when the ROM carries the GAX engine fingerprint string.</summary>
    public static bool IsGaxRom(ReadOnlySpan<byte> rom) => FindSignature(rom) >= 0;

    /// <summary>The numeric engine generation from the banner, or zero when it cannot be parsed.</summary>
    public static int GetEngineMajorVersion(ReadOnlySpan<byte> rom)
    {
        var at = FindSignature(rom);
        if (at < 0)
            return 0;

        var cursor = at + EngineSignature.Length;
        if (cursor < rom.Length && rom[cursor] is (byte)'v' or (byte)'V')
            cursor++;

        var version = 0;
        var digits = 0;
        while (cursor < rom.Length && rom[cursor] is >= (byte)'0' and <= (byte)'9')
        {
            var digit = rom[cursor] - '0';
            if (version > (int.MaxValue - digit) / 10)
                return 0;
            version = version * 10 + digit;
            cursor++;
            digits++;
        }

        return digits == 0 ? 0 : version;
    }

    /// <summary>The full "GAX Sound Engine v…" banner (version + build date + author), or null.</summary>
    public static string? GetVersionBanner(ReadOnlySpan<byte> rom)
    {
        var at = FindSignature(rom);
        if (at < 0)
            return null;
        var end = at;
        // The banner is NUL-terminated; the © byte (0xA9) ends the printable head.
        while (end < rom.Length && rom[end] != 0 && rom[end] != 0xA9)
            end++;
        return Encoding.Latin1.GetString(rom[at..end]).TrimEnd();
    }

    /// <summary>
    ///     Locates the wave set and returns one record per sample. Returns false
    ///     when no sufficiently long contiguous <c>{addr,size}</c> run is present.
    /// </summary>
    public static bool TryFindWaveSet(ReadOnlySpan<byte> rom, out int tableOffset, out List<GaxSample> samples)
    {
        tableOffset = -1;
        samples = [];
        var bestOffset = -1;
        var bestCount = 0;

        // Records are 4-aligned in the corpus; the pointers inside are byte-granular.
        for (var off = 0; off + 8 <= rom.Length; off += 4)
        {
            if (!TryReadRecord(rom, off, out var addr, out var size))
                continue;

            // Only start a run where the PREVIOUS record does not flow into this
            // one, so each maximal run is measured once from its true start.
            if (off >= 4 && TryReadRecord(rom, off - 8, out var prevAddr, out var prevSize)
                         && prevAddr + (uint)prevSize == addr)
                continue;

            var count = 1;
            var expected = addr + (uint)size;
            var cursor = off + 8;
            while (TryReadRecord(rom, cursor, out var nextAddr, out var nextSize) && nextAddr == expected)
            {
                count++;
                expected = nextAddr + (uint)nextSize;
                cursor += 8;
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestOffset = off;
            }
        }

        if (bestCount < MinRunLength || bestOffset < 0)
            return false;

        tableOffset = bestOffset;
        samples = new List<GaxSample>(bestCount);
        for (var i = 0; i < bestCount; i++)
        {
            var addr = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(bestOffset + i * 8, 4));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(bestOffset + i * 8 + 4, 4));
            samples.Add(new GaxSample(i, addr, size));
        }

        return true;
    }

    /// <summary>
    ///     Finds every complete sample table in a GAX ROM. Unlike
    ///     <see cref="TryFindWaveSet" />, this follows the table through unused
    ///     slots and non-contiguous pools instead of returning only its longest
    ///     packed run. The Tony Hawk carts contain two banks (music and shared
    ///     effects); the old run scan exposed only a fraction of one of them.
    /// </summary>
    public static List<GaxWaveSet> FindWaveSets(ReadOnlySpan<byte> rom)
    {
        var majorVersion = GetEngineMajorVersion(rom);
        if (majorVersion == 0)
            return [];

        var encoding = majorVersion >= 3 ? GaxPcmEncoding.Unsigned8 : GaxPcmEncoding.Signed8;
        var candidates = new List<WaveSetCandidate>();

        for (var offset = 0; offset + 16 <= rom.Length; offset += 4)
        {
            var markerAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));
            var markerSize = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset + 4, 4));
            if (markerSize != 0)
                continue;

            // GAX 1/2 use {0,0}; GAX 3 uses {sample-pool boundary,0}.
            if (majorVersion >= 3 ? !IsRomAddress(markerAddress, rom.Length) : markerAddress != 0)
                continue;
            if (!TryReadBankRecord(rom, offset + 8, out _, out _))
                continue;

            var samples = new List<GaxSample>();
            uint previousEnd = 0;
            var consecutiveEmpty = 0;
            var slot = 1;
            for (; slot <= MaxBankSlots && offset + (slot + 1) * 8 <= rom.Length; slot++)
            {
                var entryOffset = offset + slot * 8;
                var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(entryOffset, 4));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(entryOffset + 4, 4));
                if (address == 0 && rawSize == 0)
                {
                    consecutiveEmpty++;
                    if (consecutiveEmpty > MaxConsecutiveEmptySlots)
                        break;
                    continue;
                }

                if (!TryReadBankRecord(rom, entryOffset, out address, out var size))
                    break;
                if (previousEnd != 0 && address < previousEnd)
                    break;

                consecutiveEmpty = 0;
                previousEnd = address + (uint)size;
                samples.Add(new GaxSample(slot, address, size));
            }

            if (samples.Count >= MinBankSampleCount)
                candidates.Add(new WaveSetCandidate(offset, offset + slot * 8, slot - 1, samples));
        }

        // An unused slot is itself {0,0}, so it can look like the start of a
        // second table. Keep only the outermost candidate that contains it.
        var outermost = candidates
            .Where(candidate => !candidates.Any(other =>
                other.TableOffset < candidate.TableOffset && candidate.TableOffset < other.EndOffset))
            .OrderBy(candidate => candidate.TableOffset)
            .ToArray();

        var sets = new List<GaxWaveSet>(outermost.Length);
        for (var i = 0; i < outermost.Length; i++)
        {
            var candidate = outermost[i];
            sets.Add(new GaxWaveSet(
                i,
                candidate.TableOffset,
                candidate.SlotCount,
                encoding,
                candidate.Samples));
        }

        return sets;
    }

    /// <summary>Raw PCM8 sample bytes for one wave-set record.</summary>
    public static ReadOnlySpan<byte> GetSampleBytes(ReadOnlySpan<byte> rom, GaxSample sample)
    {
        var start = (int)(sample.Address - RomBase);
        return rom.Slice(start, sample.Size);
    }

    /// <summary>Widens signed 8-bit PCM to 16-bit PCM (sample &lt;&lt; 8) for a standard WAV.</summary>
    public static short[] DecodeToPcm16(ReadOnlySpan<byte> raw)
        => DecodeToPcm16(raw, GaxPcmEncoding.Signed8);

    /// <summary>
    ///     Converts the engine generation's native PCM8 convention to PCM16.
    ///     GAX 1/2 samples are signed; GAX 3 samples are unsigned around 0x80.
    /// </summary>
    public static short[] DecodeToPcm16(ReadOnlySpan<byte> raw, GaxPcmEncoding encoding)
    {
        var pcm = new short[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var value = encoding == GaxPcmEncoding.Unsigned8
                ? raw[i] - 128
                : (sbyte)raw[i];
            pcm[i] = (short)(value << 8);
        }
        return pcm;
    }

    private static bool TryReadRecord(ReadOnlySpan<byte> rom, int off, out uint addr, out int size)
    {
        addr = 0;
        size = 0;
        if (off < 0 || off + 8 > rom.Length)
            return false;
        addr = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(off, 4));
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(off + 4, 4));
        if (rawSize == 0 || rawSize > MaxSampleSize)
            return false;
        if (addr < RomBase || (ulong)addr + rawSize > (ulong)RomBase + (uint)rom.Length)
            return false;
        size = (int)rawSize;
        return true;
    }

    private static bool TryReadBankRecord(ReadOnlySpan<byte> rom, int offset, out uint address, out int size)
    {
        address = 0;
        size = 0;
        if (offset < 0 || offset + 8 > rom.Length)
            return false;

        address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset + 4, 4));
        if (rawSize == 0 || rawSize > (uint)rom.Length || !IsRomAddress(address, rom.Length))
            return false;

        var end = (ulong)address + rawSize;
        if (end > (ulong)RomBase + (uint)rom.Length)
            return false;

        size = (int)rawSize;
        return true;
    }

    private static bool IsRomAddress(uint address, int romLength) =>
        address >= RomBase && (ulong)address < (ulong)RomBase + (uint)romLength;

    private sealed record WaveSetCandidate(
        int TableOffset,
        int EndOffset,
        int SlotCount,
        List<GaxSample> Samples);

    private static int FindSignature(ReadOnlySpan<byte> rom)
    {
        var idx = rom.IndexOf(EngineSignature);
        return idx;
    }
}
