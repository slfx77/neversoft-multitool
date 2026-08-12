using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts THUG/THUG2 cutscene containers (.cut, .cut.ps2, .cut.xbx) — the engine's
///     CFileLibrary format (THUG source Sys/File/FileLibrary.{h,cpp}):
///     header { u32 version=1; i32 numFiles } + numFiles × { u32 offset; i32 size;
///     u32 nameQbKey; u32 extQbKey } + contiguous data blobs (little-endian on all platforms).
///     Payloads are keyed by an extension checksum (SKA/CAM/OBA anims, SKIN/MDL/GEOM models,
///     TEX textures, QB script, CIF object-binding list, CAS, WGT cutscene-head
///     mesh-scaling weight/index triples;
///     THUG2 adds SKE and "cifstruct" (the CIF replacement — a CStruct WriteToBuffer
///     stream, see <see cref="QbStructBuffer" />); bare THUG .cut masters add a
///     plaintext .q-style placement section). These extension checksums are NOT pak-style
///     QbKey(".ext") values — the map below comes from the case labels in
///     Sk/Objects/cutscenedetails.cpp:3098-3200.
///     Few TOC name keys resolve from the global dictionaries, so names are recovered from
///     the embedded QB script's CHECKSUM_NAME pairs and (bare .cut) the plaintext section's
///     identifiers, validated by hash before use. The object-binding table (THUG CIF:
///     u32 ver; i32 count; count × {obj, model, skeleton, anim, flags} — THUG2 cifstruct:
///     {Version, numObjects, camanimduration, Objects[]}) cross-links objects to sibling
///     entries and is exported as a JSON manifest during extraction.
/// </summary>
public static partial class CutArchive
{
    private const int HeaderSize = 8;
    private const int TocEntrySize = 16;
    private const int MaxFiles = 4096; // corpus max is 303; the engine's 128 cap is runtime-only

    private const uint ExtCif = 0x5AC14717;
    private const uint ExtSka = 0xEAB51346;
    private const uint ExtCam = 0x05CA1497;
    private const uint ExtOba = 0x2E4BF21B;
    private const uint ExtQb = 0x2BBEA5C3;
    private const uint ExtGeom = 0xE7308C1F; // PS2 geometry; Xbox cuts carry MDL instead
    private const uint ExtSkin = 0xFD8697E1;
    private const uint ExtMdl = 0x0524FD4E;
    private const uint ExtTex = 0x1512808D;
    private const uint ExtCas = 0xFFC529F4;
    private const uint ExtWgt = 0x2CD4107D;
    private const uint ExtSke = 0xEDD8D75F; // QbKey("ske"); THUG2 only
    private const uint ExtCif2 = 0x508AE2F2; // = QbKey("cifstruct"): THUG2 CIF replacement, CStruct stream
    private const uint ExtText = 0x2208E9E8; // bare-.cut masters: plaintext .q-style placement

    private static readonly Dictionary<uint, string> ExtensionByKey = new()
    {
        [ExtCif] = "cif",
        [ExtSka] = "ska",
        [ExtCam] = "cam",
        [ExtOba] = "oba",
        [ExtQb] = "qb",
        [ExtGeom] = "geom",
        [ExtSkin] = "skin",
        [ExtMdl] = "mdl",
        [ExtTex] = "tex",
        [ExtCas] = "cas",
        [ExtWgt] = "wgt",
        [ExtSke] = "ske",
        [ExtCif2] = "cif2",
        [ExtText] = "q"
    };

    // Payloads in the platform's compiled scene formats get the container's platform
    // suffix so extracted files route to the existing converters (.tex.xbx -> xbxtex etc.).
    private static readonly HashSet<uint> PlatformTypedKeys =
        [ExtGeom, ExtSkin, ExtMdl, ExtTex, ExtCas, ExtWgt];

