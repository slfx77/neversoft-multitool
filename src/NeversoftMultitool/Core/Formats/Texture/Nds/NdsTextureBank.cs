using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NeversoftMultitool.Core.Formats.Texture.Nds;

/// <summary>Nintendo DS GX texture formats, as encoded in TEXIMAGE_PARAM bits 26-28.</summary>
public enum NdsTextureFormat
{
    None = 0,
    A3I5 = 1,
    Palette4 = 2,
    Palette16 = 3,
    Palette256 = 4,
    Compressed4X4 = 5,
    A5I3 = 6,
    Direct16 = 7
}

/// <summary>One texture in a bank: its pixel-file id, its GX parameters, and its palette.</summary>
public sealed record NdsTextureEntry(
    uint PixelId,
    int PixelBytes,
    uint TexImageParam,
    NdsTextureFormat Format,
    int Width,
    int Height,
    bool Color0Transparent,
    ushort[] Palette)
{
    /// <summary>The name the loader composes for this texture's pixel file.</summary>
    public string PixelFileName => $".\\{PixelId:x8}.texture.bin";
}

/// <summary>
///     Vicarious Visions DS texture bank — how the Tony Hawk DS carts store their
///     art. A bank holds the METADATA and PALETTES for a set of textures; each
///     texture's texels live in a separate GOB file the loader names
///     <c>.\%08x.texture.bin</c> from the record's id.
///
///     Layout, transcribed from the ARM9 fix-up routine (Sk8land
///     <c>FUN_020bf6b0</c> @ <c>0x020BF6B0</c>), which walks the records and
///     patches the runtime pointers:
///     <code>
///     u16 textureCount            // the loop bound
///     u16 paletteCount            // only used to locate the palette DATA
///     u32 reserved
///     textureCount x 28 bytes:
///         +0  u32 pixelId         // -> ".\%08x.texture.bin"
///         +4  u32 pixelBytes
///         +8  u32 texImageParam   // fmt 26-28, sizeS 20-22, sizeT 23-25, colour0 29
///         +12 u32, +16 u32, +20 u32
///         +24 u32 palettePtr      // patched to &amp;paletteRecord[i]
///     paletteCount x 16 bytes:
///         +0  u32 format
///         +4  u32 dataOffset      // into the palette data section
///         +8  u32
///         +12 u32 dataPtr         // patched to paletteData + dataOffset
///     palette data                // BGR555
///     </code>
///
///     The fix-up binds texture <c>i</c> to palette record <c>i</c> — the two
///     counts are equal in every shipped bank, and the second is what places the
///     data section at <c>8 + textureCount*28 + paletteCount*16</c>.
///
///     Detection is structural rather than a magic (there isn't one): every record
///     must satisfy <c>width * height * bpp / 8 == pixelBytes</c> computed from its
///     own TEXIMAGE_PARAM bits. Across the three carts that admits 217 banks and
///     4,593 textures with the identity holding on every record, and every pixel id
///     resolving to a real GOB file of exactly the declared length.
/// </summary>
public static class NdsTextureBank
{
    private const int HeaderSize = 8;
    private const int TextureRecordSize = 28;
    private const int PaletteRecordSize = 16;
    private const int MaxCount = 4000;

    /// <summary>Bits per texel for a format, or 0 when the format carries no texels.</summary>
    public static int BitsPerTexel(NdsTextureFormat format)
    {
        return format switch
        {
            NdsTextureFormat.A3I5 => 8,
            NdsTextureFormat.Palette4 => 2,
            NdsTextureFormat.Palette16 => 4,
            NdsTextureFormat.Palette256 => 8,
            NdsTextureFormat.Compressed4X4 => 2,
            NdsTextureFormat.A5I3 => 8,
            NdsTextureFormat.Direct16 => 16,
            _ => 0
        };
    }

