using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts files from Neversoft WAD archives using companion HED files.
///     Supports three HED variants:
///     - PS1 plaintext: name(null-term, align4) → offset(u32) → size(u32) ... 0xFF sentinel
///     - THUG plaintext: offset(u32) → size(u32) → name(null-term, align4) ... 0xFFFFFFFF sentinel
///     - Hashed: hash(u32) → offset(u32) → size(u32) ... all-zeros sentinel
///     Credit to JayRedFox: https://github.com/JayFoxRox/thps2-tools/blob/master/extract-hed-wad.py
/// </summary>
public static class WadArchive
{
    /// <summary>
    ///     Gets the companion HED file path for a WAD file.
    /// </summary>
    public static string GetHedPath(string wadPath)
    {
        var directory = Path.GetDirectoryName(wadPath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(wadPath);
        return Path.Combine(directory, nameWithoutExt + ".HED");
    }

    /// <summary>
    ///     Reads the file list from the HED companion file.
    ///     Supports both plaintext HED (null-terminated names) and hashed HED
    ///     (12-byte entries: uint32 hash, uint32 offset, uint32 size).
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string wadPath)
    {
        var hedPath = GetHedPath(wadPath);
        if (!File.Exists(hedPath))
            throw new FileNotFoundException($"Companion HED file not found: {hedPath}");

        using var stream = File.OpenRead(hedPath);
        using var reader = new BinaryReader(stream);

        var format = DetectHedFormat(stream);
        stream.Position = 0;

        return format switch
        {
            HedFormat.Ps1Plaintext => ReadPlaintextEntries(reader, stream.Length),
            HedFormat.ThugPlaintext => ReadThugPlaintextEntries(reader, stream.Length),
            _ => ReadHashedEntries(reader, stream.Length)
        };
    }

    /// <summary>
    ///     Detects HED format by probing the first bytes:
    ///     - PS1: bytes 0+ are printable ASCII (a filename first)
    ///     - THUG: bytes 0-7 are numeric (offset+size), byte 8+ is printable ASCII (a filename)
    ///     - Hashed: first 12 bytes are all numeric (hash+offset+size)
    /// </summary>
    private static HedFormat DetectHedFormat(Stream stream)
    {
        var probe = new byte[Math.Min(16, stream.Length)];
        stream.ReadExactly(probe.AsSpan(0, probe.Length));

        // Check if the first bytes are printable ASCII → PS1 plaintext
        var firstBytesAscii = true;
        for (var i = 0; i < Math.Min(8, probe.Length); i++)
        {
            if (probe[i] == 0)
            {
                firstBytesAscii = i >= 2;
                break;
            }

            if (probe[i] is < 0x20 or > 0x7E)
            {
                firstBytesAscii = false;
                break;
            }
        }

        if (firstBytesAscii)
            return HedFormat.Ps1Plaintext;

        // Not PS1 plaintext. Check if bytes 8+ are printable ASCII → THUG plaintext
        // (bytes 0-7 are offset+size, byte 8 starts the filename)
        if (probe.Length >= 12)
        {
            var byte8Ascii = true;
            for (var i = 8; i < Math.Min(16, probe.Length); i++)
            {
                if (probe[i] == 0)
                {
                    byte8Ascii = i > 8;
                    break;
                } // null after at least 1 char

                if (probe[i] is < 0x20 or > 0x7E)
                {
                    byte8Ascii = false;
                    break;
                }
            }

            if (byte8Ascii)
                return HedFormat.ThugPlaintext;
        }

        return HedFormat.Hashed;
    }

