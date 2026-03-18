using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts files from Neversoft PAK archives used in THAW, THUG2, and Guitar Hero series (PS2).
///     Format: variable-size entry table terminated by QbKey("last") = 0xB524565F sentinel.
///     Entry sizes: 32 bytes (compact, no filename) or 192 bytes (full, with 160-byte filename field).
///     THAW shell/Create-A-Park archives use the same table shape but set an extra 0x10 flag bit and
///     store size before offset in the entry payload.
///     A single .pak file may contain multiple concatenated sub-PAKs, each with its own entry table.
///     Companion .pab files hold data when offsets exceed the .pak file size.
///     File types identified by QbKey hash of extension (e.g. QbKey(".ska") = 0x745DCD45).
///     Reference: Nanook/Queen-Bee PakHeaderItem.cs (GitHub).
/// </summary>
public static class PakArchive
{
    /// <summary>QbKey("last") — sentinel marking end of entry table.</summary>
    private const uint LastSentinel = 0xB524565F;

    /// <summary>Compact entry size (no filename): 8 × u32 = 32 bytes.</summary>
    private const int CompactEntrySize = 0x20;

    /// <summary>Full entry size (with filename): 32 + 160 = 192 bytes.</summary>
    private const int FullEntrySize = 0xC0;

    /// <summary>Flag bit indicating the entry has an embedded filename.</summary>
    private const uint HasFilenameFlag = 0x20;

    /// <summary>THAW shell/Create-A-Park entry variant bit.</summary>
    private const uint ThawShellFieldOrderFlag = 0x10;

    /// <summary>Known file type QbKey hashes → extension strings.</summary>
    private static readonly Dictionary<uint, string> KnownTypes = new()
    {
        [0x745DCD45] = ".ska", // QbKey(".ska")
        [0x5D796624] = ".sqb", // QbKey(".sqb")
        [0xA7F505C4] = ".qb", // QbKey(".qb")
        [0x9BCC234D] = ".mdl", // QbKey(".mdl")
        [0x64112E85] = ".skin", // QbKey(".skin")
        [0x8BFA5E8E] = ".tex", // QbKey(".tex")
        [0xDAD5E950] = ".img", // QbKey(".img")
        [0x72A6D78C] = ".col", // QbKey(".col")
        [0x365318B2] = ".scripts", // QbKey(".scripts")
        [0x559566CC] = ".dbg", // QbKey(".dbg")
        [0x7330095C] = ".ske", // QbKey(".ske")
        [0x1F3E0235] = ".anm", // QbKey(".anm")
        [0x9B22CA94] = ".cam", // QbKey(".cam")
        [0x98F2AA1D] = ".ped", // QbKey(".ped")
        [0x2C3B5ADC] = ".scn", // QbKey(".scn")
        [0x6C217288] = ".pak", // QbKey(".pak")
        [0x2B0A3095] = ".stex", // QbKey(".stex")
        [0x2F1A6A09] = ".shd", // QbKey(".shd")
        [0x7EA7357B] = ".mdl" // THAW shell/Create-A-Park geometry chunk
    };

    /// <summary>
    ///     Returns true if the file is a PAK archive (contains at least one QbKey("last") sentinel
    ///     preceded by a valid entry with flags == 0x00 or 0x20).
    /// </summary>
    public static bool IsPakArchive(string filePath)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            if (data.Length < CompactEntrySize)
                return false;

