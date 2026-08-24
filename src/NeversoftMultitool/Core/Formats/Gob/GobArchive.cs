using Microsoft.Win32.SafeHandles;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Gob;

/// <summary>Reads <paramref name="destination" />.Length bytes of the .gob at <paramref name="offset" />.</summary>
public delegate void GobDataReader(long offset, Span<byte> destination);

/// <summary>
///     Archive view over the Vicarious Visions GOB container: a <c>.gob</c> data blob
///     paired with a <c>.gfc</c> index (<see cref="GobIndex" />). It holds essentially
///     all content in the three Tony Hawk Nintendo DS carts, which otherwise expose
///     nothing but this pair through their Nitro filesystem.
///
///     The <c>.gob</c> is the archive and the <c>.gfc</c> is its companion index — the
///     same way round as WAD/HED. A logical file is a CHAIN of independently-compressed
///     chunks rather than one byte range, so unlike the other container formats this one
///     cannot be served by <c>FileArchiveFileSystem</c> and has its own filesystem.
/// </summary>
public static class GobArchive
{
    /// <summary>Companion index path for a <c>.gob</c> (<c>main.gob</c> → <c>main.gfc</c>).</summary>
    public static string GetIndexPath(string gobPath)
    {
        var directory = Path.GetDirectoryName(gobPath) ?? "";
        var name = Path.GetFileName(gobPath);
        var extension = name.LastIndexOf(".gob", StringComparison.OrdinalIgnoreCase);
        var indexName = extension < 0 ? name + ".gfc" : name[..extension] + ".gfc" + name[(extension + 4)..];
        return Path.Combine(directory, indexName);
    }