    // Engine fetches these by (nameKey=0, extKey) wildcard — one per cutscene, named by role.
    private static readonly Dictionary<uint, string> SingletonRoleNames = new()
    {
        [ExtCif] = "objects",
        [ExtCif2] = "objects",
        [ExtCam] = "camera",
        [ExtOba] = "object",
        [ExtQb] = "cutscene",
        [ExtText] = "placement"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // QbKeys of the cifstruct component names (all field checksums hash lowercased).
    private static readonly uint FieldCamAnimDuration = QbKey.QbKey.HashLower("camanimduration");
    private static readonly uint FieldObjects = QbKey.QbKey.HashLower("Objects");
    private static readonly uint FieldObjectName = QbKey.QbKey.HashLower("ObjectName");
    private static readonly uint FieldType = QbKey.QbKey.HashLower("Type");
    private static readonly uint FieldSkeletonName = QbKey.QbKey.HashLower("SkeletonName");
    private static readonly uint FieldExternalModelFile = QbKey.QbKey.HashLower("ExternalModelFile");
    private static readonly uint FieldRefObjectName = QbKey.QbKey.HashLower("RefObjectName");

    /// <summary>
    ///     Structural probe: version 1, sane file count, TOC starts the data region and
    ///     the blobs are strictly contiguous, ending exactly at EOF.
    /// </summary>
    public static bool IsCut(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return TryReadToc(stream, out _) != null;
    }

    public static List<ArchiveEntry> GetFileList(string cutPath)
    {
        var data = File.ReadAllBytes(cutPath);
        var toc = ParseToc(data);
        var names = ResolveNames(data, toc);
        var platformSuffix = GetPlatformSuffix(cutPath);

        var entries = new List<ArchiveEntry>(toc.Count);
        foreach (var e in toc)
        {
            var extension = ExtensionByKey.GetValueOrDefault(e.ExtKey, $"{e.ExtKey:x8}");
            var fileName = $"{names[e]}.{extension}";
            // The container suffix is provenance, not a claim that a consumer supports the payload.
            // Bare authoring CUTs naturally keep bare member names because their suffix is empty.
            if (PlatformTypedKeys.Contains(e.ExtKey))
                fileName += platformSuffix;

            entries.Add(new ArchiveEntry
            {
                Name = fileName,
                Directory = "",
                Size = e.Size,
                Offset = e.Offset,
                Crc = e.NameKey
            });
        }

        return entries;
    }

    public static void ExtractFiles(string cutPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var data = File.ReadAllBytes(cutPath);
        var toc = ParseToc(data);
        var entries = GetFileList(cutPath);
        var archiveName = ArchiveNaming.GetExtractionStem(cutPath);
        var outputRoot = Path.GetFullPath(outputDir);
        var extractDir = ArchiveExtractionPath.GetContainedPath(
            outputRoot, archiveName, "CUT extraction directory");
        var exportPaths = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            exportPaths[i] = ArchiveExtractionPath.GetContainedPath(
                extractDir, entries[i].Name, "CUT entry");
        }

        var manifestPath = ArchiveExtractionPath.GetContainedPath(
            outputRoot, archiveName + ".cif.json", "CUT manifest");
        Directory.CreateDirectory(extractDir);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[i];
            File.WriteAllBytes(exportPaths[i],
                data.AsSpan((int)entry.Offset, (int)entry.Size).ToArray());
            onFileExtracted?.Invoke(i + 1, entries.Count);
        }

        var manifest = BuildManifest(Path.GetFileName(cutPath), data, toc, entries);
        File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    // -------------------------------------------------------------------
    // Container parsing
    // -------------------------------------------------------------------

    private static List<TocEntry> ParseToc(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var toc = TryReadToc(stream, out var reason);
        return toc ?? throw new InvalidDataException($"Not a CUT container: {reason}");
    }

