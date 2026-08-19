using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts files from Neversoft PAK archives used in THAW and related PS2-era titles.
///     Format: variable-size entry table terminated by QbKey("last") = 0xB524565F sentinel.
///     Entry sizes: 32 bytes (compact, no filename) or 192 bytes (full, with 160-byte filename field).
///     The 0x20 flag bit selects the full-entry layout. Lower flag bits are additive archive-family markers.
///     A single .pak file may contain multiple concatenated sub-PAKs, each with its own entry table.
///     File types identified by QbKey hash of extension (e.g. QbKey(".ska") = 0x745DCD45).
///     Reference: Nanook/Queen-Bee PakHeaderItem.cs + PakEditor.cs (GitHub).
///     Entry data offsets are RELATIVE TO THE ENTRY'S OWN HEADER POSITION, not absolute
///     (Queen-Bee: HeaderStart + FileOffset). When the resolved position exceeds the .pak
///     file size, the data lives in the companion .pab file at (resolved − pak length).
///     Verified 2026-07-09 with payload signature checks: THAW PS2 12,120 and THAW PC
///     12,756 hits — zero mismatches under this rule, versus wholesale failures when
///     offsets are read as absolute (e.g. qb.pak.ps2: 266/266 relative vs 2/266 absolute).
///     THAW GameCube ships the same format big-endian as .apk.ngc / .pak.ngc (detected by
///     sentinel byte order). GC entries use flag 0x80000000 to mark data resident in the
///     archive itself (header-relative offsets like LE); entries WITHOUT it live in the
///     companion .mpk.ngc at RAW stored offsets (the first companion entry stores 0).
///     Self-contained archives pair with a 32-byte 0xAB/0xCD-fill .mpk.ngc placeholder stub.
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

    /// <summary>Known additive family flag bits that still use the standard entry layout.</summary>
    private const uint SupportedVariantFlags = 0x13;

    /// <summary>GC flag bit: entry data lives in the archive itself, not the companion.</summary>
    private const uint DataInPakFlag = 0x80000000;

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
        [0x7EA7357B] = ".mdl", // THAW shell/Create-A-Park geometry chunk
        // Resolved 2026-07-10 (QbKey-bruted, cross-checked against Queen-Bee
        // PakHeaderItem.cs known-extension list):
        [0xA7DEA591] = ".pfx", // particle effects (no converter planned)
        [0xFF2D0E91] = ".gap", // gap definitions
        [0x199F902B] = ".nav", // navigation data
        [0x91E1028D] = ".rnb", // QbKey(".rnb")
        [0x33180544] = ".rnb_lvl", // QbKey(".rnb_lvl")
        [0x4A2E1FA0] = ".rnb_mdl", // QbKey(".rnb_mdl")
        [0x7E1ABC70] = ".fnt", // fonts
        [0x9DE9087F] = ".fam", // appearance config (text)
        [0x689028A5] = ".pimg", // QbKey(".pimg")
        [0x6290993B] = ".mcol", // movable collision
        [0x8A20E0F8] = ".hkc", // QbKey(".hkc")
        [0xB065C9A2] = ".imv", // QbKey(".imv")
        [0x66AEDA37] = ".mdv", // QbKey(".mdv")
        [0x4BC1E85E] = ".mqb", // QbKey(".mqb")
        [0x49875607] = ".nqb", // QbKey(".nqb")
        [0xB0A32C18] = ".oba", // QbKey(".oba")
        [0xDBD7EFC9] = ".perf", // QbKey(".perf")
        [0x02200857] = ".pimv", // QbKey(".pimv")
        [0xE20F226C] = ".png", // QbKey(".png")
        [0x6613EACD] = ".rag", // ragdoll
        [0x7BA4FAA9] = ".raw", // QbKey(".raw")
        [0x4995F5EF] = ".rgn", // QbKey(".rgn")
        [0x3F57C28A] = ".scv", // QbKey(".scv")
        [0x777DB6D3] = ".skiv", // QbKey(".skiv")
        [0x43904241] = ".table", // QbKey(".table")
        [0xEA151F1C] = ".tvx", // QbKey(".tvx")
        [0x0A6808D4] = ".wav", // QbKey(".wav")
        [0xFDC939B7] = ".fnc", // QbKey(".fnc")
        [0x9014DD5C] = ".fnv", // QbKey(".fnv")
        [0x4AE71C19] = ".clt", // QbKey(".clt")
        [0x94F3F11B] = ".jam", // QbKey(".jam")
        [0xA9D5BC8F] = ".note", // QbKey(".note")
        [0xCD452536] = ".qs", // QbKey(".qs")
        [0xBBB0E344] = ".trkobj", // QbKey(".trkobj")
        [0x50E3F99F] = ".xml", // QbKey(".xml")
        // QbKey("unknown") — the pak builder's fallback for unclassified files.
        // In THAW PC these are plain RIFF/WAVE zone-ambience sounds; extraction
        // sniffs RIFF payloads and names them .wav.
        [0x52D95838] = ".unknown"
    };

    private static uint ReadU32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : BitConverter.ToUInt32(data, offset);
    }

    /// <summary>
    ///     Returns true if the file is a PAK archive (contains at least one QbKey("last") sentinel
    ///     preceded by a valid entry). Detects both little-endian (PS2) and big-endian (GC) layouts.
    /// </summary>
    public static bool IsPakArchive(string filePath)
    {
        try
        {
            return IsPakArchive(File.ReadAllBytes(filePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>In-memory variant of <see cref="IsPakArchive(string)" />.</summary>
    public static bool IsPakArchive(byte[] data)
    {
        return IsPakArchive(data, false) || IsPakArchive(data, true);
    }

    private static bool IsPakArchive(byte[] data, bool bigEndian)
    {
        if (data.Length < CompactEntrySize)
            return false;

        for (var i = CompactEntrySize; i <= data.Length - 4; i += 4)
        {
            if (ReadU32(data, i, bigEndian) != LastSentinel)
                continue;

            if (LooksLikeEntryAt(data, i - CompactEntrySize, false, i, bigEndian))
                return true;

            if (LooksLikeEntryAt(data, i - FullEntrySize, true, i, bigEndian))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Reads all file entries from a PAK archive (across all sub-PAKs).
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string pakPath)
    {
        return GetFileListCore(File.ReadAllBytes(pakPath), HasCompanionData(pakPath));
    }

    /// <summary>
    ///     In-memory variant. Callers that have PAK bytes without a filesystem path
    ///     pass <paramref name="hasPab" /> = false (no companion PAB detection).
    /// </summary>
    public static List<ArchiveEntry> GetFileList(byte[] data, bool hasPab = false)
    {
        return GetFileListCore(data, hasPab);
    }

    private static List<ArchiveEntry> GetFileListCore(byte[] data, bool hasPab)
    {
        var entries = new List<ArchiveEntry>();

        var (sentinelPositions, bigEndian) = FindSentinelPositions(data);
        if (sentinelPositions.Count == 0)
            return entries;

        foreach (var sentinelPos in sentinelPositions)
        {
            var tableEntries = WalkBackward(data, sentinelPos, bigEndian);
            foreach (var entry in tableEntries)
            {
                entry.IsCompressed = hasPab;
                entries.Add(entry);
            }
        }

        DisambiguateCollidingNames(entries);
        return entries;
    }

    /// <summary>
    ///     Mission worldzone paks stamp ONE shared name CRC (0x6F980DC3 → "mission")
    ///     on every unnamed entry, so generated names collide and extraction silently
    ///     overwrites siblings (m_zhogaps13_gameplay loses 13 of 22 unnamed entries).
    ///     Colliding (directory, name) groups get their pak offset spliced in before
    ///     the first extension dot — EVERY member, so the result is independent of
    ///     enumeration order and identical across GetFileList/GetTypedEntries. Unique
    ///     names (all zone paks — their unnamed entries are offset-named already)
    ///     are untouched.
    /// </summary>
    private static void DisambiguateCollidingNames(List<ArchiveEntry> entries)
    {
        foreach (var group in entries
                     .GroupBy(static e => $"{e.Directory}/{e.Name}", StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2)
                continue;

            var ordinal = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in members)
            {
                var dot = entry.Name.IndexOf('.');
                var stem = dot >= 0 ? entry.Name[..dot] : entry.Name;
                var extension = dot >= 0 ? entry.Name[dot..] : "";
                var candidate = $"{stem}_{entry.Offset:X8}{extension}";
                while (!seen.Add(candidate))
                    candidate = $"{stem}_{entry.Offset:X8}_{ordinal++}{extension}";
                entry.Name = candidate;
            }
        }
    }

    /// <summary>
    ///     Reads all entries with their raw file-type QbKey hash, preserving PAK-entry-table order.
    ///     Callers that need neighbor relationships between entries (e.g. pairing a .91E1028D
    ///     worldzone placement file with the preceding .mdl) should use this method.
    /// </summary>
    public static List<(uint TypeHash, ArchiveEntry Entry)> GetTypedEntries(string pakPath)
    {
        return GetTypedEntriesCore(File.ReadAllBytes(pakPath), HasCompanionData(pakPath));
    }

    /// <summary>In-memory variant of <see cref="GetTypedEntries(string)" />.</summary>
    public static List<(uint TypeHash, ArchiveEntry Entry)> GetTypedEntries(byte[] data, bool hasPab = false)
    {
        return GetTypedEntriesCore(data, hasPab);
    }

    private static List<(uint TypeHash, ArchiveEntry Entry)> GetTypedEntriesCore(byte[] data, bool hasPab)
    {
        var typedEntries = new List<(uint TypeHash, ArchiveEntry Entry)>();

        var (sentinelPositions, bigEndian) = FindSentinelPositions(data);
        if (sentinelPositions.Count == 0)
            return typedEntries;

        foreach (var sentinelPos in sentinelPositions)
        {
            var tableStart = FindTableStart(data, sentinelPos, bigEndian);
            ParseTypedEntries(data, tableStart, sentinelPos, hasPab, bigEndian, typedEntries);
        }

        // Same collision rule as GetFileList so both enumerations agree on names.
        DisambiguateCollidingNames(typedEntries.Select(static t => t.Entry).ToList());
        return typedEntries;
    }

    private static void ParseTypedEntries(
        byte[] data, int tableStart, int sentinelPos, bool hasPab, bool bigEndian,
        List<(uint TypeHash, ArchiveEntry Entry)> output)
    {
        var current = tableStart;
        while (current < sentinelPos && current + CompactEntrySize <= data.Length)
        {
            var parsed = TryReadTypedEntry(data, current, sentinelPos, hasPab, bigEndian);
            if (parsed is null)
                break;

            output.Add(parsed.Value.Typed);
            current += parsed.Value.EntrySize;
        }
    }

    private static ((uint TypeHash, ArchiveEntry Entry) Typed, int EntrySize)? TryReadTypedEntry(
        byte[] data, int current, int sentinelPos, bool hasPab, bool bigEndian)
    {
        var fileType = ReadU32(data, current, bigEndian);
        if (fileType == LastSentinel)
            return null;

        var flags = ReadU32(data, current + 0x1C, bigEndian);
        if (!IsValidPakFlags(flags))
            return null;

        var offset = ReadU32(data, current + 0x04, bigEndian);
        var length = ReadU32(data, current + 0x08, bigEndian);
        var inCompanion = IsCompanionResident(flags, bigEndian);
        var resolved = inCompanion ? offset : current + offset;
        if (length == 0 || (!inCompanion && resolved <= sentinelPos))
            return null;

        var hasFilename = HasEmbeddedFilename(flags);
        var entrySize = hasFilename ? FullEntrySize : CompactEntrySize;
        var nameOnlyCrc = ReadNameCrc(data, current, bigEndian);

        var (name, directory) = hasFilename && current + CompactEntrySize + 160 <= data.Length
            ? ParseFilename(data, current + CompactEntrySize)
            : GenerateName(fileType, nameOnlyCrc, (uint)resolved, bigEndian);

        var entry = new ArchiveEntry
        {
            Name = name,
            Directory = directory,
            Size = length,
            Offset = resolved,
            Crc = nameOnlyCrc,
            IsCompressed = hasPab,
            InCompanion = inCompanion
        };

        return ((fileType, entry), entrySize);
    }

    /// <summary>
    ///     Extracts all files from a PAK archive.
    /// </summary>
    public static void ExtractFiles(string pakPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var entries = GetFileList(pakPath);
        if (entries.Count == 0)
            return;

        var archiveName = Path.GetFileNameWithoutExtension(pakPath);
        var pakData = File.ReadAllBytes(pakPath);

        // Load companion data file if present (.pab for PS2, .mpk.ngc for GC)
        var pabPath = GetPabPath(pakPath);
        byte[]? pabData = null;
        if (HasCompanionData(pakPath))
            pabData = File.ReadAllBytes(pabPath);

        var outputRoot = Path.GetFullPath(outputDir);
        var extractionRoot = ArchiveExtractionPath.GetContainedPath(
            outputRoot, archiveName, "PAK extraction directory");
        var exportPaths = new string?[entries.Count];
        var entrySources = new byte[]?[entries.Count];
        var entryPositions = new long[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Size <= 0)
                throw new InvalidDataException($"PAK entry '{entry.FullName}' has invalid size {entry.Size}.");
            if (!TryResolveEntryData(entry, pakData, pabData, out var sourceData, out var position))
            {
                throw new InvalidDataException(
                    $"PAK entry '{entry.FullName}' data range starting at {entry.Offset} with length {entry.Size} " +
                    "is unavailable or outside the PAK/companion data.");
            }

            exportPaths[i] = ArchiveExtractionPath.GetContainedPath(
                extractionRoot, entry.FullName, "PAK entry");
            entrySources[i] = sourceData;
            entryPositions[i] = position;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];
            var sourceData = entrySources[i]!;
            var position = entryPositions[i];

            var fileData = new byte[entry.Size];
            Array.Copy(sourceData, position, fileData, 0, (int)entry.Size);

            // QbKey("unknown")-typed entries are the pak builder's fallback for
            // unclassified files; sniff RIFF/WAVE payloads so THAW PC zone
            // ambience extracts directly playable.
            var exportPath = exportPaths[i]!;
            if (entry.FullName.EndsWith(".unknown", StringComparison.OrdinalIgnoreCase)
                && fileData.Length >= 12
                && fileData.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && fileData.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                exportPath = Path.ChangeExtension(exportPath, ".wav");
            }

            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            File.WriteAllBytes(exportPath, fileData);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }

    private static bool TryResolveEntryData(
        ArchiveEntry entry, byte[] pakData, byte[]? pabData, out byte[] sourceData, out long position)
    {
        sourceData = pakData;
        position = entry.Offset;
        if (entry.InCompanion)
        {
            if (pabData == null)
                return false;
            sourceData = pabData;
        }
        else if (!IsRangeWithin(position, entry.Size, pakData.LongLength) && pabData != null)
        {
            sourceData = pabData;
            position -= pakData.Length;
        }

        return IsRangeWithin(position, entry.Size, sourceData.LongLength);
    }

    private static bool IsRangeWithin(long position, long size, long length)
    {
        return position >= 0 && size >= 0 && position <= length && size <= length - position;
    }

    /// <summary>
    ///     Gets the companion data file path for a PAK file.
    ///     .pak.ps2 → .pab.ps2, .pak.xen → .pab.xen; GC .apk.ngc → .mpk.ngc.
    /// </summary>
    public static string GetPabPath(string pakPath)
    {
        // Handle double extensions like .pak.ps2
        var dir = Path.GetDirectoryName(pakPath) ?? "";
        var name = Path.GetFileName(pakPath);

        var apkIndex = name.IndexOf(".apk", StringComparison.OrdinalIgnoreCase);
        if (apkIndex >= 0)
            return Path.Combine(dir, name[..apkIndex] + ".mpk" + name[(apkIndex + 4)..]);

        // Find ".pak" in the filename (case-insensitive)
        var pakIndex = name.IndexOf(".pak", StringComparison.OrdinalIgnoreCase);
        if (pakIndex < 0)
            return Path.ChangeExtension(pakPath, ".pab");

        var newName = name[..pakIndex] + ".pab" + name[(pakIndex + 4)..];
        return Path.Combine(dir, newName);
    }

    /// <summary>
    ///     True when a real companion data file sits next to the archive.
    ///     GC .mpk.ngc placeholders (32-byte fill) do not count.
    /// </summary>
    private static bool HasCompanionData(string pakPath)
    {
        var pabPath = GetPabPath(pakPath);
        if (!File.Exists(pabPath))
            return false;

        return new FileInfo(pabPath).Length > CompactEntrySize;
    }

    /// <summary>
    ///     Finds all positions of the QbKey("last") sentinel in the file data.
    ///     Tries little-endian (PS2/PC) first, then big-endian (GC .apk.ngc/.pak.ngc).
    /// </summary>
    private static (List<int> Positions, bool BigEndian) FindSentinelPositions(byte[] data)
    {
        foreach (var bigEndian in (ReadOnlySpan<bool>)[false, true])
        {
            var positions = new List<int>();
            for (var i = 0; i <= data.Length - CompactEntrySize; i += 4)
            {
                if (ReadU32(data, i, bigEndian) == LastSentinel && i + CompactEntrySize <= data.Length)
                    positions.Add(i);
            }

            if (positions.Count > 0)
                return (positions, bigEndian);
        }

        return ([], false);
    }

    /// <summary>
    ///     Walks backward from a "last" sentinel to find the entry table start,
    ///     then parses forward to collect all entries.
    ///     Validation stays conservative so raw-data families are not misclassified as archives.
    /// </summary>
    private static List<ArchiveEntry> WalkBackward(byte[] data, int sentinelPos, bool bigEndian)
    {
        var tableStart = FindTableStart(data, sentinelPos, bigEndian);
        return ParseEntries(data, tableStart, sentinelPos, bigEndian);
    }

    private static int FindTableStart(byte[] data, int sentinelPos, bool bigEndian)
    {
        var pos = sentinelPos;

        while (pos > 0)
        {
            if (TryStepBack(data, pos, FullEntrySize, true, bigEndian, out var newPos) ||
                TryStepBack(data, pos, CompactEntrySize, false, bigEndian, out newPos))
            {
                pos = newPos;
                continue;
            }

            break;
        }

        return pos;
    }

    private static bool TryStepBack(
        byte[] data, int pos, int entrySize, bool hasFilename, bool bigEndian, out int newPos)
    {
        newPos = pos;
        if (pos < entrySize)
            return false;

        var candidatePos = pos - entrySize;
        if (!LooksLikeEntryAt(data, candidatePos, hasFilename, pos, bigEndian))
            return false;

        newPos = candidatePos;
        return true;
    }

    private static List<ArchiveEntry> ParseEntries(byte[] data, int start, int sentinelPos, bool bigEndian)
    {
        var entries = new List<ArchiveEntry>();
        var current = start;

        while (current < sentinelPos && current + CompactEntrySize <= data.Length)
        {
            var fileType = ReadU32(data, current, bigEndian);
            if (fileType == LastSentinel)
                break;

            var flags = ReadU32(data, current + 0x1C, bigEndian);
            if (!IsValidPakFlags(flags))
                break;

            var offset = ReadU32(data, current + 0x04, bigEndian);
            var length = ReadU32(data, current + 0x08, bigEndian);
            var nameOnlyCrc = ReadNameCrc(data, current, bigEndian);

            var hasFilename = HasEmbeddedFilename(flags);
            var entrySize = hasFilename ? FullEntrySize : CompactEntrySize;
            var inCompanion = IsCompanionResident(flags, bigEndian);
            var resolved = inCompanion ? offset : current + offset;
            if (length == 0 || (!inCompanion && resolved <= sentinelPos))
                break;

            var (name, directory) = hasFilename && current + CompactEntrySize + 160 <= data.Length
                ? ParseFilename(data, current + CompactEntrySize)
                : GenerateName(fileType, nameOnlyCrc, (uint)resolved, bigEndian);

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Directory = directory,
                Size = length,
                Offset = resolved,
                Crc = nameOnlyCrc,
                InCompanion = inCompanion
            });

            current += entrySize;
        }

        return entries;
    }

    private static bool LooksLikeEntryAt(byte[] data, int pos, bool hasFilename, int nextPos, bool bigEndian)
    {
        if (pos < 0 || pos + CompactEntrySize > data.Length)
            return false;

        var candidateType = ReadU32(data, pos, bigEndian);
        var candidateFlags = ReadU32(data, pos + 0x1C, bigEndian);
        if (candidateType == 0 ||
            candidateType == LastSentinel ||
            !IsValidPakFlags(candidateFlags) ||
            HasEmbeddedFilename(candidateFlags) != hasFilename)
        {
            return false;
        }

        var offset = ReadU32(data, pos + 0x04, bigEndian);
        var length = ReadU32(data, pos + 0x08, bigEndian);
        if (length == 0)
            return false;

        // Offsets are relative to the entry header; resolved data must land past
        // the following table region (data always follows the sentinel). GC
        // companion-resident entries store raw .mpk.ngc positions (0 is valid).
        if (!IsCompanionResident(ReadU32(data, pos + 0x1C, bigEndian), bigEndian) &&
            pos + offset <= nextPos)
            return false;

        if (hasFilename && !LooksLikeFilename(data, pos + CompactEntrySize))
        {
            return false;
        }

        return true;
    }

    private static bool IsValidPakFlags(uint flags)
    {
        return (flags & ~(SupportedVariantFlags | HasFilenameFlag | DataInPakFlag)) == 0;
    }

    private static bool HasEmbeddedFilename(uint flags)
    {
        return (flags & HasFilenameFlag) != 0;
    }

    /// <summary>
    ///     GC entries without the 0x80000000 flag store their data in the companion
    ///     .mpk.ngc file. Little-endian archives never set the bit and keep the
    ///     legacy overrun-based companion fallback.
    /// </summary>
    private static bool IsCompanionResident(uint flags, bool bigEndian)
    {
        return bigEndian && (flags & DataInPakFlag) == 0;
    }

    private static bool LooksLikeFilename(byte[] data, int filenameOffset)
    {
        if (filenameOffset < 0 || filenameOffset + 160 > data.Length)
            return false;

        var fnBytes = data.AsSpan(filenameOffset, 160);
        var nullIdx = fnBytes.IndexOf((byte)0);
        if (nullIdx <= 0)
            return false;

        var hasDirectorySeparator = false;
        var hasDot = false;
        for (var i = 0; i < nullIdx; i++)
        {
            var b = fnBytes[i];
            if (b is < 0x20 or > 0x7E)
                return false;

            if (b is (byte)'\\' or (byte)'/')
                hasDirectorySeparator = true;
            else if (b == (byte)'.')
                hasDot = true;
        }

        return hasDirectorySeparator && hasDot;
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

    /// <summary>
    ///     Best name key for an entry. PS2 entries carry the short-name CRC at +0x14
    ///     (full-path QbKey at +0x10 as fallback); GC entries put the name QbKey at +0x0C.
    /// </summary>
    private static uint ReadNameCrc(byte[] data, int entryPos, bool bigEndian)
    {
        if (bigEndian)
            return ReadU32(data, entryPos + 0x0C, bigEndian);

        var nameOnlyCrc = ReadU32(data, entryPos + 0x14, bigEndian);
        return nameOnlyCrc != 0 ? nameOnlyCrc : ReadU32(data, entryPos + 0x10, bigEndian);
    }

    private static (string Name, string Directory) GenerateName(
        uint fileType, uint nameCrc, uint offset, bool bigEndian)
    {
        var extension = KnownTypes.GetValueOrDefault(fileType, $".{fileType:X8}");
        // GC archives name their payloads with the platform suffix (e.g. .qb.ngc),
        // which also keeps extracted files routing to the big-endian parsers.
        if (bigEndian)
            extension += ".ngc";

        var resolved = QbKey.QbKey.TryResolve(nameCrc);
        if (resolved != null)
            return (resolved + extension, "");

        var identifier = nameCrc != 0 ? $"{nameCrc:X8}" : $"{offset:X8}";
        return (identifier + extension, "");
    }
}