    /// <summary>
    ///     True when this <c>.gob</c> has a companion <c>.gfc</c> that describes it: the
    ///     index magic, an index length that matches its own declared counts, and a
    ///     declared data length equal to this file's. That triple is settled from 16
    ///     bytes plus two file lengths, with no record walk.
    /// </summary>
    public static bool IsGobArchive(string gobPath)
    {
        try
        {
            var indexPath = GetIndexPath(gobPath);
            if (!File.Exists(indexPath) || !File.Exists(gobPath))
                return false;

            using var stream = File.OpenRead(indexPath);
            if (stream.Length < GobIndex.HeaderSize)
                return false;
            var header = new byte[GobIndex.HeaderSize];
            stream.ReadExactly(header);
            var expected = GobIndex.GetExpectedIndexLength(header, out var gobLength);
            return expected == stream.Length && gobLength == new FileInfo(gobPath).Length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Classification string for the detector ("GOB" or null).</summary>
    public static string? ClassifyArchive(string gobPath)
    {
        return IsGobArchive(gobPath) ? "GOB" : null;
    }

    /// <summary>Parses the companion index, checking it really describes this <c>.gob</c>.</summary>
    public static GobIndex ReadIndex(string gobPath)
    {
        var indexPath = GetIndexPath(gobPath);
        if (!File.Exists(indexPath))
            throw new InvalidDataException(
                $"GOB data file '{Path.GetFileName(gobPath)}' has no companion index " +
                $"'{Path.GetFileName(indexPath)}'.");
        return ReadIndex(File.ReadAllBytes(indexPath), new FileInfo(gobPath).Length);
    }

    /// <summary>Parses index bytes against a known <c>.gob</c> length.</summary>
    public static GobIndex ReadIndex(byte[] indexData, long gobLength)
    {
        var index = GobIndex.Parse(indexData);
        if (index.GobLength != gobLength)
            throw new InvalidDataException(
                $"GOB index describes a {index.GobLength}-byte data file, but the companion is {gobLength} bytes.");
        return index;
    }

    public static List<ArchiveEntry> GetFileList(string gobPath)
    {
        var index = ReadIndex(gobPath);
        using var handle = File.OpenHandle(
            gobPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        return BuildFileList(index, CreateReader(handle, RandomAccess.GetLength(handle)));
    }

    /// <summary>
    ///     One entry per logical file. <see cref="ArchiveEntry.Offset" /> is the FILE
    ///     INDEX, not a byte offset — a file's bytes are scattered across a chunk chain
    ///     — and <see cref="ArchiveEntry.Crc" /> carries the container's name key.
    ///     Files whose name is not in <see cref="GobNames" /> are named after that key,
    ///     which is stable across runs and across carts.
    ///     <para>
    ///         Given a <paramref name="sniff" /> reader, an unnamed file additionally
    ///         gets a real extension from its content (<see cref="GobContentTypes" />)
    ///         instead of a bare <c>.bin</c>. Only the file's FIRST CHUNK is decoded
    ///         for that, so listing a 14,606-file container stays cheap.
    ///     </para>
    /// </summary>
    public static List<ArchiveEntry> BuildFileList(GobIndex index, GobDataReader? sniff = null)
    {
        var entries = new List<ArchiveEntry>(index.Files.Count);
        for (var i = 0; i < index.Files.Count; i++)
        {
            var file = index.Files[i];
            var resolved = GobNames.TryResolve(file.NameCrc);
            string path;
            if (resolved != null)
            {
                path = GobNames.ToRelativePath(resolved);
            }
            else
            {
                var extension = sniff == null ? null : DetectExtension(index, i, sniff);
                path = $"{file.NameCrc:x8}{extension ?? ".bin"}";
            }

            var slash = path.LastIndexOf('/');
            entries.Add(new ArchiveEntry
            {
                Directory = slash > 0 ? path[..slash] : "",
                Name = slash >= 0 ? path[(slash + 1)..] : path,
                Size = file.UncompressedSize,
                Offset = i,
                Crc = file.NameCrc
            });
        }

        return entries;
    }

    /// <summary>
    ///     Content extension for one file, decoding only its first chunk. A rule that
    ///     needs the whole file (the fixed-size palette) is served because such a file
    ///     never spans more than one chunk.
    /// </summary>
    private static string? DetectExtension(GobIndex index, int fileIndex, GobDataReader read)
    {
        var file = index.Files[fileIndex];
        if (file.FirstChunk == GobIndex.NoChunk)
            return null;

        try
        {
            var chunk = index.Chunks[(int)file.FirstChunk];
            var stored = new byte[chunk.StoredSize];
            read(chunk.Offset, stored);
            var payload = GobCodec.Decode(chunk, stored, file.UncompressedSize, "GOB sniff");
            return GobContentTypes.Detect(payload);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            // A file that will not decode gets no extension rather than a wrong one;
            // reading it later reports the real error.
            return null;
        }
    }

    /// <summary>
    ///     Rebuilds one logical file: walk its chunk chain, decode each chunk, and
    ///     concatenate. The result must be exactly the declared size — the index, the
    ///     per-chunk checksums, and the compressed trailers all have to agree, so a
    ///     misparse cannot pass silently.
    /// </summary>
    public static byte[] ReadFile(GobIndex index, int fileIndex, GobDataReader read)
    {
        var file = index.Files[fileIndex];
        var what = $"GOB file {fileIndex} (0x{file.NameCrc:x8})";
        if (file.UncompressedSize > int.MaxValue)
            throw new InvalidDataException($"{what}: {file.UncompressedSize} bytes exceeds the 2 GB entry limit.");

        var result = new byte[file.UncompressedSize];
        var written = 0;
        foreach (var chunkIndex in index.ChunksOf(fileIndex))
        {
            var chunk = index.Chunks[chunkIndex];
            var stored = new byte[chunk.StoredSize];
            read(chunk.Offset, stored);
            var payload = GobCodec.Decode(chunk, stored, result.Length - written, $"{what} chunk {chunkIndex}");
            Buffer.BlockCopy(payload, 0, result, written, payload.Length);
            written += payload.Length;
        }

        if (written != result.Length)
            throw new InvalidDataException(
                $"{what}: chain produced {written} bytes but the index declares {result.Length}.");
        return result;
    }

    public static void ExtractFiles(
        string gobPath,
        string outputDir,
        Action<int, int>? onFileExtracted = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var index = ReadIndex(gobPath);

        using var handle = File.OpenHandle(
            gobPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        var read = CreateReader(handle, RandomAccess.GetLength(handle));
        var entries = BuildFileList(index, read);
        Directory.CreateDirectory(outputDir);

        var done = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var target = ArchiveExtractionPath.GetContainedPath(outputDir, entry.FullName, "GOB entry");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, ReadFile(index, (int)entry.Offset, read));
            onFileExtracted?.Invoke(++done, entries.Count);
        }
    }

    /// <summary>Chunk reader over an open <c>.gob</c> handle.</summary>
    public static GobDataReader CreateReader(SafeFileHandle handle, long length)
    {
        return (offset, destination) =>
        {
            if (offset < 0 || destination.Length > length - offset)
                throw new InvalidDataException(
                    $"GOB chunk range [{offset}, {offset + destination.Length}) is outside the " +
                    $"{length}-byte data file.");
            var read = 0;
            while (read < destination.Length)
            {
                var n = RandomAccess.Read(handle, destination[read..], offset + read);
                if (n == 0)
                    throw new EndOfStreamException("Unexpected end of GOB data file.");
                read += n;
            }
        };
    }

    /// <summary>Chunk reader over <c>.gob</c> bytes already in memory (nested opens).</summary>
    public static GobDataReader CreateReader(byte[] data)
    {
        return (offset, destination) =>
        {
            if (offset < 0 || destination.Length > data.LongLength - offset)
                throw new InvalidDataException(
                    $"GOB chunk range [{offset}, {offset + destination.Length}) is outside the " +
                    $"{data.LongLength}-byte data file.");
            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
        };
    }
}
