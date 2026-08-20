using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.N64;

/// <summary>
///     The overlay roster the ports DMA from a hardcoded cart address: the PS1
///     <c>.bin</c>/<c>.rel</c> overlays (<c>blackcat</c>, <c>carnage</c>,
///     <c>docock</c>, … <c>venom</c>) plus front-end and codec code.
///     <para>
///         Nothing in the master directory references this region, so
///         <see cref="N64AssetCarver" /> could not see it: 313 KB of THPS2,
///         325 KB of THPS3 and 632 KB of Spider-Man sat uncarved. THPS1 has no
///         such region at all.
///     </para>
///     <para>
///         <b>The manifest's addresses are NOT ROM offsets.</b> Masking the cart
///         domain off (<c>&amp; 0x0FFFFFFF</c>) the way the master-directory
///         reader does for its root pointer lands past the stored data — most
///         payloads then decode as pure zeros, and only the first happens to
///         overlap real code by coincidence. The addresses are load addresses,
///         and a per-ROM fixup maps them to storage. The fixup is DERIVED, and
///         it is what makes the whole reading verifiable: applying it, every
///         payload in every ROM lands on dense data (30/30, 16/16, 16/16).
///     </para>
/// </summary>
internal static class N64OverlayManifest
{
    private const int EntryStride = 0x1C;

    /// <summary>
    ///     Long enough that neighbouring words cannot fake it. The real rosters
    ///     are 16 and 30 entries, and a scan of every boot image finds exactly
    ///     one run of four or more — the same self-locating property the model
    ///     name table has, so no per-game offset is stored.
    /// </summary>
    private const int MinimumRunLength = 4;

    /// <summary>Cart (PI) domain addresses always carry this top nibble.</summary>
    private const uint CartDomainNibble = 0xB;

    /// <summary>A single overlay's stored payload.</summary>
    internal readonly record struct Payload(string Name, string Extension, int Offset, int Length);

    internal readonly record struct Entry(
        string Name, uint CodeAddress, int CodeSize, uint RelocAddress, int RelocSize);

    /// <summary>
    ///     Reads the roster, or an empty list when the image does not contain
    ///     exactly one qualifying run.
    /// </summary>
    internal static IReadOnlyList<Entry> ReadEntries(N64BootImage? boot)
    {
        if (boot == null)
            return [];

        IReadOnlyList<Entry>? found = null;
        var offset = 0;
        while (offset + EntryStride <= boot.Length)
        {
            var run = ReadRun(boot, offset);
            if (run.Count < MinimumRunLength)
            {
                offset += 4;
                continue;
            }

            if (found != null)
                return [];

            found = run;
            offset += run.Count * EntryStride;
        }

        return found ?? [];
    }

    private static List<Entry> ReadRun(N64BootImage boot, int start)
    {
        var entries = new List<Entry>();
        for (var offset = start; offset + EntryStride <= boot.Length; offset += EntryStride)
        {
            // The trailing two words are zero in every shipped record, which is
            // most of what keeps an ordinary pointer pair from matching.
            if (boot.ReadUInt32(offset + 0x14) != 0 || boot.ReadUInt32(offset + 0x18) != 0)
                break;

            var codeAddress = boot.ReadUInt32(offset + 4);
            var codeSize = boot.ReadUInt32(offset + 8);
            var relocAddress = boot.ReadUInt32(offset + 0x0C);
            var relocSize = boot.ReadUInt32(offset + 0x10);
            if (codeAddress >> 28 != CartDomainNibble || relocAddress >> 28 != CartDomainNibble)
                break;
            if (!IsPlausibleSize(codeSize) || !IsPlausibleSize(relocSize))
                break;

            var name = boot.TryReadCString(boot.ReadUInt32(offset));
            if (name == null)
                break;

            entries.Add(new Entry(
                name, codeAddress, (int)codeSize, relocAddress, (int)relocSize));
        }

        return entries;
    }

