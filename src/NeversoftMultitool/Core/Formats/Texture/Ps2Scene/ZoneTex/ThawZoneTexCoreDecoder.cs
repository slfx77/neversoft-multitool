using NeversoftMultitool.Core.Formats.Texture.Ps2;
using static NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex.ThawZoneTexFile;
using static NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex.ThawZoneTexVramSupport;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Core decoder for THAW PS2 zone .tex files. Parses the record table plus DMA upload stream,
///     prefers the prepared CPU-side source slots described by the record table, and falls back
///     to upload-snapshot GS VRAM decode when the source-slot path cannot resolve an entry.
///     Based on Ghidra decompilation of the THAW PS2 binary (FUN_0019cd48 pixel decode,
///     FUN_001cfb58 blob processing). See tools/ghidra/thaw-ps2/output/zone_tex_format.md.
/// </summary>
internal static class ThawZoneTexCoreDecoder
{
    /// <summary>
    ///     All data_offset and cumul_off values in records are relative to this base.
    ///     Verified across all 3 extracted zone .tex files (z_ho, z_ho_net, z_sm).
    /// </summary>
    private const int PackedBase = 0x0A;

    private const int RecordSize = 0x40;

    /// <summary>
    ///     Discover the record table by finding the DMA chain start and walking backwards.
    ///     Returns the table start offset and record count, or (-1, 0) if not found.
    /// </summary>
    internal static (int TableStart, int RecordCount) DiscoverRecordTable(ReadOnlySpan<byte> data)
    {
        var dmaStart = FindDmaChainStart(data);
        if (dmaStart < 0)
            return (-1, 0);

        // Walk backwards from DMA start in 0x40-byte steps to find valid records.
        // Each record has a TEX0 at +0x10 with plausible PSM, dimensions, and TBP values.
        var recordEnd = dmaStart;
        var recordStart = recordEnd;
        while (recordStart >= RecordSize)
        {
            var candidateStart = recordStart - RecordSize;
            if (!IsPlausibleRecord(data, candidateStart))
                break;
            recordStart = candidateStart;
        }

        var recordCount = (recordEnd - recordStart) / RecordSize;
        if (recordCount == 0)
            return (-1, 0);

        return (recordStart, recordCount);
    }

    /// <summary>
    ///     Parse all records from the discovered record table.
    /// </summary>
    internal static List<ZoneTexHeaderEntry> ParseRecords(ReadOnlySpan<byte> data, int tableStart, int recordCount)
    {
        var entries = new List<ZoneTexHeaderEntry>(recordCount);
        for (var i = 0; i < recordCount; i++)
        {
            var off = tableStart + i * RecordSize;
            if (off + RecordSize > data.Length)
                break;

            entries.Add(ParseSingleRecord(data, off));
        }

        return entries;
    }

    /// <summary>
    ///     Parse a single 0x40-byte record at the given offset.
    /// </summary>
    private static ZoneTexHeaderEntry ParseSingleRecord(ReadOnlySpan<byte> data, int off)
    {
        var checksum = BitConverter.ToUInt32(data[off..]);
        var groupChecksum = BitConverter.ToUInt32(data[(off + 0x04)..]);
        var mipCount = BitConverter.ToUInt32(data[(off + 0x08)..]);
        var layoutMode = BitConverter.ToUInt32(data[(off + 0x0C)..]);
        var tex0 = BitConverter.ToUInt64(data[(off + 0x10)..]);
        var cumulOff = BitConverter.ToUInt32(data[(off + 0x28)..]);
        var dataSize = BitConverter.ToUInt32(data[(off + 0x2C)..]);
        var dataOffset = BitConverter.ToUInt32(data[(off + 0x30)..]);
        var palBytes = BitConverter.ToUInt32(data[(off + 0x34)..]);
        var uploadOff = BitConverter.ToUInt32(data[(off + 0x38)..]);
        var pixelQwcShifted = BitConverter.ToUInt32(data[(off + 0x3C)..]);

        return new ZoneTexHeaderEntry(
            checksum,
            tex0,
            dataSize,
            dataOffset,
            palBytes,
            uploadOff,
            mipCount,
            pixelQwcShifted >> 12,
            layoutMode,
            groupChecksum,
            cumulOff);
    }

    /// <summary>
    ///     Discover and parse all records from a zone .tex file.
    /// </summary>
    internal static List<ZoneTexHeaderEntry> DiscoverAndParseRecords(ReadOnlySpan<byte> data)
    {
        var (tableStart, recordCount) = DiscoverRecordTable(data);
        if (tableStart < 0)
            return [];

        return ParseRecords(data, tableStart, recordCount);
    }

