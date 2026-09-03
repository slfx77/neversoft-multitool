using System.Collections.Concurrent;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Finds the VRAM payload holding a PS3 texture dictionary's pixels.
/// </summary>
/// <remarks>
///     A PS3 <c>.tex.ps3</c> is metadata only; the surfaces live in a sibling
///     <c>.tvx.ps3</c>/<c>.vtex.ps3</c>. Resolution is deliberately proof based:
///     exact owner names win, byte-identical dictionaries can reuse only one
///     byte-identical payload within the same <c>PS3_GAME/USRDIR/DATA</c> tree,
///     and split PAK entries pair only when the complete typed populations agree
///     by preserved table index, name CRC, logical stem, and exact declared size.
///     No filename proximity, payload size, or cross-build first-match rule is
///     used.
/// </remarks>
public static class NextGenVramTwinLocator
{
    internal const uint TexDescriptorType = 0x8BFA5E8E;
    internal const uint StexDescriptorType = 0x2B0A3095;
    internal const uint VtexPayloadType = 0x1CD4C0A7;
    internal const uint VstexPayloadType = 0x692F8667;

    private const string PakSuffix = ".pak.ps3";
    private const string VramPakSuffix = "_vram.pak.ps3";

    private static readonly ConcurrentDictionary<string, Lazy<ArchivePairCatalog>> ArchiveCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<ContentTwinCatalog>> ContentCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a filesystem dictionary's proven payload, or null.</summary>
    public static byte[]? TryLoad(string dictionaryPath, byte[] dictionaryData)
    {
        return ResolvePayload(dictionaryPath, dictionaryData).Bytes;
    }