    private static bool IsPlausibleSize(uint size)
    {
        return size is > 0 and <= 1 << 22;
    }

    /// <summary>
    ///     Slices the region into per-overlay payloads, or an empty list when
    ///     the roster cannot be proven to describe it. The caller then emits the
    ///     region whole rather than mis-slicing it.
    ///     <para>
    ///         <paramref name="regionStart" /> is where master-directory
    ///         coverage ends, and <paramref name="regionEnd" /> is one past the
    ///         last byte that is not cart padding. The fixup is
    ///         <c>min(payload address) − regionStart</c>; it is accepted only if
    ///         the payloads then TILE the region with no gap or overlap and
    ///         finish at its end, which is a far stronger statement than any one
    ///         address looking right.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<Payload> Slice(
        IReadOnlyList<Entry> entries, int regionStart, int regionEnd, int romLength)
    {
        if (entries.Count == 0 || regionEnd <= regionStart)
            return [];

        var payloads = new List<(string Name, string Extension, uint Address, int Length)>(
            entries.Count * 2);
        foreach (var entry in entries)
        {
            payloads.Add((entry.Name, ".bin", entry.CodeAddress, entry.CodeSize));
            payloads.Add((entry.Name, ".rel", entry.RelocAddress, entry.RelocSize));
        }

        payloads.Sort(static (a, b) => a.Address.CompareTo(b.Address));

        var fixup = (long)payloads[0].Address - regionStart;
        var sliced = new List<Payload>(payloads.Count);
        var cursor = (long)payloads[0].Address;
        foreach (var (name, extension, address, length) in payloads)
        {
            // Contiguity is the proof. A roster that merely fits somewhere in
            // the region says nothing; one that accounts for every byte of it
            // in order cannot be a coincidence.
            if (address != cursor)
                return [];

            var offset = address - fixup;
            if (offset < 0 || offset + length > romLength)
                return [];

            sliced.Add(new Payload(name, extension, (int)offset, length));
            cursor = address + length;
        }

        // The roster has to account for essentially the whole region; a set of
        // payloads that merely fits inside it proves nothing. Measured, the
        // last payload stops 2 to 10 bytes short of the last stored byte — a
        // small footer the roster does not claim — so a bounded shortfall is
        // allowed and the caller emits that remainder rather than dropping it.
        var end = cursor - fixup;
        if (end > romLength || end > regionEnd || regionEnd - end > MaxTrailerBytes)
            return [];

        return sliced;
    }

    /// <summary>
    ///     How much stored data may sit past the last roster payload. The
    ///     measured footers are 8 bytes (Spider-Man), 2 (THPS2) and 10 (THPS3);
    ///     the bound is deliberately tight so a mis-derived fixup cannot pass by
    ///     leaving most of the region unclaimed.
    /// </summary>
    internal const int MaxTrailerBytes = 64;

    /// <summary>
    ///     One past the last byte of real data at or after
    ///     <paramref name="regionStart" />. Cart padding is 0x00 or 0xFF, and
    ///     both appear as fill in these images.
    /// </summary>
    internal static int FindRegionEnd(ReadOnlySpan<byte> rom, int regionStart)
    {
        for (var i = rom.Length - 1; i >= regionStart; i--)
        {
            if (rom[i] != 0x00 && rom[i] != 0xFF)
                return i + 1;
        }

        return regionStart;
    }

    /// <summary>
    ///     One past the last byte any master-directory group or the boot table
    ///     covers. The overlay region begins exactly here in all three ROMs
    ///     that have one.
    /// </summary>
    internal static int FindDirectoryEnd(
        N64RomArchive.SubFileTable bootTable, IReadOnlyList<N64RomArchive.StreamGroup> groups)
    {
        var end = 0;
        foreach (var (offset, length) in bootTable.Blocks)
            end = Math.Max(end, offset + length);
        foreach (var group in groups)
        {
            foreach (var (offset, length) in group.Leaves)
                end = Math.Max(end, offset + length);
        }

        return end;
    }

    internal static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }
}
