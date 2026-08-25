using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Texture.Gba;

/// <summary>
///     Extracts THPS2 GBA's sprite/UI art families whose decode is fully proven and
///     <b>self-validating</b> (each table is located by content, never a hardcoded
///     address; every rule below was validated byte-for-byte against live OBJ
///     VRAM/OAM/palette captures):
///     <list type="bullet">
///         <item><b>123 skateboard decks</b> — a stride-8 table of
///         <c>{palettePtr, artPtr}</c> where the art LZ77-decodes to exactly 2048
///         bytes (64 4bpp tiles = one 32×128 deck, two stacked 32×64 halves) and the
///         palette is the 32 raw bytes at <c>align4(art + compressedLength)</c> —
///         the stream's own aligned end. That equality is the validator; pairing a
///         deck with the <i>next</i> record's adjacent palette looks plausible
///         (adjacent decks share brand colours) and is exactly the off-by-one a live
///         capture caught.</item>
///         <item><b>15 skater portraits</b> — a stride-0x4C character table whose
///         records point at a 2496-byte colour stream (the first 256 bytes are the
///         character's 128 OBJ palette entries; the rest are the 3D renderer's
///         shading ramps) and a 1024-byte portrait (32×32 8bpp, indices &lt; 100
///         into the shared select palette).</item>
///         <item><b>Venue photos</b> — each level record carries two 4096-byte
///         streams at <c>+0x44/+0x48</c> (64×64 8bpp sepia photographs) indexing
///         the full select palette.</item>
///     </list>
///     The shared select palette is itself found by content: the full 512-byte
///     select palette's first 200 bytes equal the separate 200-byte select-screen
///     palette stream — a pair no unrelated streams reproduce.
///
///     <para>The skater itself has <b>no sprite frames to extract</b>: it is
///     software-rendered 3D at runtime (its 64×64 8bpp OBJ matches no ROM bytes and
///     rotates smoothly frame-to-frame); the colour streams here are that renderer's
///     palette + shading data. Fonts/HUD glyphs/badges are decoded in the research
///     record but not extracted here — their tables are address-pinned rather than
///     self-validating, and some palette banks remain unproven.</para>
/// </summary>
public static class GbaSpriteArt
{
    private const uint RomBase = 0x08000000;

    public readonly record struct GbaDeck(int Index, uint ArtAddress, byte[] Rgba)
    {
        public const int Width = 32;
        public const int Height = 128;
    }

    public readonly record struct GbaPortrait(int Index, byte[] Rgba)
    {
        public const int Size = 32;
    }

    public readonly record struct GbaVenuePhoto(int LevelIndex, int Slot, uint Address, byte[] Rgba)
    {
        public const int Size = 64;
    }

    /// <summary>All decks, each with its own 16-colour palette (entry 0 transparent).</summary>
    public static List<GbaDeck> ExtractDecks(ReadOnlySpan<byte> rom)
    {
        var (tableOffset, count) = FindDeckTable(rom);
        var decks = new List<GbaDeck>(count);
        for (var i = 0; i < count; i++)
        {
            var paletteAddress = ReadU32(rom, tableOffset + i * 8);
            var artAddress = ReadU32(rom, tableOffset + i * 8 + 4);
            if (!GbaBiosLz77.TryDecompress(rom, (int)(artAddress - RomBase), out var art, out _))
                continue;

            var palette = ReadRawPalette16(rom, (int)(paletteAddress - RomBase));
            var rgba = new byte[GbaDeck.Width * GbaDeck.Height * 4];
            for (var y = 0; y < GbaDeck.Height; y++)
            for (var x = 0; x < GbaDeck.Width; x++)
            {
                // Two stacked 32×64 halves of 32 tiles each, 1D order, 4 tiles/row.
                var half = y / 64;
                var tile = half * 32 + (y % 64) / 8 * 4 + x / 8;
                var b = art[tile * 32 + (y % 8) * 4 + (x % 8) / 2];
                var index = (x & 1) == 0 ? b & 0xF : b >> 4;
                WriteRgba(rgba, (y * GbaDeck.Width + x) * 4, palette, index);
            }

            decks.Add(new GbaDeck(i, artAddress, rgba));
        }

        return decks;
    }

