using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The THPS3 GBA <b>3D rider</b>: a real-time software-rendered model (the
///     cart carries the same IWRAM ARM 3D library THPS2 ships — a 3x3 integer
///     transform over three-signed-byte vertices, a 192-bucket depth sort and two
///     triangle rasterizers) drawn into a 64×64 8bpp OBJ every frame. Like the
///     THPS2 skater it animates by <b>morph frames</b>: every pose stores the
///     complete rider vertex set; unlike THPS2 the deck is a separate rigid part
///     stored once and posed per frame by a translation in the frame header.
///
///     <para><b>Container</b> (located by shape — the only table of this shape in
///     the ROM; every structure below closes by arithmetic that cannot hold by
///     accident, re-measured against the retail ROM and a live emulator capture):
///     a directory of <c>0x14</c>-byte records <c>{u32 mesh, u32 bankStart, u32
///     bankEnd, u32 clipTable, u32 lz77}</c>. Each mesh header is <c>{u32
///     frameStride; u8 part0Verts, part1Verts, part0Faces, part1Faces; u32 selfPtr
///     == mesh + 0xC}</c>, and its 12-byte face bank ends exactly at
///     <c>bankStart</c>. <c>frameStride == 12 + 3·ceil4(part0Verts)</c> for the
///     animated rider, <c>4 + 3·ceil4(part0Verts) + 3·ceil4(part1Verts)</c> for a
///     static record; the two forms cannot collide. The lz77 flag is proven from
///     the loader (it gates the BIOS decompress), and record 0 — the rider — is raw.</para>
///
///     <para><b>Faces</b> are 12 bytes: <c>{u8 v0, v1, v2, 0; u8 u0, v0, u1, v1,
///     u2, v2; u8 material; u8 flag}</c>. Vertex indices are GLOBAL — part 0 spans
///     exactly <c>[0, part0Verts)</c> and part 1 (the deck) exactly
///     <c>[part0Verts, part0Verts+part1Verts)</c>, both ends touched. The six
///     bytes after the indices are per-corner texture coordinates: the library's
///     textured rasterizer (<c>ldrb lr, [r3, lr, lsr #18]</c> after packing
///     <c>(v &amp; 0x3F) &lt;&lt; 6 | u</c>) reads a <b>64×64 8bpp page</b> whose base
///     it receives in <c>r3</c>, and the setup shifts each byte by 22 so a byte is
///     the texel coordinate in <b>6.2 fixed point</b> (values reach 253 = 63.25).
///     Its flat sibling instead stores <c>r3 + material</c> as the colour, so the
///     material byte is a palette-relative colour for the flat path and the flag
///     byte is consumed by neither rasterizer nor the sort. Which page the rider
///     binds is NOT located: no ROM pointer in the live render descriptor names it,
///     so the export carries the coordinates and no image.</para>
///
///     <para><b>Pose bank</b> (record 0): <c>3·ceil4(part1Verts)</c> bytes of deck
///     vertices, then <c>frameCount × frameStride</c> frames, closing exactly
///     against <c>bankEnd</c> (THPS3: 72 + 5,024 × 360). A frame is a 12-byte
///     header then <c>part0Verts</c> s8 (x, y, z) triples in 12-byte blocks. Header
///     bytes 4–6 are the <b>deck translation</b>: the live capture's transformed
///     deck copy equals the stored deck plus exactly these three bytes on all 24
///     vertices (frame 686). Bytes 0–2 are an anchor (not the AABB centre —
///     28/296 sampled frames) and bytes 8–10 vary per frame but the same live copy
///     ignores them, so both stay undecoded and unapplied.</para>
///
///     <para><b>Clips</b> use THPS2's exact grammar, which is what closed the
///     earlier "entry 13 exceeds the bank" contradiction: the clip table is
///     <c>{u16 tickStart, u16 tickCount}</c> into a <b>tick→frame remap</b> that
///     fills the region after <c>bankEnd</c> up to the next record's mesh. Every
///     remap entry is a frame in the pool, and the table's furthest tick, aligned
///     to 4 bytes, is exactly the region's length — the same closure that fixes
///     THPS2's clip count. Entries continue past authored-empty <c>(0, 0)</c>
///     clips, so the table is read until an entry leaves the remap. THPS3: 239
///     clips (7 empty), 8,507 ticks, holds of two ticks per frame throughout.</para>
/// </summary>
public static class GbaThps3RiderModel
{
    private const uint RomBase = 0x08000000;

    /// <summary>The directory record stride — also the exact length of a carved
    ///     THPS3 <c>.chr.gba</c> entry, which is what the GUI scanner gates on.</summary>
    public const int DirectoryRecordSize = 0x14;

    public const int MeshHeaderSize = 12;
    public const int FaceRecordSize = 12;
    public const int FrameHeaderSize = 12;

    /// <summary>The textured rasterizer's page: 64 texels a side, addressed
    ///     <c>v * 64 + u</c> from 6.2 fixed-point coordinate bytes.</summary>
    public const int TexturePageSize = 64;

    /// <summary>One directory record plus its mesh header.</summary>
    public readonly record struct Record(
        int DirectoryOffset,
        int MeshOffset,
        int BankStart,
        int BankEnd,
        int ClipTableOffset,
        bool Compressed,
        int FrameStride,
        int Part0Verts,
        int Part1Verts,
        int Part0Faces,
        int Part1Faces,
        bool Animated)
    {
        public int VertexCount => Part0Verts + Part1Verts;
        public int FaceCount => Part0Faces + Part1Faces;
        public int FaceBankOffset => MeshOffset + MeshHeaderSize;
    }

    /// <summary>The closed complex. <see cref="Records" />[0] is the animated rider.</summary>
    public sealed record ModelInfo(
        int DirectoryOffset,
        IReadOnlyList<Record> Records,
        int FrameCount,
        int FramePoolOffset,
        int DeckVertexOffset,
        int TickTableOffset,
        int TickCount,
        int ClipCount)
    {
        public Record Rider => Records[0];
    }

    /// <summary>A per-corner texture coordinate in 6.2 fixed-point texels of the 64×64 page.</summary>
    public readonly record struct TexCoord(byte U, byte V);

    public readonly record struct Face(
        int Part, int V0, int V1, int V2, TexCoord T0, TexCoord T1, TexCoord T2, byte Material, byte Flag);

    public readonly record struct Clip(int Index, int TickStart, int TickCount);

    /// <summary>
    ///     The 12-byte frame header, kept raw. Only the deck translation is
    ///     proven (see the class summary); the anchor and the trailing triple are
    ///     exposed for inspection and never applied.
    /// </summary>
    public readonly record struct FrameHeader(byte[] Raw)
    {
        public (sbyte X, sbyte Y, sbyte Z) Anchor => ((sbyte)Raw[0], (sbyte)Raw[1], (sbyte)Raw[2]);
        public (sbyte X, sbyte Y, sbyte Z) DeckTranslation => ((sbyte)Raw[4], (sbyte)Raw[5], (sbyte)Raw[6]);
        public (sbyte A, sbyte B, sbyte C) Unknown => ((sbyte)Raw[8], (sbyte)Raw[9], (sbyte)Raw[10]);
    }

    /// <summary>
    ///     Locates and closes the rider complex, or null when this ROM does not
    ///     carry it (only THPS3 GBA does; THPS2's skater and Downhill Jam's rider
    ///     are different containers and their carts decline here).
    /// </summary>
    public static ModelInfo? TryLocate(ReadOnlySpan<byte> rom)
    {
        for (var offset = 0; offset + DirectoryRecordSize <= rom.Length; offset += 4)
        {
            if (!TryReadRecord(rom, offset, out var first) || !first.Animated || first.Compressed
                || first.ClipTableOffset < 0)
                continue;
            // A run start: the record before must not itself be a record.
            if (offset >= DirectoryRecordSize && TryReadRecord(rom, offset - DirectoryRecordSize, out _))
                continue;

            var records = new List<Record> { first };
            for (var next = offset + DirectoryRecordSize;
                 TryReadRecord(rom, next, out var record);
                 next += DirectoryRecordSize)
                records.Add(record);
            if (records.Count < 2)
                continue;

            var model = TryClose(rom, offset, records);
            if (model != null)
                return model;
        }

        return null;
    }

    /// <summary>All face records, part 0 (rider) first, then part 1 (deck).</summary>
    public static List<Face> ReadFaces(ReadOnlySpan<byte> rom, ModelInfo model) => ReadFaces(rom, model.Rider);

    public static List<Face> ReadFaces(ReadOnlySpan<byte> rom, Record record)
    {
        var faces = new List<Face>(record.FaceCount);
        for (var i = 0; i < record.FaceCount; i++)
        {
            var at = record.FaceBankOffset + i * FaceRecordSize;
            faces.Add(new Face(
                i < record.Part0Faces ? 0 : 1,
                rom[at], rom[at + 1], rom[at + 2],
                new TexCoord(rom[at + 4], rom[at + 5]),
                new TexCoord(rom[at + 6], rom[at + 7]),
                new TexCoord(rom[at + 8], rom[at + 9]),
                rom[at + 10], rom[at + 11]));
        }

        return faces;
    }

    /// <summary>One frame's rider vertices (part 0) in model space: s8 (x, y, z), z up.</summary>
    public static sbyte[][] ReadFrameVertices(ReadOnlySpan<byte> rom, ModelInfo model, int frame)
    {
        var offset = model.FramePoolOffset + frame * model.Rider.FrameStride + FrameHeaderSize;
        return ReadTriples(rom, offset, model.Rider.Part0Verts);
    }

    /// <summary>The deck's vertices (part 1), stored once ahead of the frame pool.</summary>
    public static sbyte[][] ReadDeckVertices(ReadOnlySpan<byte> rom, ModelInfo model) =>
        ReadTriples(rom, model.DeckVertexOffset, model.Rider.Part1Verts);

    public static FrameHeader ReadFrameHeader(ReadOnlySpan<byte> rom, ModelInfo model, int frame) =>
        new(rom.Slice(model.FramePoolOffset + frame * model.Rider.FrameStride, FrameHeaderSize).ToArray());

    public static List<Clip> ReadClips(ReadOnlySpan<byte> rom, ModelInfo model)
    {
        var clips = new List<Clip>(model.ClipCount);
        var table = model.Rider.ClipTableOffset;
        for (var i = 0; i < model.ClipCount; i++)
            clips.Add(new Clip(
                i,
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(table + i * 4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(table + i * 4 + 2, 2))));
        return clips;
    }

    /// <summary>The physical frame a clip tick plays (through the tick→frame remap).</summary>
    public static int FrameForTick(ReadOnlySpan<byte> rom, ModelInfo model, int tick) =>
        BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(model.TickTableOffset + tick * 2, 2));

    /// <summary>One clip's frame per tick, in playback order (holds and jumps are real).</summary>
    public static int[] ClipFrames(ReadOnlySpan<byte> rom, ModelInfo model, Clip clip)
    {
        var frames = new int[clip.TickCount];
        for (var t = 0; t < frames.Length; t++)
            frames[t] = FrameForTick(rom, model, clip.TickStart + t);
        return frames;
    }

    /// <summary>
    ///     A static record's bank (records 1–5; LZ77 when flagged) as whole frames
    ///     of <c>{u32 header, part0 triples, part1 triples}</c>. Returns the frame
    ///     count and frame 0's two parts, or null when the bank does not decode to
    ///     an exact multiple of the stride. Parsed for the closure pins; the
    ///     records' role (the counts suggest rider variants) is not established
    ///     and they are not exported.
    /// </summary>
    public static (int FrameCount, sbyte[][] Part0, sbyte[][] Part1)? TryReadStaticRecord(
        ReadOnlySpan<byte> rom, Record record)
    {
        if (record.Animated)
            return null;
        ReadOnlySpan<byte> bank;
        if (record.Compressed)
        {
            // The directory states this bank, so even a one-frame 28-byte record
            // (record 5) is a real stream, below the content scanners' floor.
            if (!GbaBiosLz77.TryDecompress(rom, record.BankStart, out var payload, out _, minDecompressedSize: 1))
                return null;
            bank = payload;
        }
        else
        {
            bank = rom.Slice(record.BankStart, record.BankEnd - record.BankStart);
        }

        if (bank.Length == 0 || bank.Length % record.FrameStride != 0)
            return null;
        var part0 = ReadTriples(bank, 4, record.Part0Verts);
        var part1 = ReadTriples(bank, 4 + 3 * Ceil4(record.Part0Verts), record.Part1Verts);
        return (bank.Length / record.FrameStride, part0, part1);
    }

    private static ModelInfo? TryClose(ReadOnlySpan<byte> rom, int directory, List<Record> records)
    {
        var rider = records[0];
        var deckBytes = 3 * Ceil4(rider.Part1Verts);
        var poolBytes = rider.BankEnd - rider.BankStart - deckBytes;
        if (poolBytes <= 0 || poolBytes % rider.FrameStride != 0)
            return null;
        var frameCount = poolBytes / rider.FrameStride;

        // The tick→frame remap fills the gap between the rider's bank end and the
        // next record's mesh — the directory states both ends.
        var regionEnd = int.MaxValue;
        foreach (var record in records)
        {
            if (record.MeshOffset > rider.BankEnd)
                regionEnd = Math.Min(regionEnd, record.MeshOffset);
        }

        if (regionEnd == int.MaxValue)
            return null;
        var regionBytes = regionEnd - rider.BankEnd;
        if (regionBytes <= 0 || regionBytes % 4 != 0)
            return null;
        var remapCapacity = regionBytes / 2;

        // Clip entries run until one leaves the remap; (0,0) entries are
        // authored-empty clips, not a terminator.
        var clipCount = 0;
        var maxEnd = 0;
        for (var at = rider.ClipTableOffset; at + 4 <= rom.Length; at += 4)
        {
            int start = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(at, 2));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(at + 2, 2));
            if (start + count > remapCapacity)
                break;
            maxEnd = Math.Max(maxEnd, start + count);
            clipCount++;
        }

        // Closure: the furthest tick, aligned to 4 bytes, IS the region.
        if (clipCount == 0 || ((maxEnd * 2 + 3) & ~3) != regionBytes)
            return null;
        for (var tick = 0; tick < maxEnd; tick++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rider.BankEnd + tick * 2, 2)) >= frameCount)
                return null;
        }

        return new ModelInfo(
            directory, records, frameCount,
            FramePoolOffset: rider.BankStart + deckBytes,
            DeckVertexOffset: rider.BankStart,
            TickTableOffset: rider.BankEnd,
            TickCount: maxEnd,
            ClipCount: clipCount);
    }

    private static bool TryReadRecord(ReadOnlySpan<byte> rom, int offset, out Record record)
    {
        record = default;
        if (offset < 0 || offset + DirectoryRecordSize > rom.Length)
            return false;
        var mesh = ReadU32(rom, offset);
        var bankStart = ReadU32(rom, offset + 4);
        var bankEnd = ReadU32(rom, offset + 8);
        var clipTable = ReadU32(rom, offset + 12);
        var compressed = ReadU32(rom, offset + 16);
        if (compressed > 1 || !InRom(rom, mesh) || !InRom(rom, bankStart) || !InRom(rom, bankEnd)
            || mesh >= bankStart || bankStart >= bankEnd)
            return false;
        if (clipTable != 0 && !InRom(rom, clipTable))
            return false;

        var meshOffset = (int)(mesh - RomBase);
        if (meshOffset + MeshHeaderSize > rom.Length)
            return false;
        var stride = (int)ReadU32(rom, meshOffset);
        int v0 = rom[meshOffset + 4], v1 = rom[meshOffset + 5], f0 = rom[meshOffset + 6], f1 = rom[meshOffset + 7];
        if (v0 == 0 || f0 == 0 || stride <= 0 || ReadU32(rom, meshOffset + 8) != mesh + 0xC)
            return false;

        var animated = stride == FrameHeaderSize + 3 * Ceil4(v0);
        var isStatic = stride == 4 + 3 * Ceil4(v0) + 3 * Ceil4(v1);
        if (!animated && !isStatic)
            return false;
        if ((long)meshOffset + MeshHeaderSize + (long)FaceRecordSize * (f0 + f1) != bankStart - RomBase)
            return false;

        record = new Record(
            offset, meshOffset, (int)(bankStart - RomBase), (int)(bankEnd - RomBase),
            clipTable == 0 ? -1 : (int)(clipTable - RomBase), compressed == 1,
            stride, v0, v1, f0, f1, animated);
        return true;
    }

    private static sbyte[][] ReadTriples(ReadOnlySpan<byte> data, int offset, int count)
    {
        var result = new sbyte[count][];
        for (var i = 0; i < count; i++)
            result[i] = [(sbyte)data[offset + i * 3], (sbyte)data[offset + i * 3 + 1], (sbyte)data[offset + i * 3 + 2]];
        return result;
    }

    private static int Ceil4(int value) => (value + 3) / 4 * 4;

    private static bool InRom(ReadOnlySpan<byte> rom, uint address) =>
        address >= RomBase && address < RomBase + (uint)rom.Length;

    private static uint ReadU32(ReadOnlySpan<byte> rom, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));
}