    private static List<ArchiveEntry> ReadPlaintextEntries(BinaryReader reader, long fileSize)
    {
        var entries = new List<ArchiveEntry>();

        while (reader.BaseStream.Position < fileSize - 7)
        {
            var name = ReadNullTerminatedString(reader);
            AlignStream(reader.BaseStream, 4);
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Size = size,
                Offset = offset
            });
        }

        // Verify terminator
        var terminator = reader.ReadByte();
        if (terminator != 0xFF)
            throw new InvalidDataException("Invalid HED file: missing 0xFF terminator");

        return entries;
    }

    /// <summary>
    ///     THUG-era HED: offset(u32) → size(u32) → name(null-term, align4), sentinel 0xFFFFFFFF.
    ///     Size bit 31 = NO_WAD flag (external file, not in WAD). Names may include directory paths.
    ///     THAW uses sector-based offsets (×2048) with disc region flags in the upper byte.
    /// </summary>
    private static List<ArchiveEntry> ReadThugPlaintextEntries(BinaryReader reader, long fileSize)
    {
        const uint NoWadFlag = 0x80000000;
        var entries = new List<ArchiveEntry>();

        while (reader.BaseStream.Position + 8 <= fileSize)
        {
            var offset = reader.ReadUInt32();

            // 0xFFFFFFFF sentinel marks end of entries
            if (offset == 0xFFFFFFFF)
                break;

            var rawSize = reader.ReadUInt32();
            var name = ReadNullTerminatedString(reader);
            AlignStream(reader.BaseStream, 4);

            var isNoWad = (rawSize & NoWadFlag) != 0;
            var size = rawSize & ~NoWadFlag;

            // Normalize path separators and strip leading slash
            name = name.Replace('\\', '/').TrimStart('/');

            // Split directory and filename
            var directory = "";
            var fileName = name;
            var lastSlash = name.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                directory = name[..lastSlash];
                fileName = name[(lastSlash + 1)..];
            }

            entries.Add(new ArchiveEntry
            {
                Name = fileName,
                Directory = directory,
                Size = size,
                Offset = offset,
                IsCompressed = isNoWad // repurpose flag to mark NO_WAD entries
            });
        }

        // Detect THAW sector-based offsets: if consecutive entries overlap when treated as
        // byte offsets but make sense as sector offsets, convert to byte offsets.
        // THAW HED stores disc sector numbers with region flags in the upper byte;
        // the lower 24 bits are the sector offset within the WAD (sector × 2048 = byte offset).
        if (IsSectorBased(entries))
        {
            for (var i = 0; i < entries.Count; i++)
                entries[i].Offset = (entries[i].Offset & 0x00FFFFFF) * 2048;
        }

        return entries;
    }

    /// <summary>
    ///     Detects THAW-style sector-based offsets by checking if consecutive entries
    ///     overlap when treated as byte offsets but are valid as sector offsets.
    /// </summary>
    private static bool IsSectorBased(List<ArchiveEntry> entries)
    {
        for (var i = 0; i < Math.Min(entries.Count - 1, 20); i++)
        {
            var curr = entries[i];
            var next = entries[i + 1];

            // Skip zero-size or NO_WAD entries
            if (curr.Size == 0 || next.Size == 0 || curr.IsCompressed || next.IsCompressed)
                continue;

            // Strip upper byte (disc region flags) for comparison
            var currSector = curr.Offset & 0x00FFFFFF;
            var nextSector = next.Offset & 0x00FFFFFF;

            // Entries must be in increasing order
            if (nextSector <= currSector)
                continue;

            // If next entry starts before current entry's data ends → byte offsets overlap
            if (nextSector < currSector + curr.Size)
            {
                // Verify it makes sense as sectors: next sector >= current + ceil(size/2048)
                var sectorsNeeded = (curr.Size + 2047) / 2048;
                if (nextSector >= currSector + sectorsNeeded)
                    return true;
            }

            // If we found a valid pair without overlap, byte offsets are fine
            if (nextSector >= currSector + curr.Size)
                return false;
        }

        return false;
    }

    private static List<ArchiveEntry> ReadHashedEntries(BinaryReader reader, long fileSize)
    {
        var entries = new List<ArchiveEntry>();

        while (reader.BaseStream.Position + 12 <= fileSize)
        {
            var hash = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();

            // Sentinel: all zeros marks end of entries
            if (hash == 0 && offset == 0 && size == 0)
                break;

            // Try to resolve the hash to a filename using Crc32Neversoft dictionary
            var resolvedName = HedDictionary.TryResolve(hash);
            var name = resolvedName ?? $"{hash:X8}.dat";

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Crc = hash,
                Size = size,
                Offset = offset
            });
        }

        return entries;
    }

    /// <summary>
    ///     Extracts all files from a WAD archive.
    /// </summary>
    public static void ExtractFiles(string wadPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        var entries = GetFileList(wadPath);
        var archiveName = Path.GetFileNameWithoutExtension(wadPath);

        using var wadStream = File.OpenRead(wadPath);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];

            // Skip NO_WAD entries (THUG-era external file references not in WAD)
            if (entry.IsCompressed && !string.IsNullOrEmpty(entry.Directory))
            {
                onFileExtracted?.Invoke(i + 1, entries.Count);
                continue;
            }

            // Skip entries that reference data past the end of the WAD
            // (can happen with truncated ISOs or dual-layer disc boundaries)
            if (entry.Offset + entry.Size > wadStream.Length)
            {
                onFileExtracted?.Invoke(i + 1, entries.Count);
                continue;
            }

            wadStream.Seek(entry.Offset, SeekOrigin.Begin);

            var exportPath = Path.Combine(outputDir, archiveName, entry.FullName);
            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            var data = new byte[entry.Size];
            wadStream.ReadExactly(data);

            File.WriteAllBytes(exportPath, data);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static void AlignStream(Stream stream, int alignment)
    {
        var remainder = stream.Position % alignment;
        if (remainder != 0)
            stream.Position += alignment - remainder;
    }

    private enum HedFormat
    {
        Ps1Plaintext,
        ThugPlaintext,
        Hashed
    }
}
