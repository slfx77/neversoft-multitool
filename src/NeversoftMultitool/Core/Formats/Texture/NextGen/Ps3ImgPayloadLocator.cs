using System.Collections.Concurrent;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Resolves the raw IMV payload paired with a PS3 IMG descriptor.
/// </summary>
/// <remarks>
///     Loose images use a same-directory <c>.imv.ps3</c>. Project 8 additionally
///     puts IMG descriptors in <c>FOO.PAK</c> and payload entries in
///     <c>FOO_VRAM.PAK</c>. Named entries pair by basename; unnamed zone entries
///     pair by table ordinal, accepted only when the two typed populations have
///     equal counts and every descriptor's exact BC byte size matches its paired
///     payload. This is the same decode-then-validate principle used by the
///     next-gen TEX twin locator, extended to split PAKs.
/// </remarks>
internal static class Ps3ImgPayloadLocator
{
    internal const uint DescriptorType = 0xDAD5E950;
    internal const uint PayloadType = 0x4DFB7779;

    private const string DescriptorSuffix = ".img.ps3";
    private const string ExtractedPayloadSuffix = ".4DFB7779.ps3";

    private static readonly ConcurrentDictionary<string, Lazy<ArchivePairCatalog>> ArchiveCatalogs =
        new(StringComparer.OrdinalIgnoreCase);

