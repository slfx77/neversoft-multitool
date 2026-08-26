using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Tests.Core.Formats.Gob;

/// <summary>
///     Pins the Vicarious Visions DS GOB container: a synthetic <c>.gob</c>/<c>.gfc</c>
///     pair round-trips through both codecs and a multi-chunk chain, the parser's
///     rejection gates hold, and the three shipped Tony Hawk DS carts rebuild every
///     one of their files at exactly the size the index declares.
///
///     That last check is the real proof of the format. The index's declared size, the
///     per-chunk seed-0 Adler over the stored bytes, and the compressed chunks' own
///     trailers are three independent statements about the same bytes — a misparse
///     cannot satisfy all three by accident.
/// </summary>
public sealed class GobArchiveTests(TestPaths paths)
{
    private const string Sk8landBuild = "Tony Hawk's American Sk8land (2005-11-15, DS - Final)";
    private const string Sk8landRom = "Tony Hawk's American Sk8land (USA).nds";
    private const string DhjBuild = "Tony Hawk's Downhill Jam (2006-10-24, DS - Final)";
    private const string DhjRom = "Tony Hawk's Downhill Jam (USA).nds";
    private const string PgBuild = "Tony Hawk's Proving Ground (2007-10-15, DS - Final)";
    private const string PgRom = "Tony Hawk's Proving Ground (USA).nds";

    private static readonly byte[] StoredPayload = Encoding.ASCII.GetBytes("plain stored chunk payload");

    private static readonly byte[] CompressiblePayload =
        Encoding.ASCII.GetBytes(new string('a', 400) + "tail" + new string('b', 400));

    private static readonly byte[] ChainPartA = Encoding.ASCII.GetBytes("first-chunk-");
    private static readonly byte[] ChainPartB = Encoding.ASCII.GetBytes(new string('c', 300));
    private static readonly byte[] ChainPartC = Encoding.ASCII.GetBytes("-last-chunk");

    [Fact]
    public void Parse_SyntheticPair_RebuildsBothCodecsAndAChain()
    {
        var (gfc, gob) = BuildSyntheticPair();
        var index = GobArchive.ReadIndex(gfc, gob.Length);

        Assert.Equal(5, index.Chunks.Count);
        Assert.Equal(4, index.Files.Count);
        Assert.Equal(gob.Length, index.GobLength);

        var read = GobArchive.CreateReader(gob);
        Assert.Equal(StoredPayload, GobArchive.ReadFile(index, 0, read));
        Assert.Equal(CompressiblePayload, GobArchive.ReadFile(index, 1, read));
        Assert.Equal(
            ChainPartA.Concat(ChainPartB).Concat(ChainPartC).ToArray(),
            GobArchive.ReadFile(index, 2, read));

        // A file with no chunks is legal and rebuilds empty (7 in Downhill Jam, 8 in Proving Ground).
        Assert.Empty(GobArchive.ReadFile(index, 3, read));
    }

    [Fact]
    public void BuildFileList_NamesUnresolvedFilesAfterTheirKey()
    {
        var (gfc, gob) = BuildSyntheticPair();
        var entries = GobArchive.BuildFileList(GobArchive.ReadIndex(gfc, gob.Length));

        Assert.Equal(4, entries.Count);
        // The synthetic keys are arbitrary, so nothing resolves: entries fall back to
        // the container's own key, which is stable across runs.
        Assert.All(entries, e => Assert.EndsWith(".bin", e.Name, StringComparison.Ordinal));
        Assert.Equal($"{entries[0].Crc:x8}.bin", entries[0].Name);
        // Offset is the FILE INDEX, not a byte offset.
        Assert.Equal([0L, 1L, 2L, 3L], entries.Select(e => e.Offset).ToArray());
    }