    private static List<TocEntry>? TryReadToc(Stream stream, out string reason)
    {
        reason = "";
        if (stream.Length < HeaderSize + TocEntrySize)
        {
            reason = "file too small";
            return null;
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        var version = reader.ReadUInt32();
        var numFiles = reader.ReadInt32();
        if (version != 1)
        {
            reason = $"version {version} (expected 1)";
            return null;
        }

        if (numFiles is < 1 or > MaxFiles)
        {
            reason = $"file count {numFiles} out of range";
            return null;
        }

        var dataStart = HeaderSize + numFiles * TocEntrySize;
        if (dataStart > stream.Length)
        {
            reason = "TOC runs past end of file";
            return null;
        }

        // The first blob starts exactly where the TOC ends — an exact anchor that ties
        // offset[0] to numFiles, so random data essentially never passes this probe.
        // Subsequent blobs are ascending and non-overlapping; the writer aligns most of
        // them but occasionally leaves padding (corpus max gap and tail are both 48 bytes).
        const int maxAlignmentGap = 64;
        var toc = new List<TocEntry>(numFiles);
        var previousEnd = (long)dataStart;
        for (var i = 0; i < numFiles; i++)
        {
            var entry = new TocEntry(reader.ReadUInt32(), reader.ReadInt32(),
                reader.ReadUInt32(), reader.ReadUInt32());
            var gap = entry.Offset - previousEnd;
            if (i == 0 && entry.Offset != dataStart)
            {
                reason = $"first blob at 0x{entry.Offset:X}, expected TOC end 0x{dataStart:X}";
                return null;
            }

            if (entry.Size < 0 || gap is < 0 or > maxAlignmentGap)
            {
                reason = $"entry {i} breaks blob ordering (gap {gap})";
                return null;
            }

            previousEnd = entry.Offset + entry.Size;
            toc.Add(entry);
        }

        var tail = stream.Length - previousEnd;
        if (tail is < 0 or > maxAlignmentGap)
        {
            reason = $"blobs end at 0x{previousEnd:X} but file is 0x{stream.Length:X} bytes";
            return null;
        }

        return toc;
    }

    private static string GetPlatformSuffix(string cutPath)
    {
        var name = Path.GetFileName(cutPath).ToLowerInvariant();
        var idx = name.LastIndexOf(".cut", StringComparison.Ordinal);
        return idx < 0 ? "" : name[(idx + 4)..]; // "" for bare .cut, else ".ps2"/".xbx"/".ngc"
    }

    // -------------------------------------------------------------------
    // Name resolution: embedded QB pairs -> plaintext identifiers -> global
    // dictionaries -> role names for zero-key singletons -> hex fallback.
    // -------------------------------------------------------------------

    private static Dictionary<TocEntry, string> ResolveNames(byte[] data, List<TocEntry> toc)
    {
        var harvested = HarvestLocalNames(data, toc);

        var names = new Dictionary<TocEntry, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in toc)
        {
            string name;
            if (e.NameKey != 0)
            {
                name = harvested.GetValueOrDefault(e.NameKey)
                       ?? QbKey.QbKey.TryResolve(e.NameKey)
                       ?? $"{e.NameKey:x8}";
                name = SanitizeFileName(name);
            }
            else
            {
                name = SingletonRoleNames.GetValueOrDefault(e.ExtKey, $"{e.Offset:x8}");
            }

            if (!used.Add(name + e.ExtKey))
                name = $"{name}_{e.Offset:x8}";
            names[e] = name;
        }

        return names;
    }