    /// <summary>
    ///     Legacy direct packed-data decode of a single record. Kept for analyzer paths only;
    ///     production decode replays the DMA upload stream instead.
    /// </summary>
    internal static Ps2Texture? DecodeRecord(ReadOnlySpan<byte> data, ZoneTexHeaderEntry entry)
    {
        var tex0 = entry.Tex0;
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var tw = (int)((tex0 >> 26) & 0xF);
        var th = (int)((tex0 >> 30) & 0xF);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var width = 1 << tw;
        var height = 1 << th;

        // Read CLUT data for paletted formats
        byte[]? clut = null;
        if (entry.PaletteBytes > 0 && psm is Ps2TexPixelDecoder.PSMT4 or Ps2TexPixelDecoder.PSMT8)
        {
            var clutStart = PackedBase + (int)entry.DataOffset;
            var clutEnd = clutStart + (int)entry.PaletteBytes;
            if (clutStart < 0 || clutEnd > data.Length)
                return null;

            clut = data.Slice(clutStart, (int)entry.PaletteBytes).ToArray();

            // CSM1 CLUT unswizzle for PSMT8 with PSMCT32 CLUTs (256 entries, 4 bytes each)
            if (psm == Ps2TexPixelDecoder.PSMT8)
            {
                var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm) / 8;
                if (clutBpp > 0)
                    UnswizzleClutCsm1(clut, clutBpp);
            }
        }

        // Read pixel data from cumul_off (canonical pixel location)
        var pixelStart = PackedBase + (int)entry.CumulativeOffset;
        var pixelEnd = pixelStart + (int)entry.DataSize;
        if (pixelStart < 0 || pixelEnd > data.Length || entry.DataSize == 0)
            return null;

        var pixelData = data.Slice(pixelStart, (int)entry.DataSize);

        // Apply unswizzle based on PSM
        byte[] unswizzled;
        switch (psm)
        {
            case Ps2TexPixelDecoder.PSMT4:
                unswizzled = Ps2TexSwizzle.UnswizzlePsmt4(pixelData, width, height);
                break;
            case Ps2TexPixelDecoder.PSMT8:
                unswizzled = Ps2TexSwizzle.UnswizzlePsmt8(pixelData, width, height);
                break;
            default:
                unswizzled = pixelData.ToArray();
                break;
        }

        // Decode to RGBA
        var rgba = Ps2TexPixelDecoder.DecodePixels(unswizzled, width, height, psm, cpsm, clut);
        if (rgba == null)
            return null;