    [Fact]
    public void Parse_RejectsBadMagic()
    {
        var (gfc, gob) = BuildSyntheticPair();
        BinaryPrimitives.WriteUInt32BigEndian(gfc, 0x8009);
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsLengthThatDoesNotConsumeTheIndexExactly()
    {
        var (gfc, gob) = BuildSyntheticPair();
        var truncated = gfc[..^1];
        Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(truncated, gob.Length));

        var padded = gfc.Concat(new byte[4]).ToArray();
        Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(padded, gob.Length));
    }

    [Fact]
    public void Parse_RejectsCompanionOfTheWrongLength()
    {
        var (gfc, gob) = BuildSyntheticPair();
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length + 1));
        Assert.Contains("data file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsUnknownCodec()
    {
        var (gfc, gob) = BuildSyntheticPair();
        gfc[16 + 12] = (byte)'q';
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length));
        Assert.Contains("codec", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsNonZeroReservedField()
    {
        var (gfc, gob) = BuildSyntheticPair();
        gfc[16 + 13] = 1;
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsChunkRangeOutsideTheDataFile()
    {
        var (gfc, gob) = BuildSyntheticPair();
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(16 + 4), (uint)gob.Length - 1);
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length));
        Assert.Contains("runs past", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsALoopingChain()
    {
        var (gfc, gob) = BuildSyntheticPair();
        // Chunk 2 is the head of the three-chunk chain; point its tail back at itself.
        BinaryPrimitives.WriteUInt16BigEndian(gfc.AsSpan(16 + 4 * 16 + 10), 2);
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length));
        Assert.Contains("already owned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsAChunkClaimedByTwoFiles()
    {
        var (gfc, gob) = BuildSyntheticPair();
        var fileBase = 16 + 5 * 16 + 5 * 4;
        // Point file 1's chain at file 0's chunk.
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(fileBase + 12 + 8), 0);
        var ex = Assert.Throws<InvalidDataException>(() => GobArchive.ReadIndex(gfc, gob.Length));
        Assert.Contains("already owned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadFile_RejectsACorruptedChunk()
    {
        var (gfc, gob) = BuildSyntheticPair();
        var index = GobArchive.ReadIndex(gfc, gob.Length);
        gob[(int)index.Chunks[0].Offset] ^= 0xFF;
        var ex = Assert.Throws<InvalidDataException>(
            () => GobArchive.ReadFile(index, 0, GobArchive.CreateReader(gob)));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adler0_IsSeededWithZeroNotOne()
    {
        // The whole container uses Adler-32 seeded 0. The difference from the standard
        // seed is exactly (length << 16) + 1 — which is why a stock zlib reader inflates
        // these streams correctly and then fails its trailing check.
        var data = Encoding.ASCII.GetBytes("the quick brown fox");
        var seeded0 = GobCodec.Adler0(data);
        uint s1 = 1, s2 = 0;
        foreach (var b in data)
        {
            s1 = (s1 + b) % 65521;
            s2 = (s2 + s1) % 65521;
        }

        var seeded1 = (s2 << 16) | s1;
        Assert.Equal(seeded1, seeded0 + ((uint)data.Length << 16) + 1);
    }

    [CorpusTheory]
    [InlineData(Sk8landBuild, Sk8landRom, "vvobj/generated/gob/main", 18540, 14606, 21411156, 81141680, 14550,
        "DefaultConfig.xml", 698, "95fbae5b9fd13a3cdf02bedff9a0a4c2ab6478b2d313c8303ad3f222d812661e")]
    [InlineData(DhjBuild, DhjRom, "vvobj/generated/gob/main", 12087, 4657, 60741360, 71658474, 4490,
        "DefaultConfig.xml", 1401, "3580f1c9c45945f7eb717230da4585cd3021f30bb970ed9409b04ea52108ef88")]
    [InlineData(PgBuild, PgRom, "gob/mainUS", 11016, 5665, 48392876, 55721067, 5096,
        "DefaultConfig.xml", 1015, "b226d23c03a8fae417a62a3c280072b16b7f5ceb828df6ee01258445bbccf48d")]
    public void RealCart_RebuildsEveryFileAtItsDeclaredSize(
        string build, string rom, string containerStem, int chunkCount, int fileCount,
        long gobLength, long totalBytes, int namedFiles, string pinnedPath, int pinnedSize, string pinnedSha)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        var (gfc, gob) = ReadContainerFromCart(romPath!, containerStem);
        var index = GobArchive.ReadIndex(gfc, gob.LongLength);

        Assert.Equal(chunkCount, index.Chunks.Count);
        Assert.Equal(fileCount, index.Files.Count);
        Assert.Equal(gobLength, index.GobLength);
        Assert.Equal(gobLength, gob.LongLength);

        var entries = GobArchive.BuildFileList(index);
        Assert.Equal(fileCount, entries.Count);
        Assert.Equal(namedFiles, entries.Count(e => GobNames.TryResolve(e.Crc) != null));

        // Chains partition the chunk array: every chunk is reached exactly once.
        var owned = new HashSet<int>();
        for (var i = 0; i < index.Files.Count; i++)
        {
            foreach (var chunk in index.ChunksOf(i))
                Assert.True(owned.Add(chunk));
        }

        Assert.Equal(chunkCount, owned.Count);

        // Rebuild the entire container. ReadFile enforces the declared size, the
        // per-chunk checksum, and each compressed chunk's own trailer.
        var read = GobArchive.CreateReader(gob);
        long total = 0;
        byte[]? pinned = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var data = GobArchive.ReadFile(index, i, read);
            Assert.Equal(entries[i].Size, data.Length);
            total += data.Length;
            if (entries[i].FullName == pinnedPath)
                pinned = data;
        }

        Assert.Equal(totalBytes, total);

        Assert.NotNull(pinned);
        Assert.Equal(pinnedSize, pinned!.Length);
        Assert.Equal(pinnedSha, Convert.ToHexStringLower(SHA256.HashData(pinned)));
    }

    /// <summary>Pulls the <c>.gfc</c>/<c>.gob</c> pair out of a cart's Nitro filesystem.</summary>
    private static (byte[] Gfc, byte[] Gob) ReadContainerFromCart(string romPath, string containerStem)
    {
        using var fs = ArchiveFileSystem.TryOpen(romPath);
        Assert.NotNull(fs);
        var gfc = fs!.FindByPath(containerStem + ".gfc");
        var gob = fs.FindByPath(containerStem + ".gob");
        Assert.NotNull(gfc);
        Assert.NotNull(gob);
        return (fs.ReadEntry(gfc!), fs.ReadEntry(gob!));
    }

    // ---- synthetic .gfc/.gob pair -------------------------------------------

    /// <summary>
    ///     Five chunks over four files: [0] stored, [1] zlib, [2..4] a mixed three-chunk
    ///     chain, and a fourth file with no chunks at all.
    /// </summary>
    private static (byte[] Gfc, byte[] Gob) BuildSyntheticPair()
    {
        var stored = new List<byte[]>
        {
            StoredPayload,
            Compress(CompressiblePayload),
            ChainPartA,
            Compress(ChainPartB),
            ChainPartC
        };
        byte[] codecs = [GobCodec.Stored, GobCodec.Zlib, GobCodec.Stored, GobCodec.Zlib, GobCodec.Stored];
        ushort[] next = [GobIndex.ChainEnd, GobIndex.ChainEnd, 3, 4, GobIndex.ChainEnd];

        var gob = new List<byte>();
        var offsets = new uint[stored.Count];
        for (var i = 0; i < stored.Count; i++)
        {
            offsets[i] = (uint)gob.Count;
            gob.AddRange(stored[i]);
            while (gob.Count % 4 != 0) // the real container aligns chunks to 4
                gob.Add(0);
        }

        (uint Crc, uint Size, uint First)[] files =
        [
            (0x11111111, (uint)StoredPayload.Length, 0),
            (0x22222222, (uint)CompressiblePayload.Length, 1),
            (0x33333333, (uint)(ChainPartA.Length + ChainPartB.Length + ChainPartC.Length), 2),
            (0x44444444, 0, GobIndex.NoChunk)
        ];

        var gfc = new byte[16 + stored.Count * 20 + files.Length * 12];
        BinaryPrimitives.WriteUInt32BigEndian(gfc, GobIndex.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(4), (uint)gob.Count);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(8), (uint)stored.Count);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(12), (uint)files.Length);

        var checksumBase = 16 + stored.Count * 16;
        for (var i = 0; i < stored.Count; i++)
        {
            var record = gfc.AsSpan(16 + i * 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, (uint)stored[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], offsets[i]);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], next[i]);
            record[12] = codecs[i];
            BinaryPrimitives.WriteUInt32BigEndian(
                gfc.AsSpan(checksumBase + i * 4), GobCodec.Adler0(stored[i]));
        }

        var fileBase = checksumBase + stored.Count * 4;
        for (var i = 0; i < files.Length; i++)
        {
            var record = gfc.AsSpan(fileBase + i * 12);
            BinaryPrimitives.WriteUInt32BigEndian(record, files[i].Crc);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], files[i].Size);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], files[i].First);
        }

        return (gfc, gob.ToArray());
    }

    /// <summary>Builds a chunk in the container's framing: 78 9C + raw deflate + seed-0 Adler.</summary>
    private static byte[] Compress(byte[] payload)
    {
        using var body = new MemoryStream();
        using (var deflate = new DeflateStream(body, CompressionLevel.Optimal, true))
            deflate.Write(payload, 0, payload.Length);

        var result = new byte[2 + body.Length + 4];
        result[0] = 0x78;
        result[1] = 0x9C;
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(2));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), GobCodec.Adler0(payload));
        return result;
    }
}