    private static Dictionary<uint, string> HarvestLocalNames(byte[] data, List<TocEntry> toc)
    {
        var wanted = toc.Where(e => e.NameKey != 0).Select(e => e.NameKey).ToHashSet();
        var harvested = new Dictionary<uint, string>();

        foreach (var e in toc)
        {
            if (e.ExtKey == ExtQb)
            {
                try
                {
                    var qb = QbFile.Parse(data.AsSpan((int)e.Offset, e.Size).ToArray(), "embedded.qb");
                    foreach (var (checksum, name) in qb.LocalNames)
                    {
                        if (wanted.Contains(checksum))
                            harvested.TryAdd(checksum, name);
                    }
                }
                catch
                {
                    // Malformed embedded script: fall back to the other naming tiers.
                }
            }
            else if (e.ExtKey == ExtText)
            {
                var text = Encoding.ASCII.GetString(data, (int)e.Offset, e.Size);
                foreach (var candidate in IdentifierRegex().Matches(text).Select(m => m.Value))
                {
                    var lower = QbKey.QbKey.HashLower(candidate);
                    if (wanted.Contains(lower))
                        harvested.TryAdd(lower, candidate);
                    var exact = QbKey.QbKey.Hash(candidate);
                    if (wanted.Contains(exact))
                        harvested.TryAdd(exact, candidate);
                }
            }
        }

        return harvested;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return name.Any(c => invalid.Contains(c))
            ? string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c))
            : name;
    }

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]{2,63}")]
    private static partial Regex IdentifierRegex();

    // -------------------------------------------------------------------
    // CIF object-binding manifest
    // -------------------------------------------------------------------

    private static CutManifest BuildManifest(string sourceName, byte[] data,
        List<TocEntry> toc, List<ArchiveEntry> entries)
    {
        // The same name key legitimately tags several entries (a character's SKA, SKE,
        // SKIN, and TEX all carry its checksum), so CIF cross-links resolve per type class.
        var animFileByKey = new Dictionary<uint, string>();
        var modelFileByKey = new Dictionary<uint, string>();
        var manifestEntries = new List<CutManifestEntry>(toc.Count);
        for (var i = 0; i < toc.Count; i++)
        {
            var e = toc[i];
            if (e.NameKey != 0)
            {
                if (e.ExtKey is ExtSka or ExtOba)
                    animFileByKey.TryAdd(e.NameKey, entries[i].Name);
                else if (e.ExtKey is ExtSkin or ExtMdl or ExtGeom)
                    modelFileByKey.TryAdd(e.NameKey, entries[i].Name);
            }

            manifestEntries.Add(new CutManifestEntry
            {
                File = entries[i].Name,
                Type = ExtensionByKey.GetValueOrDefault(e.ExtKey, $"0x{e.ExtKey:X8}"),
                NameChecksum = e.NameKey != 0 ? $"0x{e.NameKey:X8}" : null,
                Size = e.Size
            });
        }

        List<CutManifestObject>? objects = null;
        float? camAnimDuration = null;
        var cif = toc.FirstOrDefault(e => e.ExtKey == ExtCif);
        if (cif.Size >= 8)
        {
            objects = TryParseCif(data.AsSpan((int)cif.Offset, cif.Size), animFileByKey, modelFileByKey);
        }
        else
        {
            var cifStruct = toc.FirstOrDefault(e => e.ExtKey == ExtCif2);
            if (cifStruct.Size > 0)
            {
                objects = TryParseCifStruct(data.AsSpan((int)cifStruct.Offset, cifStruct.Size),
                    animFileByKey, modelFileByKey, out camAnimDuration);
            }
        }

        return new CutManifest
        {
            Source = sourceName,
            CamAnimDuration = camAnimDuration,
            Files = manifestEntries,
            Objects = objects
        };
    }

    /// <summary>
    ///     CIF payload (cutscenedetails.cpp:3673-3716): u32 version, u32 numObjects,
    ///     then numObjects × 5×u32 { objectName, modelName, skeletonName, animName, flags }.
    /// </summary>
    private static List<CutManifestObject>? TryParseCif(ReadOnlySpan<byte> cif,
        Dictionary<uint, string> animFileByKey, Dictionary<uint, string> modelFileByKey)
    {
        var count = BitConverter.ToInt32(cif[4..8]);
        if (count < 0 || cif.Length != 8 + count * 20)
            return null;

        var objects = new List<CutManifestObject>(count);
        for (var i = 0; i < count; i++)
        {
            var p = 8 + i * 20;
            var obj = BitConverter.ToUInt32(cif[p..]);
            var model = BitConverter.ToUInt32(cif[(p + 4)..]);
            var skeleton = BitConverter.ToUInt32(cif[(p + 8)..]);
            var anim = BitConverter.ToUInt32(cif[(p + 12)..]);
            var flags = BitConverter.ToUInt32(cif[(p + 16)..]);

            objects.Add(new CutManifestObject
            {
                Name = DescribeKey(obj),
                Model = DescribeKey(model),
                Skeleton = DescribeKey(skeleton),
                Anim = DescribeKey(anim),
                Flags = flags != 0 ? $"0x{flags:X8}" : null,
                IsSkater = (flags & 0x80000000) != 0 ? true : null,
                IsHead = (flags & 0x40000000) != 0 ? true : null,
                AnimFile = animFileByKey.GetValueOrDefault(anim),
                ModelFile = modelFileByKey.GetValueOrDefault(model)
            });
        }

        return objects;
    }

    /// <summary>
    ///     THUG2 "cifstruct" payload: a CStruct WriteToBuffer stream of
    ///     { Version, numObjects, camanimduration, Objects = array of struct
    ///     { ObjectName, Type, [SkeletonName], [ExternalModelFile], [RefObjectName] } }.
    ///     Field names are matched by QbKey; unknown/anonymous components are ignored
    ///     (the raw payload is still extracted alongside).
    /// </summary>
    private static List<CutManifestObject>? TryParseCifStruct(ReadOnlySpan<byte> payload,
        Dictionary<uint, string> animFileByKey, Dictionary<uint, string> modelFileByKey,
        out float? camAnimDuration)
    {
        camAnimDuration = null;
        List<QbStructBuffer.Component> components;
        try
        {
            components = QbStructBuffer.Parse(payload);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        List<CutManifestObject>? objects = null;
        foreach (var component in components)
        {
            if (component.NameChecksum == FieldCamAnimDuration && component.Value is float duration)
            {
                camAnimDuration = duration;
            }
            else if (component.NameChecksum == FieldObjects && component.Value is QbStructBuffer.Array array)
            {
                objects = array.Elements
                    .OfType<List<QbStructBuffer.Component>>()
                    .Select(fields => ParseCifStructObject(fields, animFileByKey, modelFileByKey))
                    .ToList();
            }
        }

        return objects;
    }

    private static CutManifestObject ParseCifStructObject(List<QbStructBuffer.Component> fields,
        Dictionary<uint, string> animFileByKey, Dictionary<uint, string> modelFileByKey)
    {
        uint objectName = 0, typeName = 0, skeletonName = 0, refObjectName = 0;
        string? externalModelFile = null;
        foreach (var f in fields)
        {
            if (f.NameChecksum == FieldObjectName && f.Value is uint obj) objectName = obj;
            else if (f.NameChecksum == FieldType && f.Value is uint type) typeName = type;
            else if (f.NameChecksum == FieldSkeletonName && f.Value is uint skel) skeletonName = skel;
            else if (f.NameChecksum == FieldRefObjectName && f.Value is uint refObj) refObjectName = refObj;
            else if (f.NameChecksum == FieldExternalModelFile && f.Value is string ext) externalModelFile = ext;
        }

        return new CutManifestObject
        {
            Name = DescribeKey(objectName),
            Type = DescribeKey(typeName),
            Skeleton = DescribeKey(skeletonName),
            RefObject = DescribeKey(refObjectName),
            ExternalModelFile = externalModelFile,
            AnimFile = animFileByKey.GetValueOrDefault(objectName),
            ModelFile = modelFileByKey.GetValueOrDefault(objectName)
        };
    }

    private static string? DescribeKey(uint key)
    {
        return key == 0 ? null : QbKey.QbKey.TryResolve(key) ?? $"0x{key:X8}";
    }

    private readonly record struct TocEntry(uint Offset, int Size, uint NameKey, uint ExtKey);

    private sealed class CutManifest
    {
        public string Source { get; init; } = "";
        public float? CamAnimDuration { get; init; } // cifstruct only (seconds)
        public List<CutManifestEntry> Files { get; init; } = [];
        public List<CutManifestObject>? Objects { get; init; }
    }

    private sealed class CutManifestEntry
    {
        public string File { get; init; } = "";
        public string Type { get; init; } = "";
        public string? NameChecksum { get; init; }
        public long Size { get; init; }
    }

    private sealed class CutManifestObject
    {
        public string? Name { get; init; }
        public string? Model { get; init; }
        public string? Skeleton { get; init; }
        public string? Anim { get; init; }
        public string? Flags { get; init; }
        public bool? IsSkater { get; init; }
        public bool? IsHead { get; init; }
        public string? Type { get; init; } // cifstruct only (e.g. SkinnedModel)
        public string? RefObject { get; init; } // cifstruct only
        public string? ExternalModelFile { get; init; } // cifstruct only
        public string? AnimFile { get; init; }
        public string? ModelFile { get; init; }
    }
}