    /// <summary>The 15 roster portraits (Tony Hawk … Spider-Man, secret skater).</summary>
    public static List<GbaPortrait> ExtractPortraits(ReadOnlySpan<byte> rom)
    {
        var portraits = new List<GbaPortrait>();
        var palette = FindSelectPalette(rom);
        if (palette == null)
            return portraits;

        var (tableOffset, count) = FindCharacterTable(rom);
        for (var i = 0; i < count; i++)
        {
            var portraitAddress = ReadU32(rom, tableOffset + i * 0x4C + 4);
            if (!GbaBiosLz77.TryDecompress(rom, (int)(portraitAddress - RomBase), out var art, out _)
                || art.Length != 1024)
                continue;

            var rgba = new byte[GbaPortrait.Size * GbaPortrait.Size * 4];
            for (var y = 0; y < GbaPortrait.Size; y++)
            for (var x = 0; x < GbaPortrait.Size; x++)
            {
                // 16 8bpp tiles, 1D order, 4 tiles/row.
                var tile = y / 8 * 4 + x / 8;
                var index = art[tile * 64 + (y % 8) * 8 + (x % 8)];
                WriteRgba(rgba, (y * GbaPortrait.Size + x) * 4, palette, index);
            }

            portraits.Add(new GbaPortrait(i, rgba));
        }

        return portraits;
    }

    /// <summary>
    ///     The level-select venue photographs (two per level record; shared streams
    ///     are deduplicated by address, keeping the first level that references them).
    /// </summary>
    public static List<GbaVenuePhoto> ExtractVenuePhotos(ReadOnlySpan<byte> rom)
    {
        var photos = new List<GbaVenuePhoto>();
        var palette = FindSelectPalette(rom);
        if (palette == null)
            return photos;

        var seen = new HashSet<uint>();
        var levels = GbaLevelImages.FindLevels(rom);
        for (var levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            var trueRecord = (int)(levels[levelIndex].RecordAddress - RomBase) - 0x144;
            for (var slot = 0; slot < 2; slot++)
            {
                var address = ReadU32(rom, trueRecord + 0x44 + slot * 4);
                if (address < RomBase || address >= RomBase + (uint)rom.Length || !seen.Add(address))
                    continue;
                if (!GbaBiosLz77.TryDecompress(rom, (int)(address - RomBase), out var art, out _)
                    || art.Length != 4096)
                    continue;

                var rgba = new byte[GbaVenuePhoto.Size * GbaVenuePhoto.Size * 4];
                for (var y = 0; y < GbaVenuePhoto.Size; y++)
                for (var x = 0; x < GbaVenuePhoto.Size; x++)
                {
                    // 64 8bpp tiles, 1D order, 8 tiles/row.
                    var tile = y / 8 * 8 + x / 8;
                    var index = art[tile * 64 + (y % 8) * 8 + (x % 8)];
                    WriteRgba(rgba, (y * GbaVenuePhoto.Size + x) * 4, palette, index);
                }

                photos.Add(new GbaVenuePhoto(levelIndex, slot, address, rgba));
            }
        }

        return photos;
    }

    // The deck table: the longest stride-8 run of {palettePtr, artPtr} records whose
    // art strictly LZ77-decodes to 2048 bytes with palettePtr at its aligned end.
    private static (int Offset, int Count) FindDeckTable(ReadOnlySpan<byte> rom)
    {
        var bestOffset = 0;
        var bestCount = 0;
        for (var offset = 0; offset + 8 <= rom.Length; offset += 4)
        {
            if (!IsDeckRecord(rom, offset))
                continue;
            // Only measure maximal runs once, from their true start.
            if (offset >= 8 && IsDeckRecord(rom, offset - 8))
                continue;

            var count = 1;
            while (IsDeckRecord(rom, offset + count * 8))
                count++;
            if (count > bestCount)
            {
                bestCount = count;
                bestOffset = offset;
            }
        }

        return (bestOffset, bestCount);
    }

    private static bool IsDeckRecord(ReadOnlySpan<byte> rom, int offset)
    {
        if (offset + 8 > rom.Length)
            return false;
        var paletteAddress = ReadU32(rom, offset);
        var artAddress = ReadU32(rom, offset + 4);
        if (paletteAddress < RomBase || paletteAddress + 32 > RomBase + (uint)rom.Length)
            return false;
        if (artAddress < RomBase || artAddress >= RomBase + (uint)rom.Length)
            return false;
        var artOffset = (int)(artAddress - RomBase);
        if (!GbaBiosLz77.TryDecompress(rom, artOffset, out var art, out var compressedLength))
            return false;
        if (art.Length != 2048)
            return false;
        // The palette is the raw 32 bytes at the art stream's own aligned end.
        return paletteAddress == RomBase + (uint)((artOffset + compressedLength + 3) & ~3);
    }