            // Find at least one "last" sentinel with a valid preceding entry
            for (var i = CompactEntrySize; i <= data.Length - 4; i += 4)
            {
                if (BitConverter.ToUInt32(data, i) != LastSentinel)
                    continue;

                // Check for a valid compact entry immediately before.
                if (LooksLikeEntryAt(data, i - CompactEntrySize, false))
                    return true;

                // Check for a valid full entry.
                if (LooksLikeEntryAt(data, i - FullEntrySize, true))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Reads all file entries from a PAK archive (across all sub-PAKs).
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string pakPath)
    {
        var data = File.ReadAllBytes(pakPath);
        var entries = new List<ArchiveEntry>();

        // Find all "last" sentinel positions (each marks the end of a sub-PAK entry table)
        var sentinelPositions = FindSentinelPositions(data);
        if (sentinelPositions.Count == 0)
            return entries;

        // Determine if a companion .pab file exists
        var pabPath = GetPabPath(pakPath);
        var hasPab = File.Exists(pabPath);

        foreach (var sentinelPos in sentinelPositions)
        {
            // Walk backward from sentinel to find the start of the entry table
            var tableEntries = WalkBackward(data, sentinelPos);
            foreach (var entry in tableEntries)
            {
                entry.IsCompressed = hasPab; // repurpose to indicate PAB companion
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    ///     Extracts all files from a PAK archive.
    /// </summary>
    public static void ExtractFiles(string pakPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        var entries = GetFileList(pakPath);
        if (entries.Count == 0)
            return;

        var archiveName = Path.GetFileNameWithoutExtension(pakPath);
        var pakData = File.ReadAllBytes(pakPath);

        // Load PAB companion if present
        var pabPath = GetPabPath(pakPath);
        byte[]? pabData = null;
        if (File.Exists(pabPath))
            pabData = File.ReadAllBytes(pabPath);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];
            if (entry.Size <= 0)
            {
                onFileExtracted?.Invoke(i + 1, entries.Count);
                continue;
            }

            // Determine data source: PAK or PAB
            byte[] sourceData;
            if (pabData != null && entry.Offset + entry.Size > pakData.Length)
                sourceData = pabData;
            else
                sourceData = pakData;

            if (entry.Offset + entry.Size > sourceData.Length)
            {
                onFileExtracted?.Invoke(i + 1, entries.Count);
                continue;
            }

            var exportPath = Path.Combine(outputDir, archiveName, entry.FullName);
            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            var fileData = new byte[entry.Size];
            Array.Copy(sourceData, entry.Offset, fileData, 0, (int)entry.Size);
            File.WriteAllBytes(exportPath, fileData);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }

    /// <summary>
    ///     Gets the companion PAB file path for a PAK file.
    ///     .pak.ps2 → .pab.ps2, .pak.xen → .pab.xen, etc.
    /// </summary>
    public static string GetPabPath(string pakPath)
    {
        // Handle double extensions like .pak.ps2
        var dir = Path.GetDirectoryName(pakPath) ?? "";
        var name = Path.GetFileName(pakPath);

        // Find ".pak" in the filename (case-insensitive)
        var pakIndex = name.IndexOf(".pak", StringComparison.OrdinalIgnoreCase);
        if (pakIndex < 0)
            return Path.ChangeExtension(pakPath, ".pab");

        var newName = name[..pakIndex] + ".pab" + name[(pakIndex + 4)..];
        return Path.Combine(dir, newName);
    }

    /// <summary>
    ///     Finds all positions of the QbKey("last") sentinel in the file data.
    /// </summary>
    private static List<int> FindSentinelPositions(byte[] data)
    {
        var positions = new List<int>();

        for (var i = 0; i <= data.Length - CompactEntrySize; i += 4)
        {
            if (BitConverter.ToUInt32(data, i) == LastSentinel)
            {
                // Validate: the "last" sentinel entry should have a recognizable structure.
                // Flags field at offset +0x1C should be 0 (sentinel entries have no flags).
                if (i + CompactEntrySize <= data.Length)
                    positions.Add(i);
            }
        }

        return positions;
    }

    /// <summary>
    ///     Walks backward from a "last" sentinel to find the entry table start,
    ///     then parses forward to collect all entries.
    ///     Flags are strictly validated (0x00/0x20 and the THAW shell 0x10/0x30 variants) to avoid false positives from
    ///     filename text data (e.g. "cutscenes\..." being misinterpreted as entries).
    /// </summary>
    private static List<ArchiveEntry> WalkBackward(byte[] data, int sentinelPos)
    {
        var (tableStart, usesAltFieldOrder) = FindTableStart(data, sentinelPos);
        return ParseEntries(data, tableStart, sentinelPos, usesAltFieldOrder);
    }

    private static (int Start, bool UsesAltFieldOrder) FindTableStart(byte[] data, int sentinelPos)
    {
        var pos = sentinelPos;
        var usesAltFieldOrder = false;

        while (pos > 0)
        {
            if (TryStepBack(data, pos, FullEntrySize, true, out var newPos, out var altFieldOrder) ||
                TryStepBack(data, pos, CompactEntrySize, false, out newPos, out altFieldOrder))
            {
                pos = newPos;
                usesAltFieldOrder |= altFieldOrder;
                continue;
            }

            break;
        }

        return (pos, usesAltFieldOrder);
    }

    private static bool TryStepBack(byte[] data, int pos, int entrySize, bool hasFilename, out int newPos,
        out bool usesAltFieldOrder)
    {
        newPos = pos;
        usesAltFieldOrder = false;
        if (pos < entrySize)
            return false;

        var candidatePos = pos - entrySize;
        if (!LooksLikeEntryAt(data, candidatePos, hasFilename))
            return false;

        var candidateFlags = BitConverter.ToUInt32(data, candidatePos + 0x1C);
        newPos = candidatePos;
        usesAltFieldOrder = UsesAltFieldOrder(candidateFlags);
        return true;
    }

    private static List<ArchiveEntry> ParseEntries(byte[] data, int start, int sentinelPos, bool usesAltFieldOrder)
    {
        var entries = new List<ArchiveEntry>();
        var current = start;

        while (current < sentinelPos && current + CompactEntrySize <= data.Length)
        {
            var fileType = BitConverter.ToUInt32(data, current);
            if (fileType == LastSentinel)
                break;

            var flags = BitConverter.ToUInt32(data, current + 0x1C);
            if (!IsValidPakFlags(flags))
                break;

            var fieldA = BitConverter.ToUInt32(data, current + 0x04);
            var fieldB = BitConverter.ToUInt32(data, current + 0x08);
            var length = usesAltFieldOrder ? fieldA : fieldB;
            var offset = usesAltFieldOrder ? fieldB : fieldA;
            var fullQbKey = BitConverter.ToUInt32(data, current + 0x10);
            var nameOnlyCrc = BitConverter.ToUInt32(data, current + 0x14);

            var hasFilename = HasEmbeddedFilename(flags);
            var entrySize = hasFilename ? FullEntrySize : CompactEntrySize;

            var (name, directory) = hasFilename && current + CompactEntrySize + 160 <= data.Length
                ? ParseFilename(data, current + CompactEntrySize)
                : GenerateName(fileType, nameOnlyCrc, fullQbKey, offset);

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Directory = directory,
                Size = length,
                Offset = offset,
                Crc = nameOnlyCrc
            });

            current += entrySize;
        }

        return entries;
    }

