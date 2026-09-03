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
    private const int XenonFormatDxn = 49;

    // PS3 GCM texture formats, from the aux record's first byte.
    private const int GcmFormatArgb8888 = 0x85;
    private const int GcmFormatDxt1 = 0x86;
    private const int GcmFormatDxt5 = 0x88;

    /// <summary>
    ///     True when the bytes are a fully validated next-gen dictionary, wrapped
    ///     or not. Xenon files include their pixels, so their complete allocation
    ///     layout is validated; PS3 files can only validate metadata without the
    ///     external VRAM twin.
    /// </summary>
    public static bool IsNextGenTex(byte[] data)
    {
        return TryProbe(data, out _, out _);
    }

    /// <summary>
    ///     Validates the complete dictionary structure and reports which platform
    ///     owns the payload. Unlike a four-byte magic check, this is safe to use
    ///     as a public routing gate.
    /// </summary>
    internal static bool TryProbe(byte[] data, out bool isPs3, out string error)
    {
        isPs3 = false;
        if (!TryReadDictionary(data, out _, out var header, out error))
            return false;

        isPs3 = header.Platform == 2;
        return true;
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
            if (!TryReadDictionary(data, out var body, out var header, out var error))
                return Ps2TexResult.Fail(error);

            if (header.Count == 0)
                return new Ps2TexResult([]);

            var textures = new List<Ps2Texture>(header.Count);
            for (var i = 0; i < header.Count; i++)
            {
                Ps2Texture texture;
                try
                {
                    texture = ReadTexture(body, header, i, vramPayload);
                }
                catch (Exception ex) when (ex is ArgumentException or ArithmeticException
                                           or InvalidDataException or IndexOutOfRangeException)
                {
                    return Ps2TexResult.Fail($"Texture record {i}: {ex.Message}");
                }

                if (texture.Pixels == null && (header.Platform == 1 || vramPayload != null))
                {
                    return Ps2TexResult.Fail(
                        $"Texture record {i} has no complete decodable pixel payload");
                }

                textures.Add(texture);
            }

            return new Ps2TexResult(textures);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Parses bytes obtained from an <see cref="AssetSource" />, resolving a
    ///     PS3 VRAM twin through the source backend. Filesystem sources retain the
    ///     size-validated sibling <c>_VRAM.PAK</c> search; archive sources can
    ///     resolve a twin present in the same archive.
    /// </summary>
    public static Ps2TexResult Parse(AssetSource source, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(data);

        var vramPayload = NextGenVramTwinLocator.TryLoad(source, data);
        return Parse(data, vramPayload);
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
    ///     Candidate logical names for an archive-backed PS3 VRAM twin. Project
    ///     8 uses <c>.vtex</c> for some compact entries while Proving Ground and
    ///     named P8 entries use <c>.tvx</c>; <c>.stex</c> consistently pairs with
    ///     <c>.vstex</c>.
    /// </summary>
    internal static IReadOnlyList<string> GetVramTwinCandidateFileNames(
        string dictionaryFileName)
    {
        var primary = GetVramTwinFileName(dictionaryFileName);
        var name = Path.GetFileName(dictionaryFileName);
        var texIndex = name.LastIndexOf(".tex.", StringComparison.OrdinalIgnoreCase);
        if (texIndex < 0)
            return [primary];

        var alternate = name[..texIndex] + ".vtex." + name[(texIndex + ".tex.".Length)..];
        return string.Equals(primary, alternate, StringComparison.OrdinalIgnoreCase)
            ? [primary]
            : [primary, alternate];
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
        if (!TryReadDictionary(data, out var body, out var header, out _))
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
        var xenonPayloadLength = header.Platform == 1
            ? GetXenonPayloadLength(body, header, index, record.PayloadOffset)
            : 0;
        var pixels = DecodePixels(body, header, record, vramPayload, xenonPayloadLength);
        return new Ps2Texture(record.Checksum, record.Width, record.Height,
            (uint)record.Format, 0, pixels);
    }

    private static byte[]? DecodePixels(
        byte[] body,
        Header header,
        Record record,
        byte[]? vramPayload,
        int xenonPayloadLength)
    {
        if (record.Width <= 0 || record.Height <= 0) return null;

        var isXenon = header.Platform == 1;
        var blockBytes = GetBlockBytes(record, isXenon);
        if (blockBytes == 0) return null;

        ReadOnlySpan<byte> source;
        if (isXenon)
        {
            // The Xenon payload word is an offset from the START OF THE FILE, not
            // from dataStart (measured: adding dataStart puts every surface past
            // its real position and nothing decodes).
            if (record.PayloadOffset < 0 || xenonPayloadLength <= 0
                || record.PayloadOffset > body.Length - xenonPayloadLength)
            {
                return null;
            }

            if ((record.PayloadOffset & 0xFFF) != 0 || (xenonPayloadLength & 0xFFF) != 0)
            {
                throw new InvalidDataException(
                    "Xenon texture payload offsets and allocations must be 4 KiB aligned");
            }

            source = body.AsSpan(record.PayloadOffset, xenonPayloadLength);
        }
        else
        {
            if (vramPayload == null) return null;
            if (record.PayloadOffset < 0 || record.PayloadLength <= 0
                || record.PayloadOffset > vramPayload.Length - record.PayloadLength)
            {
                throw new InvalidDataException(
                    $"PS3 VRAM payload is truncated for the declared range " +
                    $"{record.PayloadOffset}+{record.PayloadLength}");
            }

            source = vramPayload.AsSpan(record.PayloadOffset, record.PayloadLength);
        }

        // ARGB8888 tiles per TEXEL; the block-compressed formats tile per 4x4
        // block. DXN is addressed as one 16-byte Xenos block here. Its two
        // 8-byte BC4 halves are separated only by the BC5 decoder; treating
        // either half as the tiling unit visibly macro-scrambles corpus art.
        var uncompressed = blockBytes == 4;
        var unitsX = uncompressed ? record.Width : Math.Max(1, (record.Width + 3) / 4);
        var unitsY = uncompressed ? record.Height : Math.Max(1, (record.Height + 3) / 4);

        // The fetch constant's endian field selects the swap width: 1 = 16-bit
        // (the DXT formats), 2 = 32-bit (whole ARGB texels).
        var swapWidth = record.EndianMode switch { 1 => 2, 2 => 4, _ => 0 };

        var surfaceByteOffset = isXenon
            ? XenosTiling.GetSurfaceByteOffset(record.Width, record.Height, blockBytes)
            : 0;
        var units = isXenon
            ? XenosTiling.UntileUnits(
                source,
                unitsX,
                unitsY,
                blockBytes,
                swapWidth,
                surfaceByteOffset,
                wrapAtEnd: true)
            : source.ToArray();

        var required = checked(unitsX * unitsY * blockBytes);
        if (units.Length < required) return null;

        var rgba = (isXenon, record.Format, blockBytes) switch
        {
            (true, XenonFormatDxn, _) =>
                DxtDecoder.DecodeBc5(units, record.Width, record.Height),
            (_, _, 4) => ArgbToRgba(units, record.Width, record.Height),
            (_, _, 8) => DxtDecoder.DecodeDxt1(units, record.Width, record.Height),
            _ => DxtDecoder.DecodeDxt5(units, record.Width, record.Height)
        };

        var rgbaLength = checked(record.Width * record.Height * 4);
        if (rgba.Length != rgbaLength) return null;

        return FlipRows(rgba, record.Width, record.Height);
    }

    /// <summary>
    ///     Xenon records do not store a payload length. Their offsets are ordered,
    ///     so the next greater record offset (or EOF for the final record) is the
    ///     exact allocation boundary. Keeping that boundary matters: reading to
    ///     EOF lets a damaged surface borrow bytes from the following texture.
    /// </summary>
    private static int GetXenonPayloadLength(
        byte[] body,
        Header header,
        int recordIndex,
        int payloadOffset)
    {
        var end = body.Length;
        for (var index = 0; index < header.Count; index++)
        {
            if (index == recordIndex) continue;
            var otherOffset = ReadRecord(body, header, index).PayloadOffset;
            if (otherOffset > payloadOffset && otherOffset < end)
                end = otherOffset;
        }

        return end - payloadOffset;
    }

    /// <summary>Stored A,R,G,B per texel; the exporter wants R,G,B,A.</summary>
    private static byte[] ArgbToRgba(byte[] argb, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
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
        var stride = checked(width * 4);
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
                XenonFormatDxn => 16,
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

    private static bool TryReadDictionary(
        byte[] data,
        out byte[] body,
        out Header header,
        out string error)
    {
        body = data;
        header = default;
        error = string.Empty;

        try
        {
            if (!TryUnwrap(data, out body))
            {
                error = "Not a FACECAA7 texture dictionary";
                return false;
            }

            if (!TryReadHeader(body, out header, out error))
                return false;

            for (var index = 0; index < header.Count; index++)
            {
                var offset = checked(header.TableOffset + index * header.RecordSize);
                var wordCount = body[offset];
                var byteSize = body[offset + 1];
                if (byteSize != header.RecordSize || wordCount * 4 != header.RecordSize)
                {
                    error = $"Texture record {index} does not repeat the table stride";
                    return false;
                }

                var record = ReadRecord(body, header, index);
                if (record.Width <= 0 || record.Height <= 0
                    || record.Width > 16_384 || record.Height > 16_384
                    || (long)record.Width * record.Height * 4 > Array.MaxLength)
                {
                    error = $"Texture record {index} has implausible dimensions " +
                            $"{record.Width}x{record.Height}";
                    return false;
                }

                var blockBytes = GetBlockBytes(record, header.Platform == 1);
                if (blockBytes == 0)
                {
                    error = $"Texture record {index} uses unsupported format 0x{record.Format:X2}";
                    return false;
                }

                if (record.PayloadOffset < 0 || header.Platform == 2 && record.PayloadLength <= 0)
                {
                    error = $"Texture record {index} has an invalid payload range";
                    return false;
                }

                if (header.Platform == 1
                    && (record.PayloadOffset < header.DataStart
                        || record.PayloadOffset >= body.Length
                        || (record.PayloadOffset & 0xFFF) != 0))
                {
                    error = $"Texture record {index} points outside the Xenon payload region";
                    return false;
                }

                if (header.Platform == 1)
                {
                    var allocationLength = GetXenonPayloadLength(
                        body, header, index, record.PayloadOffset);
                    if (allocationLength <= 0 || (allocationLength & 0xFFF) != 0)
                    {
                        error = $"Texture record {index} has a truncated or " +
                                "non-4-KiB Xenon allocation";
                        return false;
                    }

                    var uncompressed = blockBytes == 4;
                    var unitsX = uncompressed
                        ? record.Width
                        : Math.Max(1, (record.Width + 3) / 4);
                    var unitsY = uncompressed
                        ? record.Height
                        : Math.Max(1, (record.Height + 3) / 4);
                    XenosTiling.ValidateUnitMapping(
                        allocationLength,
                        unitsX,
                        unitsY,
                        blockBytes,
                        XenosTiling.GetSurfaceByteOffset(
                            record.Width, record.Height, blockBytes),
                        wrapAtEnd: true);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArithmeticException
                                   or InvalidDataException or IndexOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
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

        if (BinaryPrimitives.ReadUInt32BigEndian(body) != Magic)
        {
            error = "Not a FACECAA7 texture dictionary";
            return false;
        }

        var platform = body[4];
        var headerSize = body[5];
        var expectedHeaderSize = platform switch
        {
            1 => XenonHeaderSize,
            2 => Ps3HeaderSize,
            _ => 0
        };
        if (expectedHeaderSize == 0)
        {
            error = $"Unknown texture platform {platform}";
            return false;
        }

        if (headerSize != expectedHeaderSize)
        {
            error = $"Header size 0x{headerSize:X2} does not match platform {platform}";
            return false;
        }

        if (body.Length < headerSize)
        {
            error = "Texture dictionary header is truncated";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6));
        var rawTableOffset = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8));
        var rawDataStart = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x0C));
        if (rawTableOffset > int.MaxValue || rawDataStart > int.MaxValue)
        {
            error = "Texture dictionary offsets exceed the supported address range";
            return false;
        }

        var tableOffset = (int)rawTableOffset;
        var dataStart = (int)rawDataStart;
        if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x10)) != uint.MaxValue)
        {
            error = "Texture dictionary sentinel at +0x10 is missing";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x18)) != headerSize)
        {
            error = "Header size is not echoed at +0x18";
            return false;
        }

        if (platform == 2
            && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x1C)) != body.Length)
        {
            error = "PS3 texture dictionary self-length does not match the file";
            return false;
        }

        if (tableOffset < headerSize || tableOffset > body.Length
            || dataStart < tableOffset || dataStart > body.Length)
        {
            error = $"Invalid record/data offsets ({tableOffset}, {dataStart})";
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
            if (tableOffset > body.Length - 2)
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

            if (platform == 2 && recordSize != 48 || platform == 1 && recordSize == 48)
            {
                error = $"Record stride {recordSize} does not match platform {platform}";
                return false;
            }

            var auxOffsetLong = (long)tableOffset + (long)count * recordSize;
            if (auxOffsetLong > int.MaxValue)
            {
                error = "Texture record table is too large";
                return false;
            }

            var auxOffset = (int)auxOffsetLong;
            if (auxOffset > body.Length || dataStart < auxOffset || (dataStart - auxOffset) % count != 0)
            {
                error = "Auxiliary descriptor span does not divide evenly";
                return false;
            }

            auxStride = (dataStart - auxOffset) / count;
            var expectedAuxStride = platform == 2 ? 24 : recordSize == 32 ? 40 : 52;
            if (auxStride != expectedAuxStride)
            {
                error = $"Unexpected auxiliary descriptor stride {auxStride}";
                return false;
            }
        }
        else if (dataStart != tableOffset)
        {
            error = "Empty texture dictionary has different table and data offsets";
            return false;
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
        var rawPayloadOffset = isXenon ? (uint)trail[^1] : (uint)trail[1];
        var rawPayloadLength = isXenon ? 0u : (uint)trail[2];
        if (rawPayloadOffset > int.MaxValue || rawPayloadLength > int.MaxValue)
            throw new InvalidDataException("Texture payload range exceeds the supported address range");
        var payloadOffset = (int)rawPayloadOffset;
        var payloadLength = (int)rawPayloadLength;

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