    public static bool IsTextureBank(ReadOnlySpan<byte> data)
    {
        return TryParse(data, out var entries) && entries!.Count > 0;
    }

    /// <summary>
    ///     Parses a bank and confirms it against the container it came from: every
    ///     record's texel blob must exist AND be exactly the declared length.
    ///
    ///     The size identity alone is a strong filter but not a perfect one — on
    ///     Sk8land it admits three files whose records reference a blob of the wrong
    ///     length — so the container check is what separates a real bank from a
    ///     coincidence. <paramref name="pixelLength" /> returns the length of the
    ///     file named <c>.\%08x.texture.bin</c> for an id, or null if there is none.
    /// </summary>
    public static bool TryParseValidated(
        ReadOnlySpan<byte> data,
        Func<uint, long?> pixelLength,
        [NotNullWhen(true)] out IReadOnlyList<NdsTextureEntry>? entries)
    {
        if (!TryParse(data, out entries) || entries!.Count == 0)
        {
            entries = null;
            return false;
        }

        foreach (var entry in entries)
        {
            if (pixelLength(entry.PixelId) != entry.PixelBytes)
            {
                entries = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses a bank, or returns false when the data is not one.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data, [NotNullWhen(true)] out IReadOnlyList<NdsTextureEntry>? entries)
    {
        entries = null;
        if (data.Length < HeaderSize + TextureRecordSize)
            return false;

        var textureCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (textureCount is 0 or > MaxCount || paletteCount > MaxCount)
            return false;

        var paletteBase = HeaderSize + textureCount * TextureRecordSize;
        var dataBase = paletteBase + paletteCount * PaletteRecordSize;
        if (dataBase > data.Length)
            return false;

        var result = new List<NdsTextureEntry>(textureCount);
        for (var i = 0; i < textureCount; i++)
        {
            var record = data.Slice(HeaderSize + i * TextureRecordSize, TextureRecordSize);
            var pixelId = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var pixelBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var param = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);

            var format = (NdsTextureFormat)((param >> 26) & 7);
            var bpp = BitsPerTexel(format);
            if (bpp == 0)
                return false;

            var width = 8 << (int)((param >> 20) & 7);
            var height = 8 << (int)((param >> 23) & 7);

            // The identity that stands in for a magic number.
            if ((long)width * height * bpp / 8 != pixelBytes)
                return false;

            var palette = ReadPalette(data, paletteBase, dataBase, paletteCount, i);
            result.Add(new NdsTextureEntry(
                pixelId, checked((int)pixelBytes), param, format, width, height,
                ((param >> 29) & 1) != 0, palette));
        }

        entries = result;
        return true;
    }

    /// <summary>
    ///     Palette for texture <paramref name="index" />.
    ///
    ///     Two details here are easy to get wrong and both were, first time round.
    ///     The record's offset is counted in <b>u16 ENTRIES, not bytes</b> — the
    ///     ARM9 fix-up adds it to a <c>ushort*</c>, so it scales by two — and the
    ///     palette is not simply "everything up to the next offset": each one is a
    ///     self-describing <c>{u32 entryCount, u16 entries[entryCount]}</c> blob,
    ///     padded to four bytes. Reading it as a byte offset spanning to the next
    ///     record produced a plausible but wrong palette for every texture.
    /// </summary>
    private static ushort[] ReadPalette(
        ReadOnlySpan<byte> data, int paletteBase, int dataBase, int paletteCount, int index)
    {
        if (index >= paletteCount)
            return [];

        var entryOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            data[(paletteBase + index * PaletteRecordSize + 4)..]);
        var at = dataBase + (long)entryOffset * 2;
        if (at < dataBase || at + 4 > data.Length)
            return [];

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)at..]);
        if (count > 256 || at + 4 + (long)count * 2 > data.Length)
            return [];

        var palette = new ushort[count];
        for (var i = 0; i < count; i++)
            palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(at + 4 + i * 2)..]);
        return palette;
    }
}