    private static bool LooksLikeEntryAt(byte[] data, int pos, bool hasFilename)
    {
        if (pos < 0 || pos + CompactEntrySize > data.Length)
            return false;

        var candidateType = BitConverter.ToUInt32(data, pos);
        var candidateFlags = BitConverter.ToUInt32(data, pos + 0x1C);
        return candidateType != 0 &&
               candidateType != LastSentinel &&
               IsValidPakFlags(candidateFlags) &&
               HasEmbeddedFilename(candidateFlags) == hasFilename;
    }

    private static bool IsValidPakFlags(uint flags)
    {
        return flags is 0 or ThawShellFieldOrderFlag or HasFilenameFlag
            or (ThawShellFieldOrderFlag | HasFilenameFlag);
    }

    private static bool HasEmbeddedFilename(uint flags)
    {
        return (flags & HasFilenameFlag) != 0;
    }

    private static bool UsesAltFieldOrder(uint flags)
    {
        return (flags & ThawShellFieldOrderFlag) != 0;
    }

    private static (string Name, string Directory) ParseFilename(byte[] data, int filenameOffset)
    {
        var fnBytes = data.AsSpan(filenameOffset, 160);
        var nullIdx = fnBytes.IndexOf((byte)0);
        var fnLength = nullIdx >= 0 ? nullIdx : 160;
        var fullPath = Encoding.ASCII.GetString(fnBytes[..fnLength]);

        fullPath = fullPath.Replace('\\', '/').TrimStart('/');
        var lastSlash = fullPath.LastIndexOf('/');
        return lastSlash >= 0
            ? (fullPath[(lastSlash + 1)..], fullPath[..lastSlash])
            : (fullPath, "");
    }

    private static (string Name, string Directory) GenerateName(
        uint fileType, uint nameOnlyCrc, uint fullQbKey, uint offset)
    {
        var extension = KnownTypes.GetValueOrDefault(fileType, $".{fileType:X8}");
        var crc = nameOnlyCrc != 0 ? nameOnlyCrc : fullQbKey;

        var resolved = QbKey.TryResolve(crc);
        if (resolved != null)
            return (resolved + extension, "");

        var identifier = crc != 0 ? $"{crc:X8}" : $"{offset:X8}";
        return (identifier + extension, "");
    }
}