    public static Ps3ImgPayloadResolution Resolve(AssetSource source, int expectedLength)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.FileSystemPath is { } path)
            return Resolve(path, expectedLength);

        var payloadName = GetPayloadFileName(source.EntryName);
        foreach (var candidateName in GetSameOwnerCandidateNames(source.EntryName, payloadName))
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
                return Ps3ImgPayloadResolution.Error(
                    $"Failed to read PS3 IMG payload '{candidateName}': {ex.Message}");
            }

            if (bytes != null)
                return ValidateBytes(bytes, expectedLength, candidateName,
                    Ps3ImgPayloadSource.SameOwner);
        }

        if (source is ArchiveAssetSource archiveSource
            && TryGetArchivePairPaths(archiveSource, out var mainArchive, out var vramArchive))
        {
            return ResolveFromArchivePair(
                mainArchive,
                vramArchive,
                archiveSource.Entry.FullName,
                expectedLength);
        }

        return Ps3ImgPayloadResolution.Missing(
            $"PS3 IMG pixel payload '{payloadName}' was not found");
    }

    public static Ps3ImgPayloadResolution Resolve(string descriptorPath, int expectedLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(descriptorPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException)
        {
            return Ps3ImgPayloadResolution.Error(ex.Message);
        }

        var payloadName = GetPayloadFileName(Path.GetFileName(fullPath));
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null)
        {
            return Ps3ImgPayloadResolution.Missing(
                $"PS3 IMG pixel payload '{payloadName}' was not found");
        }

        // A physically present sibling owns the descriptor. If it is short, do
        // not silently borrow a same-named copy elsewhere: Proving Ground ships
        // two such truncated assets and they must fail closed.
        var direct = Path.Combine(directory, payloadName);
        var directResolution = TryReadCandidate(
            direct, expectedLength, Ps3ImgPayloadSource.SameDirectory);
        if (directResolution != null)
            return directResolution.Value;

        if (TryFindExtractedPakRoot(fullPath, out var extractedRoot, out var entryPath,
                out var mainArchive, out var vramArchive))
        {
            var vramDirectory = GetVramDirectory(extractedRoot);
            foreach (var candidateName in GetSameOwnerCandidateNames(
                         Path.GetFileName(fullPath), payloadName))
            {
                var candidate = Path.Combine(vramDirectory, candidateName);
                var resolution = TryReadCandidate(
                    candidate, expectedLength, Ps3ImgPayloadSource.ExtractedVramPak);
                if (resolution != null)
                    return resolution.Value;
            }

            if (mainArchive != null && vramArchive != null)
            {
                var archiveResolution = ResolveFromArchivePair(
                    mainArchive, vramArchive, entryPath, expectedLength);
                if (archiveResolution.Status != Ps3ImgPayloadStatus.Missing)
                    return archiveResolution;
            }
        }

        // PG mirrors 20 descriptors under DATA/COMPRESSED/PS3 but keeps their
        // IMVs in the corresponding uncompressed DATA path. Only use that exact
        // structural mirror when no local payload exists; a short local payload
        // has already returned above.
        var mirror = TryGetUncompressedMirrorPath(direct);
        if (mirror != null)
        {
            var mirrorResolution = TryReadCandidate(
                mirror, expectedLength, Ps3ImgPayloadSource.UncompressedMirror);
            if (mirrorResolution != null)
                return mirrorResolution.Value;
        }

        return Ps3ImgPayloadResolution.Missing(
            $"PS3 IMG pixel payload '{payloadName}' was not found");
    }

    internal static string GetPayloadFileName(string descriptorFileName)
    {
        var name = Path.GetFileName(descriptorFileName);
        var index = name.LastIndexOf(".img.", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return name;

        return name[..index] + ".imv." + name[(index + 5)..];
    }

    private static IEnumerable<string> GetSameOwnerCandidateNames(
        string descriptorName,
        string payloadName)
    {
        yield return payloadName;

        if (!descriptorName.EndsWith(DescriptorSuffix, StringComparison.OrdinalIgnoreCase))
            yield break;

        yield return descriptorName[..^DescriptorSuffix.Length] + ExtractedPayloadSuffix;
    }

    private static Ps3ImgPayloadResolution? TryReadCandidate(
        string path,
        int expectedLength,
        Ps3ImgPayloadSource source)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var actualLength = new FileInfo(path).Length;
            if (actualLength != expectedLength)
                return SizeFailure(expectedLength, actualLength, path, source);

            return Ps3ImgPayloadResolution.Success(
                File.ReadAllBytes(path), path, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return Ps3ImgPayloadResolution.Error(
                $"Failed to read PS3 IMG payload '{path}': {ex.Message}");
        }
    }

    private static Ps3ImgPayloadResolution ValidateBytes(
        byte[] bytes,
        int expectedLength,
        string location,
        Ps3ImgPayloadSource source)
    {
        return bytes.LongLength == expectedLength
            ? Ps3ImgPayloadResolution.Success(bytes, location, source)
            : SizeFailure(expectedLength, bytes.LongLength, location, source);
    }

    private static Ps3ImgPayloadResolution SizeFailure(
        int expectedLength,
        long actualLength,
        string location,
        Ps3ImgPayloadSource source)
    {
        var condition = actualLength < expectedLength ? "truncated" : "oversized";
        return new Ps3ImgPayloadResolution(
            Ps3ImgPayloadStatus.InvalidSize,
            null,
            location,
            source,
            $"PS3 IMG payload '{location}' is {condition}: expected {expectedLength} bytes, " +
            $"got {actualLength}");
    }

    private static Ps3ImgPayloadResolution ResolveFromArchivePair(
        string mainArchive,
        string vramArchive,
        string descriptorEntryPath,
        int expectedLength)
    {
        var cacheKey = BuildCatalogKey(mainArchive, vramArchive);
        var catalog = ArchiveCatalogs.GetOrAdd(
            cacheKey,
            _ => new Lazy<ArchivePairCatalog>(
                () => BuildArchivePairCatalog(mainArchive, vramArchive),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        if (!catalog.TryGetPayload(descriptorEntryPath, out var payload))
        {
            return Ps3ImgPayloadResolution.Missing(
                string.IsNullOrEmpty(catalog.Error)
                    ? $"No safely paired PS3 IMV entry for '{descriptorEntryPath}'"
                    : catalog.Error);
        }

        if (payload.Size != expectedLength)
        {
            return SizeFailure(
                expectedLength,
                payload.Size,
                $"{vramArchive}::{payload.EntryName}",
                Ps3ImgPayloadSource.VramPakArchive);
        }

        try
        {
            var bytes = ReadRange(vramArchive, payload.Offset, payload.Size);
            return Ps3ImgPayloadResolution.Success(
                bytes,
                $"{vramArchive}::{payload.EntryName}",
                Ps3ImgPayloadSource.VramPakArchive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidDataException
                                   or OverflowException)
        {
            return Ps3ImgPayloadResolution.Error(
                $"Failed to read PS3 IMG VRAM entry '{payload.EntryName}': {ex.Message}");
        }
    }

    private static ArchivePairCatalog BuildArchivePairCatalog(
        string mainArchive,
        string vramArchive)
    {
        try
        {
            var descriptors = PakArchive.GetTypedEntries(mainArchive)
                .Where(entry => entry.TypeHash == DescriptorType)
                .Select(entry => entry.Entry)
                .ToArray();
            var payloads = PakArchive.GetTypedEntries(vramArchive)
                .Where(entry => entry.TypeHash == PayloadType)
                .Select(entry => entry.Entry)
                .ToArray();

            if (descriptors.Length == 0 || descriptors.Length != payloads.Length)
            {
                return ArchivePairCatalog.Fail(
                    $"PS3 IMG PAK populations cannot be paired safely: " +
                    $"{descriptors.Length} descriptors, {payloads.Length} payloads");
            }

            var pairs = new Dictionary<string, PayloadLocation>(StringComparer.OrdinalIgnoreCase);
            using var mainStream = File.OpenRead(mainArchive);
            for (var i = 0; i < descriptors.Length; i++)
            {
                var descriptorEntry = descriptors[i];
                var payloadEntry = payloads[i];
                if (descriptorEntry.InCompanion || payloadEntry.InCompanion
                    || descriptorEntry.Size != Ps3ImgFile.DescriptorSize
                    || payloadEntry.Size > int.MaxValue)
                {
                    return ArchivePairCatalog.Fail(
                        $"PS3 IMG PAK ordinal {i} has an unsupported storage layout");
                }

                var descriptor = ReadRange(
                    mainStream, descriptorEntry.Offset, descriptorEntry.Size);
                if (!Ps3ImgFile.TryInspect(descriptor, out var info, out var error))
                {
                    return ArchivePairCatalog.Fail(
                        $"PS3 IMG PAK descriptor '{descriptorEntry.FullName}' is invalid: {error}");
                }

                if (info.PayloadSize != payloadEntry.Size)
                {
                    return ArchivePairCatalog.Fail(
                        $"PS3 IMG PAK ordinal pairing failed size validation at " +
                        $"'{descriptorEntry.FullName}': expected {info.PayloadSize}, " +
                        $"payload entry has {payloadEntry.Size}");
                }

                var location = new PayloadLocation(
                    payloadEntry.FullName, payloadEntry.Offset, checked((int)payloadEntry.Size));
                pairs[NormalizeEntryPath(descriptorEntry.FullName)] = location;
            }

            // Basename lookup is useful for flat ArchiveAssetSource implementations,
            // but only when it remains unique.
            foreach (var group in descriptors.GroupBy(
                         entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                var members = group.ToArray();
                if (members.Length != 1)
                    continue;
                var fullName = NormalizeEntryPath(members[0].FullName);
                pairs.TryAdd(members[0].Name, pairs[fullName]);
            }

            return new ArchivePairCatalog(pairs, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidDataException
                                   or OverflowException)
        {
            return ArchivePairCatalog.Fail(
                $"Failed to inspect PS3 IMG PAK companions: {ex.Message}");
        }
    }

    private static bool TryGetArchivePairPaths(
        ArchiveAssetSource source,
        out string mainArchive,
        out string vramArchive)
    {
        mainArchive = source.Backend.ArchivePath;
        vramArchive = GetVramArchivePath(mainArchive) ?? "";
        return File.Exists(mainArchive) && File.Exists(vramArchive);
    }

    private static bool TryFindExtractedPakRoot(
        string descriptorPath,
        out string extractedRoot,
        out string entryPath,
        out string? mainArchive,
        out string? vramArchive)
    {
        extractedRoot = "";
        entryPath = "";
        mainArchive = null;
        vramArchive = null;

        var current = new DirectoryInfo(Path.GetDirectoryName(descriptorPath)!);
        for (var depth = 0; depth < 5 && current != null; depth++, current = current.Parent)
        {
            if (!current.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                continue;

            extractedRoot = current.FullName;
            entryPath = NormalizeEntryPath(Path.GetRelativePath(current.FullName, descriptorPath));

            var candidateMain = current.FullName + ".ps3";
            var candidateVram = GetVramArchivePath(candidateMain);
            if (File.Exists(candidateMain) && candidateVram != null && File.Exists(candidateVram))
            {
                mainArchive = candidateMain;
                vramArchive = candidateVram;
            }

            return true;
        }

        return false;
    }

    private static string GetVramDirectory(string extractedRoot)
    {
        var parent = Path.GetDirectoryName(extractedRoot) ?? "";
        return Path.Combine(
            parent,
            NextGenTexFile.GetVramTwinDirectoryName(extractedRoot));
    }

    private static string? GetVramArchivePath(string mainArchive)
    {
        const string suffix = ".pak.ps3";
        if (!mainArchive.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        return mainArchive[..^suffix.Length] + "_vram.pak.ps3";
    }

    private static string? TryGetUncompressedMirrorPath(string directPayloadPath)
    {
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}COMPRESSED{separator}PS3{separator}";
        var index = directPayloadPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        return directPayloadPath.Remove(index, marker.Length - 1);
    }

    private static string BuildCatalogKey(string mainArchive, string vramArchive)
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

    private static byte[] ReadRange(string path, long offset, int size)
    {
        using var stream = File.OpenRead(path);
        return ReadRange(stream, offset, size);
    }

    private static byte[] ReadRange(Stream stream, long offset, long size)
    {
        if (offset < 0 || size < 0 || size > int.MaxValue
            || offset > stream.Length || size > stream.Length - offset)
        {
            throw new InvalidDataException(
                $"Archive entry range {offset}+{size} is outside {stream.Length} bytes");
        }

        stream.Position = offset;
        var bytes = new byte[(int)size];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string NormalizeEntryPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private readonly record struct PayloadLocation(string EntryName, long Offset, int Size);

    private sealed record ArchivePairCatalog(
        IReadOnlyDictionary<string, PayloadLocation> Pairs,
        string Error)
    {
        public static ArchivePairCatalog Fail(string error)
        {
            return new ArchivePairCatalog(
                new Dictionary<string, PayloadLocation>(StringComparer.OrdinalIgnoreCase), error);
        }

        public bool TryGetPayload(string entryPath, out PayloadLocation payload)
        {
            return Pairs.TryGetValue(NormalizeEntryPath(entryPath), out payload)
                   || Pairs.TryGetValue(Path.GetFileName(entryPath), out payload);
        }
    }
}

internal enum Ps3ImgPayloadStatus
{
    Found,
    Missing,
    InvalidSize,
    Error
}

internal enum Ps3ImgPayloadSource
{
    None,
    SameDirectory,
    SameOwner,
    ExtractedVramPak,
    VramPakArchive,
    UncompressedMirror
}

internal readonly record struct Ps3ImgPayloadResolution(
    Ps3ImgPayloadStatus Status,
    byte[]? Bytes,
    string? Location,
    Ps3ImgPayloadSource Source,
    string Message)
{
    public bool Found => Status == Ps3ImgPayloadStatus.Found && Bytes != null;

    public static Ps3ImgPayloadResolution Success(
        byte[] bytes,
        string location,
        Ps3ImgPayloadSource source)
    {
        return new Ps3ImgPayloadResolution(
            Ps3ImgPayloadStatus.Found, bytes, location, source, "");
    }

    public static Ps3ImgPayloadResolution Missing(string message)
    {
        return new Ps3ImgPayloadResolution(
            Ps3ImgPayloadStatus.Missing, null, null, Ps3ImgPayloadSource.None, message);
    }

    public static Ps3ImgPayloadResolution Error(string message)
    {
        return new Ps3ImgPayloadResolution(
            Ps3ImgPayloadStatus.Error, null, null, Ps3ImgPayloadSource.None, message);
    }
}