        return new Ps2Texture(entry.Checksum, width, height, psm, cpsm, rgba);
    }

    /// <summary>
    ///     Decode all records from a zone .tex file by preferring prepared source slots and
    ///     falling back to upload snapshots derived from the DMA chain.
    /// </summary>
    internal static List<Ps2Texture> DecodeAllRecords(ReadOnlySpan<byte> data,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var entries = DiscoverAndParseRecords(data);
        if (entries.Count == 0)
            return [];

        var uploads = ThawZoneTexVramSupport.ParseVramUploads(data);
        return DecodeEntriesFromPreparedSourcesOrUploadSnapshots(data, uploads, entries, gifQwordWordOrder);
    }

    /// <summary>
    ///     Decode specific records from a zone .tex file by preferring prepared source slots and
    ///     falling back to upload snapshots derived from the DMA chain.
    /// </summary>
    internal static List<Ps2Texture> DecodeRecords(ReadOnlySpan<byte> data,
        IEnumerable<ZoneTexHeaderEntry> entries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var entryList = entries as IReadOnlyList<ZoneTexHeaderEntry> ?? entries.ToList();
        if (entryList.Count == 0)
            return [];

        var uploads = ThawZoneTexVramSupport.ParseVramUploads(data);
        return DecodeEntriesFromPreparedSourcesOrUploadSnapshots(data, uploads, entryList, gifQwordWordOrder);
    }

    /// <summary>
    ///     Decode header entries from the file-backed prepared source buffers first, then fall back
    ///     to upload-snapshot VRAM decode for any entries the source-buffer path cannot resolve.
    ///     Ghidra decompilation shows FUN_0019cd48 decoding through CPU-side pixel/clut pointers
    ///     (set up by FUN_001e6818) rather than reading texture data back from GS VRAM.
    /// </summary>
    internal static List<Ps2Texture> DecodeEntriesFromPreparedSourcesOrUploadSnapshots(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        IReadOnlyList<ZoneTexHeaderEntry> entries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        if (entries.Count == 0)
            return [];

        var texturesByChecksum = new Dictionary<uint, Ps2Texture>();

        if (TryGetHeaderDataLayout(fileData, out _, out _))
        {
            foreach (var texture in DecodeFromHeaderDataSlots(fileData, uploads, entries))
                texturesByChecksum.TryAdd(texture.Checksum, texture);
        }

        if (texturesByChecksum.Count < entries.Count && uploads.Count > 0)
        {
            foreach (var texture in DecodeEntriesFromUploadSnapshots(uploads, entries, gifQwordWordOrder))
                texturesByChecksum.TryAdd(texture.Checksum, texture);
        }

        if (texturesByChecksum.Count == 0)
            return [];

        var textures = new List<Ps2Texture>(Math.Min(entries.Count, texturesByChecksum.Count));
        var decodedChecksums = new HashSet<uint>();
        foreach (var checksum in entries.Select(static entry => entry.Checksum))
        {
            if (!decodedChecksums.Add(checksum))
                continue;

            if (texturesByChecksum.TryGetValue(checksum, out var texture))
                textures.Add(texture);
        }

        return textures;
    }

    /// <summary>
    ///     Decode header entries by replaying uploads in order and snapshotting once the target
    ///     texture base and palette base have been populated. This remains the fallback path when
    ///     file-backed prepared source-slot decode is unavailable.
    /// </summary>
    internal static List<Ps2Texture> DecodeEntriesFromUploadSnapshots(
        IReadOnlyList<VramUpload> uploads,
        IReadOnlyList<ZoneTexHeaderEntry> entries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        if (uploads.Count == 0 || entries.Count == 0)
            return [];

        var requests = ThawZoneTexTextureCache.BuildDecodeRequests(uploads, entries);
        if (requests.Count == 0)
            return [];

        var textureCache = ThawZoneTexTextureCache.DecodeTextureCache(uploads, requests, gifQwordWordOrder);
        if (textureCache.Count == 0)
            return [];

        var textures = new List<Ps2Texture>(Math.Min(entries.Count, textureCache.Count));
        var decodedChecksums = new HashSet<uint>();

        foreach (var checksum in entries.Select(static entry => entry.Checksum))
        {
            if (!decodedChecksums.Add(checksum))
                continue;

            if (textureCache.TryGetValue(checksum, out var texture))
                textures.Add(texture);
        }

        return textures;
    }

    /// <summary>
    ///     Decode header entries using a pre-built VRAM simulation.
    ///     Used by DecodeFromTex0Values and other VRAM-only paths.
    /// </summary>
    internal static List<Ps2Texture> DecodeEntriesFromVram(Ps2GsVram vram,
        IEnumerable<ZoneTexHeaderEntry> entries)
    {
        var textures = new List<Ps2Texture>();
        var decodedChecksums = new HashSet<uint>();

        foreach (var entry in entries)
        {
            if (!decodedChecksums.Add(entry.Checksum))
                continue;

            var decoded = ThawZoneTexVramSupport.DecodeFromTex0(vram, entry.Tex0);
            if (decoded == null)
                continue;

            var psm = (uint)((entry.Tex0 >> 20) & 0x3F);
            var cpsm = (uint)((entry.Tex0 >> 51) & 0xF);
            var tw = 1 << (int)((entry.Tex0 >> 26) & 0xF);
            var th = 1 << (int)((entry.Tex0 >> 30) & 0xF);
            textures.Add(new Ps2Texture(entry.Checksum, tw, th, psm, cpsm, decoded));
        }

        return textures;
    }

    /// <summary>
    ///     Detect whether data looks like a zone .tex file by trying to discover the record table.
    /// </summary>
    internal static bool IsZoneTex(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x200) return false;

        // Must NOT be a standard format
        var version = BitConverter.ToUInt16(data);
        if (version is >= 2 and <= 6) return false;

        var (_, recordCount) = DiscoverRecordTable(data);
        return recordCount > 0;
    }

    // ── DMA chain scanning ──────────────────────────────────────────────

    /// <summary>
    ///     Find the DMA chain start by scanning for the first CNT tag with QWC=6
    ///     (0x10000006) followed by a valid GIF A+D block.
    /// </summary>
    private static int FindDmaChainStart(ReadOnlySpan<byte> data)
    {
        for (var off = 0; off + 128 <= data.Length; off += 16)
        {
            var tag = BitConverter.ToUInt32(data[off..]);
            if (tag != 0x10000006)
                continue;

            // Verify next QW is a GIF A+D tag (NLOOP>=3, NREG=1, REGS=0x0E)
            if (off + 16 + 8 > data.Length)
                continue;

            var gifLo = BitConverter.ToUInt64(data[(off + 16)..]);
            var gifHi = BitConverter.ToUInt64(data[(off + 24)..]);
            var nloop = (int)(gifLo & 0x7FFF);
            var flg = (int)((gifLo >> 58) & 3);
            var nreg = (int)((gifLo >> 60) & 0xF);
            if (nreg == 0) nreg = 16;

            if (flg == 0 && nreg == 1 && gifHi == 0x0E && nloop is >= 3 and <= 20)
                return off;
        }

        return -1;
    }

    /// <summary>
    ///     Check whether a 0x40-byte block at the given offset looks like a valid record.
    ///     Validates TEX0 at +0x10 for plausible PSM, dimensions, and buffer pointers.
    /// </summary>
    private static bool IsPlausibleRecord(ReadOnlySpan<byte> data, int off)
    {
        if (off + RecordSize > data.Length || off < 0)
            return false;

        var checksum = BitConverter.ToUInt32(data[off..]);
        if (checksum == 0)
            return false;

        var tex0 = BitConverter.ToUInt64(data[(off + 0x10)..]);
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var tw = (int)((tex0 >> 26) & 0xF);
        var th = (int)((tex0 >> 30) & 0xF);

        if (!Ps2TexPixelDecoder.IsValidPsm(psm))
            return false;
        if (tw < 1 || tw > 10 || th < 1 || th > 10)
            return false;

        return true;
    }
}
