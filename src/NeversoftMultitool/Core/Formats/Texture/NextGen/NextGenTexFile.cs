using System.Buffers.Binary;
using System.IO.Compression;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Neversoft's next-generation texture dictionary — magic <c>FA CE CA A7</c>,
///     big-endian — as shipped by THAW, Project 8 and Proving Ground on Xbox 360
///     (<c>.tex.xen</c>, <c>.stex.xen</c>) and PS3 (<c>.tex.ps3</c>,
///     <c>.stex.ps3</c>).
/// </summary>
/// <remarks>
///     Layout (derived 2026-08-26/27 and validated over the whole corpus —
///     <b>12,335/12,335 files and 90,477 texture records parse with zero
///     structural failures</b>):
///     <list type="bullet">
///         <item>
///             Header: magic, <c>u8 platform</c> (1 = Xenon, 2 = PS3),
///             <c>u8 headerSize</c> (0x1C / 0x24), <c>u16 textureCount</c>,
///             <c>u32 recordTableOffset</c>, <c>u32 dataStart</c>, <c>0xFFFFFFFF</c>,
///             a spare word, then the header size echoed. The PS3 header adds the
///             file's own length, which is checked.
///         </item>
///         <item>
///             <c>0xEF</c> filler runs from the header to the record table.
///         </item>
///         <item>
///             Each record opens with <c>[wordCount][byteSize][flags][format]</c>,
///             so the table states its own stride — 32 bytes (THAW Xenon), 40
///             (P8/PG Xenon) or 48 (PS3) — and every record in a table repeats it.
///             Then come <c>count</c> auxiliary descriptors of 40/52/24 bytes,
///             ending exactly at <c>dataStart</c>.
///         </item>
///         <item>
///             Xenon keeps pixels in the same file; PS3 keeps them in a VRAM twin
///             (<c>.tvx.ps3</c>), so a PS3 dictionary alone carries no pixels.
///         </item>
///     </list>
///     1,634 of the Xenon files are additionally wrapped in raw DEFLATE, decoded
///     here decode-then-validate so a plain file is never disturbed.
/// </remarks>
public static class NextGenTexFile
{
    private const uint Magic = 0xFACECAA7;
    private const int XenonHeaderSize = 0x1C;
    private const int Ps3HeaderSize = 0x24;

    // Xenos GPUTEXTUREFORMAT values, from the aux record's fetch constant.
    private const int XenonFormatArgb8888 = 6;
    private const int XenonFormatDxt1 = 18;
    private const int XenonFormatDxt5 = 20;

    // PS3 GCM texture formats, from the aux record's first byte.
    private const int GcmFormatArgb8888 = 0x85;
    private const int GcmFormatDxt1 = 0x86;
    private const int GcmFormatDxt5 = 0x88;

    /// <summary>True when the bytes are a next-gen dictionary, wrapped or not.</summary>
    public static bool IsNextGenTex(byte[] data)
    {
        return TryUnwrap(data, out _);
    }