    // The character table: the longest stride-0x4C run whose records point at a
    // 2496-byte colour stream and a 1024-byte portrait stream.
    private static (int Offset, int Count) FindCharacterTable(ReadOnlySpan<byte> rom)
    {
        var bestOffset = 0;
        var bestCount = 0;
        for (var offset = 0; offset + 8 <= rom.Length; offset += 4)
        {
            if (!IsCharacterRecord(rom, offset))
                continue;
            if (offset >= 0x4C && IsCharacterRecord(rom, offset - 0x4C))
                continue;

            var count = 1;
            while (IsCharacterRecord(rom, offset + count * 0x4C))
                count++;
            if (count > bestCount)
            {
                bestCount = count;
                bestOffset = offset;
            }
        }

        return (bestOffset, bestCount);
    }

    private static bool IsCharacterRecord(ReadOnlySpan<byte> rom, int offset)
    {
        if (offset + 8 > rom.Length)
            return false;
        var colourAddress = ReadU32(rom, offset);
        var portraitAddress = ReadU32(rom, offset + 4);
        if (colourAddress < RomBase || colourAddress >= RomBase + (uint)rom.Length)
            return false;
        if (portraitAddress < RomBase || portraitAddress >= RomBase + (uint)rom.Length)
            return false;
        return GbaBiosLz77.TryDecompress(rom, (int)(colourAddress - RomBase), out var colour, out _)
               && colour.Length == 2496
               && GbaBiosLz77.TryDecompress(rom, (int)(portraitAddress - RomBase), out var portrait, out _)
               && portrait.Length == 1024;
    }

    /// <summary>
    ///     The full 256-colour select palette, found by content: the first 512-byte
    ///     LZ77 stream whose leading 200 bytes equal some 200-byte stream's payload
    ///     (the select screens compose entries 0–99 from the short palette and the
    ///     level-select screen uses the full one; their shared prefix is the anchor).
    /// </summary>
    internal static byte[]? FindSelectPalette(ReadOnlySpan<byte> rom)
    {
        var shortPalettes = new List<byte[]>();
        var fullCandidates = new List<byte[]>();
        var i = 0;
        while (i + 4 <= rom.Length)
        {
            if (rom[i] == 0x10 && GbaBiosLz77.TryDecompress(rom, i, out var payload, out var compressedLength))
            {
                if (payload.Length == 200)
                    shortPalettes.Add(payload);
                else if (payload.Length == 512)
                    fullCandidates.Add(payload);
                i += (compressedLength + 3) & ~3;
                continue;
            }

            i += 4;
        }

        foreach (var full in fullCandidates)
        foreach (var half in shortPalettes)
        {
            if (full.AsSpan(0, 200).SequenceEqual(half))
                return ToRgbaPalette(full);
        }

        return null;
    }

    private static byte[] ToRgbaPalette(ReadOnlySpan<byte> bgr555)
    {
        var palette = new byte[bgr555.Length / 2 * 4];
        for (var i = 0; i < bgr555.Length / 2; i++)
        {
            var c = bgr555[i * 2] | (bgr555[i * 2 + 1] << 8);
            palette[i * 4] = Expand5(c & 0x1F);
            palette[i * 4 + 1] = Expand5((c >> 5) & 0x1F);
            palette[i * 4 + 2] = Expand5((c >> 10) & 0x1F);
            palette[i * 4 + 3] = 0xFF;
        }

        return palette;
    }

    private static byte[] ReadRawPalette16(ReadOnlySpan<byte> rom, int offset) =>
        ToRgbaPalette(rom.Slice(offset, 32));

    // Palette/OBJ index 0 is transparent for all sprite art.
    private static void WriteRgba(byte[] rgba, int at, byte[] palette, int index)
    {
        if (index == 0 || index * 4 + 3 >= palette.Length)
            return; // transparent (rgba is zero-initialised)
        rgba[at] = palette[index * 4];
        rgba[at + 1] = palette[index * 4 + 1];
        rgba[at + 2] = palette[index * 4 + 2];
        rgba[at + 3] = 0xFF;
    }

    private static uint ReadU32(ReadOnlySpan<byte> rom, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