    /// <summary>
    ///     Loads a payload for either a filesystem dictionary or an entry opened
    ///     directly from its main PAK.
    /// </summary>
    public static byte[]? TryLoad(AssetSource source, byte[] dictionaryData)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dictionaryData);

        if (source.FileSystemPath is { } path)
            return TryLoad(path, dictionaryData);

        if (!IsNonEmptyPs3Dictionary(dictionaryData, out _))
            return null;

        var exactPayloads = new List<byte[]>();
        foreach (var candidateName in NextGenTexFile.GetVramTwinCandidateFileNames(source.EntryName))
        {
            byte[]? bytes;
            try
            {
                bytes = source.TryReadCompanion(candidateName);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or UnauthorizedAccessException or ArgumentException
                                       or OverflowException)
            {
                return null;
            }

            if (bytes == null) continue;
            exactPayloads.Add(bytes);
        }

        if (exactPayloads.Count != 0)
        {
            var first = exactPayloads[0];
            return exactPayloads.Skip(1).All(
                candidate => candidate.AsSpan().SequenceEqual(first))
                ? first
                : null;
        }

        if (source is not ArchiveAssetSource archiveSource
            || !TryGetArchivePairPaths(archiveSource.Backend.ArchivePath,
                out var mainArchive, out var vramArchive)
            || !TryGetArchivePayloadReference(
                mainArchive,
                vramArchive,
                archiveSource.Entry.FullName,
                dictionaryData,
                out var reference,
                out _))
        {
            return null;
        }

        return TryReadReference(reference);
    }

    /// <summary>
    ///     Resolves a physical twin path when one exists. Raw PAK-only entries
    ///     intentionally return null here; <see cref="TryLoad(string,byte[])" />
    ///     can still read their exact byte range.
    /// </summary>
    public static string? TryResolve(string dictionaryPath, byte[] dictionaryData)
    {
        return ResolvePayload(dictionaryPath, dictionaryData).FileSystemPath;
    }

    /// <summary>Detailed resolution used by corpus gates.</summary>
    internal static NextGenVramPayloadResolution ResolvePayload(
        string dictionaryPath,
        byte[] dictionaryData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryPath);
        ArgumentNullException.ThrowIfNull(dictionaryData);

        if (!IsNonEmptyPs3Dictionary(dictionaryData, out _))
            return NextGenVramPayloadResolution.Missing;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(dictionaryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException)
        {
            return NextGenVramPayloadResolution.Missing;
        }

        var exactCandidates = GetExistingExactCandidates(fullPath);
        if (exactCandidates.Count != 0)
        {
            var exactPayloads = new List<(PayloadReference Reference, byte[] Bytes)>();
            foreach (var candidate in exactCandidates)
            {
                var readBytes = TryReadReference(candidate);
                if (readBytes == null)
                    return NextGenVramPayloadResolution.Missing;
                exactPayloads.Add((candidate, readBytes));
            }

            // Every physically present exact-name candidate is an ownership
            // assertion. Multiple spellings (.tvx/.vtex/hash-named) are usable
            // only when their complete bytes agree. A sole short payload is
            // deliberately returned so Parse reports truncation; disagreement
            // or an unreadable owner fails closed without content borrowing.
            var (reference, selectedBytes) = exactPayloads[0];
            if (exactPayloads.Skip(1).Any(
                    candidate => !candidate.Bytes.AsSpan().SequenceEqual(selectedBytes)))
            {
                return NextGenVramPayloadResolution.Missing;
            }

            return NextGenVramPayloadResolution.Success(
                selectedBytes,
                reference.Location,
                reference.FileSystemPath,
                NextGenVramPayloadSource.ExactName);
        }

        if (TryGetContentTwinReference(fullPath, dictionaryData, out var contentReference))
        {
            var bytes = TryReadReference(contentReference);
            if (bytes != null)
            {
                return NextGenVramPayloadResolution.Success(
                    bytes,
                    contentReference.Location,
                    contentReference.FileSystemPath,
                    NextGenVramPayloadSource.IdenticalDictionary);
            }
        }

        if (TryFindExtractedPakOwner(fullPath, out var extractedRoot, out var entryPath,
                out var mainArchive, out var vramArchive)
            && TryGetArchivePayloadReference(
                mainArchive,
                vramArchive,
                entryPath,
                dictionaryData,
                out var archiveReference,
                out var archiveSource))
        {
            var bytes = TryReadReference(archiveReference);
            if (bytes != null)
            {
                var extractedPath = TryGetExtractedPayloadPath(
                    extractedRoot, vramArchive, archiveReference.EntryName);
                return NextGenVramPayloadResolution.Success(
                    bytes,
                    archiveReference.Location,
                    extractedPath,
                    archiveSource);
            }
        }

        return NextGenVramPayloadResolution.Missing;
    }

    private static bool IsNonEmptyPs3Dictionary(byte[] data, out long required)
    {
        required = 0;
        if (!NextGenTexFile.TryProbe(data, out var isPs3, out _) || !isPs3)
            return false;

        required = NextGenTexFile.GetRequiredPayloadLength(data);
        return required > 0;
    }

    private static IReadOnlyList<PayloadReference> GetExistingExactCandidates(
        string dictionaryPath)
    {
        var directory = Path.GetDirectoryName(dictionaryPath);
        if (directory == null) return [];

        var candidateDirectory = directory;
        var parent = Path.GetDirectoryName(directory);
        if (parent != null)
        {
            var vramDirectory = Path.Combine(
                parent, NextGenTexFile.GetVramTwinDirectoryName(directory));
            if (Directory.Exists(vramDirectory))
                candidateDirectory = vramDirectory;
        }

        var names = NextGenTexFile.GetVramTwinCandidateFileNames(dictionaryPath).ToList();
        var hashedName = GetHashNamedTwinFileName(Path.GetFileName(dictionaryPath));
        if (hashedName != null && !names.Contains(hashedName, StringComparer.OrdinalIgnoreCase))
            names.Add(hashedName);

        var candidates = new List<PayloadReference>();
        foreach (var name in names)
        {
            var path = Path.Combine(candidateDirectory, name);
            try
            {
                if (!File.Exists(path)) continue;
                candidates.Add(PayloadReference.ForFile(path, new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
            {
                // Leave unreadable candidates unresolved.
            }
        }

        return candidates;
    }

    private static string? GetHashNamedTwinFileName(string dictionaryName)
    {
        foreach (var (suffix, typeHash) in new[]
                 {
                     (".stex.ps3", VstexPayloadType),
                     (".tex.ps3", VtexPayloadType)
                 })
        {
            if (dictionaryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return dictionaryName[..^suffix.Length] + $".{typeHash:X8}.ps3";
        }

        return null;
    }

    private static bool TryGetContentTwinReference(
        string dictionaryPath,
        byte[] dictionaryData,
        out PayloadReference reference)
    {
        reference = default;
        if (!TryFindPs3DataRoot(dictionaryPath, out var dataRoot))
            return false;

        var catalog = ContentCatalogs.GetOrAdd(
            dataRoot,
            static root => new Lazy<ContentTwinCatalog>(
                () => BuildContentTwinCatalog(root),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return catalog.TryGet(dictionaryData, out reference);
    }

    private static ContentTwinCatalog BuildContentTwinCatalog(string dataRoot)
    {
        try
        {
            var buckets = new Dictionary<string, List<ContentGroupBuilder>>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".tex.ps3", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".stex.ps3", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] metadata;
                try
                {
                    metadata = File.ReadAllBytes(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!IsNonEmptyPs3Dictionary(metadata, out var required))
                    continue;

                var fingerprint = Convert.ToHexString(SHA256.HashData(metadata));
                if (!buckets.TryGetValue(fingerprint, out var bucket))
                {
                    bucket = [];
                    buckets.Add(fingerprint, bucket);
                }

                var group = bucket.FirstOrDefault(
                    candidate => candidate.Metadata.AsSpan().SequenceEqual(metadata));
                if (group == null)
                {
                    group = new ContentGroupBuilder(metadata, required);
                    bucket.Add(group);
                }
                group.DescriptorCount++;

                foreach (var candidate in GetExistingExactCandidates(file))
                {
                    if (candidate.Size != required)
                    {
                        group.InvalidProof = true;
                        continue;
                    }

                    group.AddProof(candidate, direct: true);
                }

                if (TryFindExtractedPakOwner(file, out _, out var entryPath,
                        out var mainArchive, out var vramArchive)
                    && TryGetArchivePayloadReference(
                        mainArchive,
                        vramArchive,
                        entryPath,
                        metadata,
                        out var archiveReference,
                        out _))
                {
                    group.AddProof(archiveReference, direct: false);
                }
            }

            var entries = new Dictionary<string, List<ContentTwinEntry>>(StringComparer.Ordinal);
            foreach (var (fingerprint, bucket) in buckets)
            {
                foreach (var group in bucket)
                {
                    if (group.DescriptorCount < 2 || group.InvalidProof || !group.HasDirectProof
                        || group.Proofs.Count == 0)
                    {
                        continue;
                    }

                    var reference = group.Proofs.First(static proof => proof.Direct).Reference;
                    if (group.Proofs.Any(
                            proof => !ReferencesAreByteIdentical(reference, proof.Reference)))
                    {
                        continue;
                    }

                    if (!entries.TryGetValue(fingerprint, out var matches))
                    {
                        matches = [];
                        entries.Add(fingerprint, matches);
                    }
                    matches.Add(new ContentTwinEntry(group.Metadata, reference));
                }
            }

            return new ContentTwinCatalog(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidDataException
                                   or OverflowException)
        {
            return ContentTwinCatalog.Empty;
        }
    }

    private static bool TryFindPs3DataRoot(string dictionaryPath, out string dataRoot)
    {
        dataRoot = "";
        var current = new DirectoryInfo(Path.GetDirectoryName(dictionaryPath)!);
        while (current != null)
        {
            if (current.Name.Equals("DATA", StringComparison.OrdinalIgnoreCase)
                && current.Parent?.Name.Equals("USRDIR", StringComparison.OrdinalIgnoreCase) == true
                && current.Parent.Parent?.Name.Equals(
                    "PS3_GAME", StringComparison.OrdinalIgnoreCase) == true)
            {
                dataRoot = current.FullName;
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool TryFindExtractedPakOwner(
        string dictionaryPath,
        out string extractedRoot,
        out string entryPath,
        out string mainArchive,
        out string vramArchive)
    {
        extractedRoot = "";
        entryPath = "";
        mainArchive = "";
        vramArchive = "";

        var current = new DirectoryInfo(Path.GetDirectoryName(dictionaryPath)!);
        while (current != null)
        {
            if (current.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                var candidateMain = current.FullName + ".ps3";
                if (TryGetArchivePairPaths(candidateMain, out mainArchive, out vramArchive))
                {
                    extractedRoot = current.FullName;
                    entryPath = NormalizeEntryPath(
                        Path.GetRelativePath(current.FullName, dictionaryPath));
                    return true;
                }
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool TryGetArchivePairPaths(
        string candidateMain,
        out string mainArchive,
        out string vramArchive)
    {
        mainArchive = candidateMain;
        vramArchive = "";
        if (!candidateMain.EndsWith(PakSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        vramArchive = candidateMain[..^PakSuffix.Length] + VramPakSuffix;
        return File.Exists(mainArchive) && File.Exists(vramArchive);
    }

    private static bool TryGetArchivePayloadReference(
        string mainArchive,
        string vramArchive,
        string descriptorEntryPath,
        byte[] dictionaryData,
        out PayloadReference reference,
        out NextGenVramPayloadSource source)
    {
        reference = default;
        source = NextGenVramPayloadSource.None;
        var cacheKey = BuildArchiveCatalogKey(mainArchive, vramArchive);
        if (cacheKey == null) return false;

        var catalog = ArchiveCatalogs.GetOrAdd(
            cacheKey,
            _ => new Lazy<ArchivePairCatalog>(
                () => BuildArchivePairCatalog(mainArchive, vramArchive),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return catalog.TryGet(descriptorEntryPath, dictionaryData, out reference, out source);
    }

    private static string? BuildArchiveCatalogKey(string mainArchive, string vramArchive)
    {
        try
        {
            var main = new FileInfo(mainArchive);
            var vram = new FileInfo(vramArchive);
            return string.Join(
                '|',
                main.FullName,
                main.Length,
                main.LastWriteTimeUtc.Ticks,
                vram.FullName,
                vram.Length,
                vram.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static ArchivePairCatalog BuildArchivePairCatalog(
        string mainArchive,
        string vramArchive)
    {
        try
        {
            using var mainFileSystem = ArchiveFileSystem.TryOpen(mainArchive);
            using var vramFileSystem = ArchiveFileSystem.TryOpen(vramArchive);
            if (mainFileSystem == null || vramFileSystem == null)
                return ArchivePairCatalog.Empty;

            var descriptors = PakArchive.GetTypedEntries(mainArchive);
            var payloads = PakArchive.GetTypedEntries(vramArchive);
            var pairs = new Dictionary<string, ArchivePayloadEntry>(
                StringComparer.OrdinalIgnoreCase);
            var vramLength = new FileInfo(vramArchive).Length;

            foreach (var kinds in new[]
                     {
                         new ArchiveKinds(
                             TexDescriptorType, VtexPayloadType, ".tex.ps3", ".vtex.ps3"),
                         new ArchiveKinds(
                             StexDescriptorType, VstexPayloadType, ".stex.ps3", ".vstex.ps3")
                     })
            {
                var typedDescriptors = new List<DescriptorEntry>();
                var invalidPopulation = false;
                foreach (var (_, entry) in descriptors.Where(item => item.TypeHash == kinds.DescriptorType))
                {
                    if (entry.Size <= 0 || entry.Size > int.MaxValue)
                    {
                        invalidPopulation = true;
                        break;
                    }

                    var metadata = mainFileSystem.ReadEntry(entry);
                    if (!NextGenTexFile.TryProbe(metadata, out var isPs3, out _) || !isPs3)
                    {
                        invalidPopulation = true;
                        break;
                    }

                    var required = NextGenTexFile.GetRequiredPayloadLength(metadata);
                    if (required == 0)
                        continue;
                    typedDescriptors.Add(new DescriptorEntry(entry, metadata, required));
                }

                if (invalidPopulation) continue;

                var typedPayloads = payloads
                    .Where(item => item.TypeHash == kinds.PayloadType)
                    .Select(static item => item.Entry)
                    .ToArray();
                if (typedDescriptors.Count == 0 || typedDescriptors.Count != typedPayloads.Length)
                    continue;

                var populationPairs = new List<(DescriptorEntry Descriptor, ArchiveEntry Payload)>();
                for (var index = 0; index < typedDescriptors.Count; index++)
                {
                    var descriptor = typedDescriptors[index];
                    var payload = typedPayloads[index];
                    if (payload.Size != descriptor.Required
                        || payload.Crc != descriptor.Entry.Crc
                        || !string.Equals(
                            GetCanonicalStem(descriptor.Entry, kinds.DescriptorSuffix),
                            GetCanonicalStem(payload, kinds.PayloadSuffix),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        invalidPopulation = true;
                        break;
                    }

                    populationPairs.Add((descriptor, payload));
                }

                if (invalidPopulation) continue;

                foreach (var (descriptor, payload) in populationPairs)
                {
                    var expectedPayloadName = SwapSuffix(
                        descriptor.Entry.Name, kinds.DescriptorSuffix, kinds.PayloadSuffix);
                    var exactNamed = string.Equals(
                        expectedPayloadName, payload.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            NormalizeDirectory(descriptor.Entry.Directory),
                            NormalizeDirectory(payload.Directory),
                            StringComparison.OrdinalIgnoreCase);
                    var source = exactNamed
                        ? NextGenVramPayloadSource.ArchiveNamedEntry
                        : NextGenVramPayloadSource.ArchiveIndexedEntry;
                    var reference = !payload.InCompanion
                                    && IsRangeInside(vramLength, payload.Offset, payload.Size)
                        ? PayloadReference.ForArchive(
                            vramArchive, payload.Offset, payload.Size, payload.FullName)
                        : PayloadReference.ForInlineArchive(
                            vramArchive,
                            payload.Offset,
                            payload.Size,
                            payload.FullName,
                            vramFileSystem.ReadEntry(payload));
                    // PakArchive disambiguates duplicate names. Keep this final
                    // guard so malformed/case-colliding populations cannot
                    // silently replace an earlier descriptor owner.
                    if (!pairs.TryAdd(
                            NormalizeEntryPath(descriptor.Entry.FullName),
                            new ArchivePayloadEntry(descriptor.Metadata, reference, source)))
                    {
                        return ArchivePairCatalog.Empty;
                    }
                }
            }

            return new ArchivePairCatalog(pairs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidDataException
                                   or OverflowException)
        {
            return ArchivePairCatalog.Empty;
        }
    }

    private static bool IsRangeInside(long containerLength, long offset, long size)
    {
        return offset >= 0 && size >= 0 && offset <= containerLength
               && size <= containerLength - offset;
    }

    private static string GetCanonicalStem(ArchiveEntry entry, string suffix)
    {
        var stem = entry.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? entry.Name[..^suffix.Length]
            : entry.Name;

        // PakArchive disambiguates colliding generated names as
        // <logical>_<resolved-offset>[_ordinal]. Remove that decoration only
        // when the hex token is this entry's actual offset and the surviving
        // stem is exactly what this name CRC independently resolves to. This
        // avoids treating arbitrary authored numeric suffixes as equivalent.
        var decoratedStem = stem;
        var offsetToken = unchecked((uint)entry.Offset).ToString("X8");
        var offsetUnderscore = stem.LastIndexOf('_');
        if (offsetUnderscore < 0)
            return decoratedStem;

        if (!string.Equals(
                stem[(offsetUnderscore + 1)..], offsetToken,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(stem[(offsetUnderscore + 1)..], out _))
                return decoratedStem;

            stem = stem[..offsetUnderscore];
            offsetUnderscore = stem.LastIndexOf('_');
            if (offsetUnderscore < 0
                || !string.Equals(
                    stem[(offsetUnderscore + 1)..], offsetToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                return decoratedStem;
            }
        }

        var logicalStem = stem[..offsetUnderscore];
        var crcStem = NeversoftMultitool.Core.QbKey.QbKey.TryResolve(entry.Crc)
                      ?? $"{entry.Crc:X8}";
        if (string.Equals(logicalStem, crcStem, StringComparison.OrdinalIgnoreCase))
            return logicalStem;

        return decoratedStem;
    }

    private static string SwapSuffix(string name, string from, string to)
    {
        return name.EndsWith(from, StringComparison.OrdinalIgnoreCase)
            ? name[..^from.Length] + to
            : name;
    }

    private static string NormalizeDirectory(string directory)
    {
        return directory.Replace('\\', '/').Trim('/');
    }

    private static string NormalizeEntryPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string? TryGetExtractedPayloadPath(
        string extractedMainRoot,
        string vramArchive,
        string payloadEntryName)
    {
        var parent = Path.GetDirectoryName(extractedMainRoot);
        if (parent == null) return null;

        var vramRoot = Path.Combine(parent, Path.GetFileNameWithoutExtension(vramArchive));
        var path = Path.Combine(
            vramRoot, NormalizeEntryPath(payloadEntryName).Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) return path;

        var name = Path.GetFileName(path);
        string? hashNamed = null;
        if (name.EndsWith(".vstex.ps3", StringComparison.OrdinalIgnoreCase))
            hashNamed = name[..^".vstex.ps3".Length] + $".{VstexPayloadType:X8}.ps3";
        else if (name.EndsWith(".vtex.ps3", StringComparison.OrdinalIgnoreCase))
            hashNamed = name[..^".vtex.ps3".Length] + $".{VtexPayloadType:X8}.ps3";

        if (hashNamed == null) return null;
        var hashPath = Path.Combine(Path.GetDirectoryName(path)!, hashNamed);
        return File.Exists(hashPath) ? hashPath : null;
    }

    private static bool ReferencesAreByteIdentical(
        PayloadReference first,
        PayloadReference second)
    {
        if (first.Size != second.Size) return false;
        if (first.Path.Equals(second.Path, StringComparison.OrdinalIgnoreCase)
            && first.Offset == second.Offset)
        {
            return true;
        }

        if (first.InlineBytes != null || second.InlineBytes != null)
        {
            var firstBytes = TryReadReference(first);
            var secondBytes = TryReadReference(second);
            return firstBytes != null && secondBytes != null
                   && firstBytes.AsSpan().SequenceEqual(secondBytes);
        }

        try
        {
            using var firstStream = File.OpenRead(first.Path);
            using var secondStream = File.OpenRead(second.Path);
            firstStream.Position = first.Offset;
            secondStream.Position = second.Offset;
            var firstBuffer = new byte[64 * 1024];
            var secondBuffer = new byte[firstBuffer.Length];
            long remaining = first.Size;
            while (remaining > 0)
            {
                var count = (int)Math.Min(firstBuffer.Length, remaining);
                firstStream.ReadExactly(firstBuffer.AsSpan(0, count));
                secondStream.ReadExactly(secondBuffer.AsSpan(0, count));
                if (!firstBuffer.AsSpan(0, count).SequenceEqual(secondBuffer.AsSpan(0, count)))
                    return false;
                remaining -= count;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[]? TryReadReference(PayloadReference reference)
    {
        if (reference.Size < 0 || reference.Size > int.MaxValue)
            return null;

        if (reference.InlineBytes != null)
            return reference.InlineBytes.ToArray();

        try
        {
            using var stream = File.OpenRead(reference.Path);
            return ReadRange(stream, reference.Offset, checked((int)reference.Size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static byte[] ReadRange(Stream stream, long offset, int size)
    {
        if (!IsRangeInside(stream.Length, offset, size))
            throw new InvalidDataException(
                $"Payload range {offset}+{size} is outside {stream.Length} bytes");

        stream.Position = offset;
        var bytes = new byte[size];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private readonly record struct ArchiveKinds(
        uint DescriptorType,
        uint PayloadType,
        string DescriptorSuffix,
        string PayloadSuffix);

    private readonly record struct DescriptorEntry(
        ArchiveEntry Entry,
        byte[] Metadata,
        long Required);

    private readonly record struct ArchivePayloadEntry(
        byte[] Metadata,
        PayloadReference Reference,
        NextGenVramPayloadSource Source);

    private readonly record struct PayloadReference(
        string Path,
        long Offset,
        long Size,
        string Location,
        string? FileSystemPath,
        string EntryName,
        byte[]? InlineBytes)
    {
        public static PayloadReference ForFile(string path, long size)
        {
            return new PayloadReference(
                path, 0, size, path, path, System.IO.Path.GetFileName(path), null);
        }

        public static PayloadReference ForArchive(
            string archivePath,
            long offset,
            long size,
            string entryName)
        {
            return new PayloadReference(
                archivePath,
                offset,
                size,
                $"{archivePath}::{entryName}",
                null,
                entryName,
                null);
        }

        public static PayloadReference ForInlineArchive(
            string archivePath,
            long offset,
            long size,
            string entryName,
            byte[] bytes)
        {
            if (size > int.MaxValue)
                throw new InvalidDataException(
                    $"PAK entry '{entryName}' is too large ({size} bytes)");
            if (bytes.LongLength != size)
                throw new InvalidDataException(
                    $"PAK entry '{entryName}' read {bytes.LongLength} bytes, expected {size}");

            return new PayloadReference(
                archivePath,
                offset,
                size,
                $"{archivePath}::{entryName}",
                null,
                entryName,
                bytes);
        }
    }

    private sealed class ArchivePairCatalog(
        IReadOnlyDictionary<string, ArchivePayloadEntry> entries)
    {
        public static ArchivePairCatalog Empty { get; } = new(
            new Dictionary<string, ArchivePayloadEntry>(StringComparer.OrdinalIgnoreCase));

        public bool TryGet(
            string descriptorEntryPath,
            byte[] metadata,
            out PayloadReference reference,
            out NextGenVramPayloadSource source)
        {
            reference = default;
            source = NextGenVramPayloadSource.None;
            if (!entries.TryGetValue(NormalizeEntryPath(descriptorEntryPath), out var entry)
                || !entry.Metadata.AsSpan().SequenceEqual(metadata))
            {
                return false;
            }

            reference = entry.Reference;
            source = entry.Source;
            return true;
        }
    }

    private sealed class ContentGroupBuilder(byte[] metadata, long required)
    {
        private readonly HashSet<string> _proofIdentities = new(StringComparer.OrdinalIgnoreCase);

        public byte[] Metadata { get; } = metadata;
        public long Required { get; } = required;
        public int DescriptorCount { get; set; }
        public bool InvalidProof { get; set; }
        public bool HasDirectProof { get; private set; }
        public List<(PayloadReference Reference, bool Direct)> Proofs { get; } = [];

        public void AddProof(PayloadReference reference, bool direct)
        {
            if (reference.Size != Required)
            {
                InvalidProof = true;
                return;
            }

            var identity = $"{reference.Path}|{reference.Offset}|{reference.Size}";
            if (_proofIdentities.Add(identity))
                Proofs.Add((reference, direct));
            HasDirectProof |= direct;
        }
    }

    private readonly record struct ContentTwinEntry(
        byte[] Metadata,
        PayloadReference Reference);

    private sealed class ContentTwinCatalog(
        IReadOnlyDictionary<string, List<ContentTwinEntry>> entries)
    {
        public static ContentTwinCatalog Empty { get; } = new(
            new Dictionary<string, List<ContentTwinEntry>>(StringComparer.Ordinal));

        public bool TryGet(byte[] metadata, out PayloadReference reference)
        {
            reference = default;
            var fingerprint = Convert.ToHexString(SHA256.HashData(metadata));
            if (!entries.TryGetValue(fingerprint, out var matches))
                return false;

            foreach (var match in matches)
            {
                if (!match.Metadata.AsSpan().SequenceEqual(metadata)) continue;
                reference = match.Reference;
                return true;
            }

            return false;
        }
    }
}

internal enum NextGenVramPayloadSource
{
    None,
    ExactName,
    IdenticalDictionary,
    ArchiveNamedEntry,
    ArchiveIndexedEntry
}

internal readonly record struct NextGenVramPayloadResolution(
    byte[]? Bytes,
    string? Location,
    string? FileSystemPath,
    NextGenVramPayloadSource Source)
{
    public static NextGenVramPayloadResolution Missing { get; } = new(
        null, null, null, NextGenVramPayloadSource.None);

    public static NextGenVramPayloadResolution Success(
        byte[] bytes,
        string location,
        string? fileSystemPath,
        NextGenVramPayloadSource source)
    {
        return new NextGenVramPayloadResolution(bytes, location, fileSystemPath, source);
    }
}
