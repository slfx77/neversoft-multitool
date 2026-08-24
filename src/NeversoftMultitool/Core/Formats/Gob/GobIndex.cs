using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gob;

/// <summary>
///     One stored chunk of a GOB file. <see cref="NextChunk" /> links the chunks of
///     a single logical file into a chain terminated by <see cref="GobIndex.ChainEnd" />;
///     a chunk's bytes may be shared by several chains (Sk8land dedups 11,633 of its
///     18,540 chunks onto 6,907 distinct blobs).
/// </summary>
public readonly record struct GobChunk(uint StoredSize, uint Offset, ushort NextChunk, byte Codec, uint Checksum);

/// <summary>
///     One logical file. <see cref="NameCrc" /> is the container's only identity — a
///     CRC-32 of the lowercased name, resolved through <see cref="GobNames" />.
/// </summary>
public readonly record struct GobFile(uint NameCrc, uint UncompressedSize, uint FirstChunk);

/// <summary>
///     The Vicarious Visions <c>.gfc</c> index that describes a companion <c>.gob</c>
///     blob — the container holding essentially all content in the three Tony Hawk
///     Nintendo DS carts (American Sk8land, Downhill Jam, Proving Ground).
///
///     Every field is BIG-endian, and the layout consumes the file exactly:
///     <code>
///     u32 magic = 0x00008008
///     u32 gobLength                       // equals the .gob's length exactly
///     u32 chunkCount
///     u32 fileCount
///     chunkCount x { u32 storedSize, u32 offset, u16 =0, u16 nextChunk,
///                    u8 codec, u8 =0, u16 =0 }
///     chunkCount x   u32 checksum         // adler32(stored bytes) SEEDED WITH 0
///     fileCount  x { u32 nameCrc, u32 uncompressedSize, u32 firstChunk }
///     </code>
///
///     Transcribed from the ARM9 loader, which is uncompressed in all three carts:
///     it reads four byte-swapped u32s into its context (<c>+0x10C/+0x110/+0x114</c>,
///     Sk8land <c>0x020B87EC</c>-<c>0x020B88E0</c>), seeks past
///     <c>chunkCount &lt;&lt; 4</c> (<c>0x020B8968</c>), then reads the
///     <c>chunkCount * 4</c> checksum array (<c>0x020B899C</c>) and the
///     <c>fileCount * 0xC</c> file array (<c>0x020B8A68</c>). A further
///     <c>fileCount * 0x108</c> debug array (a 256-byte name plus two u32s) is read
///     only when its pointer is non-null, which it never is in retail — which is
///     exactly why the tail is <c>4*chunkCount + 12*fileCount</c> and nothing more.
///
///     Parsing is strict: unknown codecs, non-zero reserved fields, out-of-range
///     offsets, and chains that revisit a chunk are all rejected rather than
///     garbage-parsed, so a look-alike file errors with a reason instead of
///     producing plausible nonsense.
/// </summary>
public sealed class GobIndex
{
    /// <summary>Header magic, big-endian.</summary>
    public const uint Magic = 0x8008;

    /// <summary>Terminates a chunk chain.</summary>
    public const ushort ChainEnd = 0x7FFF;

    /// <summary><see cref="GobFile.FirstChunk" /> value for a file with no chunks.</summary>
    public const uint NoChunk = 0xFFFFFFFF;

    public const int HeaderSize = 16;
    private const int ChunkRecordSize = 16;
    private const int FileRecordSize = 12;

    private GobIndex(long gobLength, GobChunk[] chunks, GobFile[] files)
    {
        GobLength = gobLength;
        Chunks = chunks;
        Files = files;
    }

    /// <summary>Length the index declares for its companion <c>.gob</c>.</summary>
    public long GobLength { get; }

    public IReadOnlyList<GobChunk> Chunks { get; }
    public IReadOnlyList<GobFile> Files { get; }

    /// <summary>
    ///     Expected <c>.gfc</c> length for the counts in an already-read header, or
    ///     -1 when the header is not a GOB index or the counts overflow. Lets
    ///     detection settle the format from 16 bytes plus two file lengths, with no
    ///     record walk.
    /// </summary>
    public static long GetExpectedIndexLength(ReadOnlySpan<byte> header, out long gobLength)
    {
        gobLength = -1;
        if (header.Length < HeaderSize)
            return -1;
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic)
            return -1;

