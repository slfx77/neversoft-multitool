using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     Extracts PCM sound samples from a Game Boy Advance ROM that uses Shin'en's
///     GAX Sound Engine (the Vicarious Visions Tony Hawk GBA line, plus many other
///     GBA titles). GAX has no file magic and no master sample table at a fixed
///     offset, but the <b>wave set</b> — the array of <c>{u32 romAddress, u32 size}</c>
///     records that slices the packed sample bank — is a self-validating anchor:
///     the samples are stored back-to-back, so <c>addr[i] + size[i] == addr[i+1]</c>
///     for the whole run. Scanning for the longest such contiguous run of in-ROM
///     records locates the bank without depending on the (version-specific) song
///     or instrument structures.
///
///     Samples are mono, uncompressed <b>signed 8-bit PCM</b> (native GBA
///     DirectSound); this class widens them to 16-bit for a standard WAV. The GAX
///     mixing rate is engine-global and selected by a driver index at init, so it
///     is not carried per sample — callers supply a playback rate (11025 Hz is a
///     reasonable inspection default). Verified against Tony Hawk's Pro Skater 2
///     (GBA, GAX v1.99d): 101 contiguous samples, all valid PCM.
/// </summary>
public static class GbaGaxAudio
{
    private const uint RomBase = 0x08000000;
    private const int MinRunLength = 16;      // guards against coincidental short runs
    private const int MaxSampleSize = 0x20000; // 128 KB; real samples are far smaller
    public const int DefaultSampleRate = 11025;

    // The version token that follows changed across builds: early ("v1.99d")
    // carries a leading 'v', later ("3.05A") does not — so the fingerprint stops
    // before it. The trailing space keeps it from matching unrelated text.
    private static ReadOnlySpan<byte> EngineSignature => "GAX Sound Engine "u8;

    public readonly record struct GaxSample(int Index, uint Address, int Size);

    /// <summary>True when the ROM carries the GAX engine fingerprint string.</summary>
    public static bool IsGaxRom(ReadOnlySpan<byte> rom) => FindSignature(rom) >= 0;

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

    /// <summary>Raw signed-8-bit sample bytes for one wave-set record.</summary>
    public static ReadOnlySpan<byte> GetSampleBytes(ReadOnlySpan<byte> rom, GaxSample sample)
    {
        var start = (int)(sample.Address - RomBase);
        return rom.Slice(start, sample.Size);
    }

    /// <summary>Widens signed 8-bit PCM to 16-bit PCM (sample &lt;&lt; 8) for a standard WAV.</summary>
    public static short[] DecodeToPcm16(ReadOnlySpan<byte> raw)
    {
        var pcm = new short[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            pcm[i] = (short)((sbyte)raw[i] << 8);
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
        if (addr < RomBase || addr + rawSize > RomBase + (uint)rom.Length)
            return false;
        size = (int)rawSize;
        return true;
    }

    private static int FindSignature(ReadOnlySpan<byte> rom)
    {
        var idx = rom.IndexOf(EngineSignature);
        return idx;
    }
}