    /// <summary>
    ///     Parses a dictionary. <paramref name="vramPayload" /> supplies the PS3
    ///     twin's bytes; without it a PS3 dictionary parses to metadata-only
    ///     textures (no pixels), which is what it physically contains.
    /// </summary>
    public static Ps2TexResult Parse(byte[] data, byte[]? vramPayload = null)
    {
        try
        {
            if (!TryUnwrap(data, out var body))
                return Ps2TexResult.Fail("Not a FACECAA7 texture dictionary");

            if (!TryReadHeader(body, out var header, out var error))
                return Ps2TexResult.Fail(error);

            if (header.Count == 0)
                return new Ps2TexResult([]);

            var textures = new List<Ps2Texture>(header.Count);
            for (var i = 0; i < header.Count; i++)
                textures.Add(ReadTexture(body, header, i, vramPayload));

            return new Ps2TexResult(textures);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     The VRAM-twin file name a PS3 dictionary's pixels live in:
    ///     <c>cutscene.tex.ps3</c> → <c>cutscene.tvx.ps3</c>.
    /// </summary>
    public static string GetVramTwinFileName(string dictionaryFileName)
    {
        var name = Path.GetFileName(dictionaryFileName);
        foreach (var (from, to) in new[] { (".tex.", ".tvx."), (".stex.", ".vstex.") })
        {
            var index = name.LastIndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return name[..index] + to + name[(index + from.Length)..];
        }

        return name;
    }

    /// <summary>
    ///     The sibling directory a pak's VRAM twin lives in: an extracted
    ///     <c>FOO.PAK</c> pairs with <c>FOO_VRAM.PAK</c>. The suffix goes BEFORE
    ///     the extension — appending it instead (<c>FOO.PAK_vram.pak</c>) silently
    ///     resolves to a same-directory copy that is not the payload, which cost
    ///     49 of 49 pak-contained textures before it was caught.
    /// </summary>
    public static string GetVramTwinDirectoryName(string directoryName)
    {
        var name = Path.GetFileName(directoryName.TrimEnd(Path.DirectorySeparatorChar));
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name + "_VRAM" : name[..dot] + "_VRAM" + name[dot..];
    }

    /// <summary>Total payload bytes a dictionary's records reference.</summary>
    public static long GetRequiredPayloadLength(byte[] data)
    {
        if (!TryUnwrap(data, out var body) || !TryReadHeader(body, out var header, out _))
            return 0;

        long needed = 0;
        for (var i = 0; i < header.Count; i++)
        {
            var record = ReadRecord(body, header, i);
            needed = Math.Max(needed, (long)record.PayloadOffset + record.PayloadLength);
        }

        return needed;
    }

    private static Ps2Texture ReadTexture(byte[] body, Header header, int index, byte[]? vramPayload)
    {
        var record = ReadRecord(body, header, index);
        var pixels = DecodePixels(body, header, record, vramPayload);
        return new Ps2Texture(record.Checksum, record.Width, record.Height,
            (uint)record.Format, 0, pixels);
    }

    private static byte[]? DecodePixels(byte[] body, Header header, Record record, byte[]? vramPayload)
    {
        if (record.Width <= 0 || record.Height <= 0) return null;

        var isXenon = header.Platform == 1;
        var blockBytes = GetBlockBytes(record, isXenon);
        if (blockBytes == 0) return null; // Format we do not claim (e.g. DXN).

        ReadOnlySpan<byte> source;
        if (isXenon)
        {
            // The Xenon payload word is an offset from the START OF THE FILE, not
            // from dataStart (measured: adding dataStart puts every surface past
            // its real position and nothing decodes).
            var start = record.PayloadOffset
                        + XenosTiling.GetSurfaceByteOffset(record.Width, record.Height, blockBytes);
            if (start < 0 || start >= body.Length) return null;
            source = body.AsSpan(start);
        }
        else
        {
            if (vramPayload == null) return null;
            if (record.PayloadOffset >= vramPayload.Length) return null;
            var length = Math.Min(record.PayloadLength, vramPayload.Length - record.PayloadOffset);
            source = vramPayload.AsSpan(record.PayloadOffset, length);
        }

        // ARGB8888 tiles per TEXEL; the DXT formats tile per 4x4 block.
        var uncompressed = blockBytes == 4;
        var unitsX = uncompressed ? record.Width : Math.Max(1, (record.Width + 3) / 4);
        var unitsY = uncompressed ? record.Height : Math.Max(1, (record.Height + 3) / 4);

        // The fetch constant's endian field selects the swap width: 1 = 16-bit
        // (the DXT formats), 2 = 32-bit (whole ARGB texels).
        var swapWidth = record.EndianMode switch { 1 => 2, 2 => 4, _ => 0 };

        var units = isXenon
            ? XenosTiling.UntileUnits(source, unitsX, unitsY, blockBytes, swapWidth)
            : source.ToArray();

        var required = unitsX * unitsY * blockBytes;
        if (units.Length < required) return null;

        var rgba = blockBytes switch
        {
            4 => ArgbToRgba(units, record.Width, record.Height),
            8 => DxtDecoder.DecodeDxt1(units, record.Width, record.Height),
            _ => DxtDecoder.DecodeDxt5(units, record.Width, record.Height)
        };

        return FlipRows(rgba, record.Width, record.Height);
    }

    /// <summary>Stored A,R,G,B per texel; the exporter wants R,G,B,A.</summary>
    private static byte[] ArgbToRgba(byte[] argb, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i + 3 < Math.Min(argb.Length, rgba.Length); i += 4)
        {
            rgba[i] = argb[i + 1];
            rgba[i + 1] = argb[i + 2];
            rgba[i + 2] = argb[i + 3];
            rgba[i + 3] = argb[i];
        }

        return rgba;
    }

    /// <summary>
    ///     Surfaces are stored BOTTOM-UP, the same convention this studio's
    ///     GameCube and DS art uses.
    /// </summary>
    /// <remarks>
    ///     Not detectable by the cross-platform pixel comparison that validates
    ///     everything else here: both platforms share this orientation and the
    ///     same decode path, so a flip cancels out and 371/371 textures still
    ///     matched while every one of them was upside-down. It took legible art —
    ///     a "KEEP OUT / NO TRESPASSING" sign that only reads correctly after a
    ///     vertical flip — to catch it.
    /// </remarks>
    private static byte[] FlipRows(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        var flipped = new byte[rgba.Length];
        for (var y = 0; y < height; y++)
        {
            var source = y * stride;
            var destination = (height - 1 - y) * stride;
            if (source + stride > rgba.Length || destination + stride > flipped.Length) break;
            Array.Copy(rgba, source, flipped, destination, stride);
        }

        return flipped;
    }

    private static int GetBlockBytes(Record record, bool isXenon)
    {
        if (isXenon)
        {
            return record.Format switch
            {
                XenonFormatArgb8888 => 4,
                XenonFormatDxt1 => 8,
                XenonFormatDxt5 => 16,
                _ => 0
            };
        }

        return record.Format switch
        {
            GcmFormatArgb8888 => 4,
            GcmFormatDxt1 => 8,
            GcmFormatDxt5 => 16,
            _ => 0
        };
    }

    private static bool TryUnwrap(byte[] data, out byte[] body)
    {
        body = data;
        if (data.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic)
            return true;

        // 1,634 Xenon dictionaries ship raw-DEFLATE wrapped. Decode then validate,
        // so a plain (or unrelated) file is never mangled.
        try
        {
            using var input = new MemoryStream(data, false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            var decoded = output.ToArray();
            if (decoded.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(decoded) == Magic)
            {
                body = decoded;
                return true;
            }
        }
        catch (InvalidDataException)
        {
            // Not deflate-wrapped.
        }

        return false;
    }

    private static bool TryReadHeader(byte[] body, out Header header, out string error)
    {
        header = default;
        error = "";

        if (body.Length < XenonHeaderSize)
        {
            error = "Texture dictionary header is truncated";
            return false;
        }

        var platform = body[4];
        var headerSize = body[5];
        if (headerSize != XenonHeaderSize && headerSize != Ps3HeaderSize)
        {
            error = $"Unknown header size 0x{headerSize:X2}";
            return false;
        }

        if (body.Length < headerSize)
        {
            error = "Texture dictionary header is truncated";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6));
        var tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8));
        var dataStart = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x0C));
        if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x18)) != headerSize)
        {
            error = "Header size is not echoed at +0x18";
            return false;
        }

        // The 0xEF filler between the header and the table is part of the format.
        // The counter must be an int: headerSize is a byte, so `var i = headerSize`
        // gives a byte counter that wraps at 255 and re-tests offset 0 — which any
        // file with a record table past 255 bytes hits.
        for (int i = headerSize; i < Math.Min(tableOffset, body.Length); i++)
        {
            if (body[i] == 0xEF) continue;
            error = $"Expected 0xEF filler at {i}";
            return false;
        }

        var recordSize = 0;
        var auxStride = 0;
        if (count > 0)
        {
            if (tableOffset + 2 > body.Length)
            {
                error = "Record table starts past the end of the file";
                return false;
            }

            var wordCount = body[tableOffset];
            recordSize = body[tableOffset + 1];
            if (recordSize != wordCount * 4 || recordSize is not (32 or 40 or 48))
            {
                error = $"Implausible record stride {recordSize}";
                return false;
            }

            var auxOffset = tableOffset + count * recordSize;
            if (auxOffset > body.Length || dataStart < auxOffset || (dataStart - auxOffset) % count != 0)
            {
                error = "Auxiliary descriptor span does not divide evenly";
                return false;
            }

            auxStride = (dataStart - auxOffset) / count;
        }

        header = new Header(platform, count, tableOffset, dataStart, recordSize, auxStride);
        return true;
    }

    private static Record ReadRecord(byte[] body, Header header, int index)
    {
        var offset = header.TableOffset + index * header.RecordSize;
        var format = body[offset + 3];
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset + 4));
        var width = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset + 8));
        var height = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset + 10));

        // Trailing words start after the dimension block and the format word.
        var trailStart = offset + (header.RecordSize == 32 ? 20 : 24);
        var trailCount = (offset + header.RecordSize - trailStart) / 4;
        var trail = new int[trailCount];
        for (var i = 0; i < trailCount; i++)
            trail[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(trailStart + i * 4));

        // Xenon keeps the payload offset in the LAST trailing word; PS3 keeps
        // offset and length in the first two.
        var isXenon = header.Platform == 1;
        var payloadOffset = isXenon ? trail[^1] : trail[1];
        var payloadLength = isXenon ? 0 : trail[2];

        var endianMode = 0;
        var resolvedFormat = format;
        var auxOffset = header.TableOffset + header.Count * header.RecordSize
                                          + index * header.AuxStride;
        if (isXenon && header.AuxStride >= 24 && auxOffset + header.AuxStride <= body.Length)
        {
            // The tail of the Xenon aux record is the GPU fetch constant: its
            // second word carries the texture format and the endian selector.
            var fetch = BinaryPrimitives.ReadUInt32BigEndian(
                body.AsSpan(auxOffset + header.AuxStride - 24 + 4));
            resolvedFormat = (byte)(fetch & 0x3F);
            endianMode = (int)((fetch >> 6) & 3);
        }
        else if (!isXenon && auxOffset < body.Length)
        {
            resolvedFormat = (byte)(body[auxOffset] & 0x9F);
        }

        return new Record(checksum, width, height, resolvedFormat, payloadOffset, payloadLength,
            endianMode);
    }

    private readonly record struct Header(
        byte Platform, int Count, int TableOffset, int DataStart, int RecordSize, int AuxStride);

    private readonly record struct Record(
        uint Checksum, int Width, int Height, byte Format,
        int PayloadOffset, int PayloadLength, int EndianMode);
}