        gobLength = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        long chunkCount = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        long fileCount = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        return HeaderSize + chunkCount * (ChunkRecordSize + 4) + fileCount * FileRecordSize;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out GobIndex? index)
    {
        try
        {
            index = Parse(data);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            index = null;
            return false;
        }
    }

    public static GobIndex Parse(ReadOnlySpan<byte> data)
    {
        var expected = GetExpectedIndexLength(data, out var gobLength);
        if (expected < 0)
            throw new InvalidDataException("Not a GOB index (.gfc): magic 0x00008008 not found.");
        if (expected != data.Length)
            throw new InvalidDataException(
                $"GOB index length mismatch: header describes {expected} bytes, file is {data.Length}.");

        var chunkCount = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var fileCount = (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);

        // A chain terminator that is also a valid index would make the format
        // ambiguous. No shipped cart comes close (18,540 chunks is the maximum).
        if (chunkCount > ChainEnd)
            throw new InvalidDataException(
                $"GOB index declares {chunkCount} chunks, which collides with the 0x7FFF chain terminator.");

        var checksumBase = HeaderSize + chunkCount * ChunkRecordSize;
        var fileBase = checksumBase + chunkCount * 4;

        var chunks = new GobChunk[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var record = data.Slice(HeaderSize + i * ChunkRecordSize, ChunkRecordSize);
            var storedSize = BinaryPrimitives.ReadUInt32BigEndian(record);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
            var reserved0 = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var next = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            var codec = record[12];
            var reserved1 = record[13];
            var reserved2 = BinaryPrimitives.ReadUInt16BigEndian(record[14..]);

            if (reserved0 != 0 || reserved1 != 0 || reserved2 != 0)
                throw new InvalidDataException(
                    $"GOB chunk {i}: reserved fields are not zero ({reserved0}, {reserved1}, {reserved2}).");
            if (!GobCodec.IsKnownCodec(codec))
                throw new InvalidDataException(
                    $"GOB chunk {i}: unknown codec 0x{codec:X2}; only '0' (stored) and 'z' (zlib) are known.");
            if (offset + (long)storedSize > gobLength)
                throw new InvalidDataException(
                    $"GOB chunk {i}: range [{offset}, {offset + (long)storedSize}) runs past the " +
                    $"{gobLength}-byte data file.");
            if (next != ChainEnd && next >= chunkCount)
                throw new InvalidDataException($"GOB chunk {i}: next-chunk index {next} is out of range.");

            chunks[i] = new GobChunk(
                storedSize, offset, next, codec,
                BinaryPrimitives.ReadUInt32BigEndian(data[(checksumBase + i * 4)..]));
        }

        var files = new GobFile[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var record = data.Slice(fileBase + i * FileRecordSize, FileRecordSize);
            var first = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            if (first != NoChunk && first >= (uint)chunkCount)
                throw new InvalidDataException($"GOB file {i}: first-chunk index {first} is out of range.");
            files[i] = new GobFile(
                BinaryPrimitives.ReadUInt32BigEndian(record),
                BinaryPrimitives.ReadUInt32BigEndian(record[4..]),
                first);
        }

        ValidateChains(chunks, files);
        return new GobIndex(gobLength, chunks, files);
    }

    /// <summary>
    ///     Every chunk must be owned by at most one chain. Walking with an owner map
    ///     rejects both a chunk claimed by two files and a chain that loops — the
    ///     latter matters because a read follows these links.
    /// </summary>
    private static void ValidateChains(GobChunk[] chunks, GobFile[] files)
    {
        var owner = new int[chunks.Length];
        Array.Fill(owner, -1);
        for (var f = 0; f < files.Length; f++)
        {
            var current = files[f].FirstChunk;
            while (current != NoChunk)
            {
                if (owner[current] != -1)
                    throw new InvalidDataException(
                        $"GOB file {f}: chunk {current} is already owned by file {owner[current]} " +
                        "(a shared chunk or a looping chain).");
                owner[current] = f;
                var next = chunks[current].NextChunk;
                current = next == ChainEnd ? NoChunk : next;
            }
        }
    }

    /// <summary>Chunk indices of one file's chain, in order.</summary>
    public IEnumerable<int> ChunksOf(int fileIndex)
    {
        var current = Files[fileIndex].FirstChunk;
        while (current != NoChunk)
        {
            yield return (int)current;
            var next = Chunks[(int)current].NextChunk;
            current = next == ChainEnd ? NoChunk : next;
        }
    }
}
