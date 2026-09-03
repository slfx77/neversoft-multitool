using System.Collections.Concurrent;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool;

internal static class MeshConverterTabFileScanner
{
    private static readonly IComparer<MeshFileEntry> RelativePathComparer =
        Comparer<MeshFileEntry>.Create(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

    public static MeshScanSummary AnalyzeDirectory(string inputDir, CancellationToken ct = default)
    {
        var allFiles = EnumerateFiles(inputDir, ct);
        return new MeshScanSummary(
            MeshConverterTabScanAnalysis.FindUnsupportedFiles(allFiles),
            MeshConverterTabScanAnalysis.CountPotentiallySupportedFiles(allFiles));
    }

    public static List<MeshFileEntry> ScanDirectory(
        string inputDir,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var buckets = ClassifyFiles(inputDir, ct);

        var ddmCompanionPaths = DdmCompanionIdentity.FindCompanionPsxPaths(
            buckets.DdmFiles,
            buckets.PsxFiles);

        var results = new ConcurrentBag<MeshFileEntry>();
        var processed = 0;

        void Report()
        {
            if (progress == null) return;
            var count = Interlocked.Increment(ref processed);
            progress.Report(count);
        }

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        Parallel.ForEach(buckets.DdmFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanDdmFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(
            buckets.PsxFiles.Where(file =>
                !ddmCompanionPaths.Contains(file)),
            parallelOptions,
            file =>
            {
                AddIfNotNull(results, ScanPsxFile(new FileSystemAssetSource(file), file, inputDir));
                Report();
            });

        Parallel.ForEach(buckets.N64ModelFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanN64ModelFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.GbaLevelFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanGbaLevelFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.GbaModelFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanGbaModelFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.NdsGeometryFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanNdsGeometryFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.RwDffFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanRwDffFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.RwBspFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanRwBspFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.ColFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanColFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.Ps2SceneFiles, parallelOptions, file =>
        {
            AddIfNotNull(results,
                ScanPs2SceneFile(new FileSystemAssetSource(file), file, inputDir, buckets.IskinStems));
            Report();
        });

        Parallel.ForEach(buckets.Ps2GeomFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanPs2GeomFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.XbxSceneFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanXbxSceneFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        Parallel.ForEach(buckets.PakSceneFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanAmbiguousSceneFile(file, inputDir, buckets.IskinStems));
            Report();
        });

        Parallel.ForEach(buckets.PakWorldzoneFiles, parallelOptions, file =>
        {
            AddIfNotNull(results, ScanPakWorldzoneFile(new FileSystemAssetSource(file), file, inputDir));
            Report();
        });

        var list = results.ToList();
        list.Sort(RelativePathComparer);
        return list;
    }

    /// <summary>
    ///     THPS3 repeats some root character assets verbatim inside level PREs.
    ///     Showing every copy produces indistinguishable rows and, more importantly,
    ///     makes the nested package copy the animation panel's source. Remove only a
    ///     nested RW-DFF whose mesh and same-stem TEX payloads both exactly match a
    ///     root-archive entry with the same filename. Nested-only variants, missing
    ///     companions, and package-specific texture variants remain separate rows.
    /// </summary>
    private static List<MeshFileEntry> RemoveNestedRwDffCopiesThatMatchRoot(
        List<MeshFileEntry> entries, CancellationToken ct)
    {
        var removed = new HashSet<MeshFileEntry>();
        var candidateGroups = entries
            .Where(static entry => entry.IsRwDff && entry.Source is ArchiveAssetSource)
            .GroupBy(static entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1);

        foreach (var candidateGroup in candidateGroups)
        {
            ct.ThrowIfCancellationRequested();
            var groupEntries = candidateGroup.ToList();
            var candidates = groupEntries.Select(entry =>
            {
                ct.ThrowIfCancellationRequested();
                return new ArchiveRwDffCopyCandidate(
                    entry.FileName,
                    ((ArchiveAssetSource)entry.Source).Backend.FileSystem.NestingDepth,
                    TryFingerprintRwDffWithTexture(entry));
            }).ToList();
            var keep = ArchiveRwDffCopyDeduplicator.SelectIndicesToKeep(candidates).ToHashSet();

            for (var index = 0; index < groupEntries.Count; index++)
            {
                if (!keep.Contains(index))
                    removed.Add(groupEntries[index]);
            }
        }

        return removed.Count == 0
            ? entries
            : entries.Where(entry => !removed.Contains(entry)).ToList();
    }

    private static ArchiveRwDffFingerprint? TryFingerprintRwDffWithTexture(MeshFileEntry entry)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(entry.Source.EntryName);
            var textureBytes = entry.Source.TryReadCompanion(stem + ".tex");
            if (textureBytes == null)
                return null;

            return ArchiveRwDffCopyDeduplicator.Fingerprint(entry.Source.ReadBytes(), textureBytes);
        }
        catch
        {
            // Deduplication is best-effort. A read failure must not hide an entry
            // or turn an otherwise successful archive scan into a failure.
            return null;
        }
    }

    /// <summary>
    ///     Entry point used when the user picks a single archive file. Routes THAW
    ///     worldzone PAKs to the single-entry worldzone path; for other archive
    ///     types (WAD, PRE, PRE3/PRX, PKR, non-worldzone PAK), opens the archive
    ///     and enumerates every entry that looks like a supported mesh file —
    ///     including entries inside NESTED archives (THPS3 packs its level .bsp
    ///     and .skn inside .pre entries of SKATE3.WAD). Phase 1 walks the entry
    ///     tables breadth-first (collecting .iskin.ps2 stems across all levels so
    ///     pre-compiled skins defer to their companions); phase 2 probes the
    ///     candidates in parallel. Every returned entry carries an
    ///     <see cref="ArchiveAssetSource" />.
    /// </summary>
    public static List<MeshFileEntry> ScanArchive(
        string archivePath,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        // Worldzone PAKs stay on their dedicated single-entry path. Extension-gated:
        // the content check alone also matches archives that merely CONTAIN worldzone
        // PAKs (DATAP.WAD embeds 52 of them) and would list the whole archive as one
        // "mesh" named after itself.
        if ((EndsWith(archivePath, ".pak.ps2") || EndsWith(archivePath, ".pak")) &&
            Ps2WorldzoneDetection.IsWorldzonePak(archivePath))
        {
            var worldzone = ScanPakWorldzoneFile(new FileSystemAssetSource(archivePath), archivePath, rootDir: "");
            return worldzone != null ? [worldzone] : [];
        }

        var backend = ArchiveAssetBackend.TryOpen(archivePath);
        if (backend == null)
            return [];

        var (workItems, iskinStems) = CollectArchiveWorkItems(backend, ct);

        var results = new ConcurrentBag<MeshFileEntry>();
        var processed = 0;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        Parallel.ForEach(workItems, parallelOptions, item =>
        {
            var source = new ArchiveAssetSource(item.Backend, item.Entry);
            var virtualPath = $"{item.Backend.DisplayPath}::{item.Entry.FullName}";
            var entry = item.IsWorldzoneProbe
                ? ScanPakWorldzoneFile(source, virtualPath, rootDir: "")
                : TryScanEntry(source, virtualPath, rootDir: "", iskinStems);
            AddIfNotNull(results, entry);

            if (progress != null)
                progress.Report(Interlocked.Increment(ref processed));
        });

        var list = RemoveNestedRwDffCopiesThatMatchRoot(results.ToList(), ct);
        list.AddRange(BuildNdsLevelRows(list, ct));
        list.Sort(RelativePathComparer);
        return list;
    }

    /// <summary>
    ///     One synthetic row per DS LEVEL, because a DS level is not a file: it is a
    ///     model set whose pieces are separate container entries authored in world
    ///     space. The per-piece rows stay — they are perfectly good models — and this
    ///     adds the whole level beside them for the Levels tab.
    ///
    ///     A set is a level when the cart NAMES it one; that needs the cart's code, so
    ///     a bare extracted .gob contributes no level rows and the tab simply shows
    ///     nothing rather than guessing from size.
    /// </summary>
    private static List<MeshFileEntry> BuildNdsLevelRows(
        List<MeshFileEntry> scanned, CancellationToken ct)
    {
        var rows = new List<MeshFileEntry>();
        var containers = new Dictionary<string, IArchiveFileSystem>(StringComparer.Ordinal);
        var bySet = new Dictionary<(string Container, uint IdA), List<MeshFileEntry>>();

        foreach (var entry in scanned)
        {
            if (!entry.IsNdsGeometry || entry.Source is not ArchiveAssetSource archive)
                continue;
            if (!NdsModelCompanions.TryReadSetIds(entry.Source, out var idA, out _))
                continue;
            var container = archive.Backend.FileSystem;
            containers[container.DisplayPath] = container;
            var key = (container.DisplayPath, idA);
            if (!bySet.TryGetValue(key, out var list))
                bySet[key] = list = [];
            list.Add(entry);
        }

        foreach (var ((containerPath, idA), pieces) in bySet)
        {
            ct.ThrowIfCancellationRequested();
            if (pieces.Count < 2)
                continue;
            var naming = NdsModelNaming.For(containers[containerPath]);
            if (!naming.Sets.TryGetValue(idA, out var setName) || !NdsSetNames.IsLevel(setName))
                continue;

            // The row's source is one piece of the set; the parser reads the set id
            // back off it and composites the rest from the container.
            var first = pieces.OrderBy(p => p.FileName, StringComparer.Ordinal).First();
            // Shown and exported without the exporter's _Visual tag; the authored
            // name keeps it, and everything that matches on it still uses that.
            var display = NdsSetNames.DisplayName(setName);
            rows.Add(new MeshFileEntry
            {
                FileName = NdsSetNames.ToStem(display),
                FilePath = $"{containerPath}::{setName}",
                RelativePath = display,
                Format = "DS Level",
                ObjectCount = pieces.Count,
                MeshCount = pieces.Sum(p => p.MeshCount),
                TriangleCount = pieces.Sum(p => p.TriangleCount),
                Source = first.Source
            });
        }

        return rows;
    }

    /// <summary>
    ///     Breadth-first walk over an archive and its nested archives, returning
    ///     scan candidates plus the .iskin.ps2 stem set across ALL levels. Only
    ///     nested-container payloads are read here; candidate payloads wait for
    ///     the parallel phase.
    /// </summary>
    private static (List<ArchiveScanItem> WorkItems, HashSet<string> IskinStems) CollectArchiveWorkItems(
        ArchiveAssetBackend root, CancellationToken ct)
    {
        var workItems = new List<ArchiveScanItem>();
        var iskinStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ArchiveAssetBackend>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var backend = pending.Dequeue();

            foreach (var archiveEntry in backend.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = archiveEntry.Name;

                if (EndsWith(name, ".iskin.ps2"))
                    iskinStems.Add(StripCompoundExtension(name));

                if (IsScanCandidate(name))
                {
                    workItems.Add(new ArchiveScanItem(backend, archiveEntry, IsWorldzoneProbe: false));
                    continue;
                }

                if (EndsWith(name, ".pak.ps2") || EndsWith(name, ".pak"))
                {
                    // A .pak entry is either a THAW worldzone (probe it as one in
                    // the parallel phase) or a plain nested archive to recurse.
                    byte[] data;
                    try
                    {
                        data = backend.ReadEntryBytes(archiveEntry);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
                    {
                        continue;
                    }

                    if (Ps2WorldzoneDetection.IsWorldzonePak(data))
                    {
                        workItems.Add(new ArchiveScanItem(backend, archiveEntry, IsWorldzoneProbe: true));
                        continue;
                    }
                }

                var nested = backend.TryOpenNested(archiveEntry);
                if (nested != null)
                    pending.Enqueue(nested);
            }
        }

        return (workItems, iskinStems);
    }

    /// <summary>
    ///     True when the entry name matches any suffix <see cref="TryScanEntry" />
    ///     can route. Worldzone PAKs are deliberately excluded so archive
    ///     enumeration keeps falling through to nested-archive opening; object
    ///     DDMs are placement companions, not standalone meshes.
    /// </summary>
    private static bool IsScanCandidate(string name)
    {
        return MeshTypeDetector.IsMeshCandidate(name) && !MeshTypeDetector.IsObjectDdm(name);
    }

    /// <summary>
    ///     Single-file picker for a worldzone PAK. Kept for backwards compat with
    ///     the existing SelectArchive_Click handler.
    /// </summary>
    public static MeshFileEntry? ScanSingleArchiveAsWorldzone(string pakFile)
    {
        return ScanPakWorldzoneFile(new FileSystemAssetSource(pakFile), pakFile,
            rootDir: Path.GetDirectoryName(pakFile) ?? "");
    }

    internal static string StripCompoundExtension(string filename)
    {
        return MeshTypeDetector.GetStem(filename);
    }

    private static List<string> EnumerateFiles(string inputDir, CancellationToken ct)
    {
        var list = new List<string>();
        foreach (var file in Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(file);
        }

        return list;
    }

    /// <summary>
    ///     Tries each per-format scan in suffix-priority order for a single entry
    ///     (used by archive enumeration where one entry can only match one format).
    /// </summary>
    private static MeshFileEntry? TryScanEntry(
        AssetSource source, string displayPath, string rootDir, HashSet<string>? iskinStems)
    {
        var name = source.EntryName;
        if (MeshTypeDetector.IsObjectDdm(name))
            return null;

        var route = MeshTypeDetector.DetectByName(name);

        // Bare .skin/.mdl carry no kind in their name — resolve from content so a
        // THAW PC scene reaches the scene reader instead of the PS2 skin reader.
        if (route.Kind == MeshFileKind.None && route.RequiresContentProbe)
            route = MeshTypeDetector.DetectNested(name, source.ReadBytes());

        return route.Kind switch
        {
            MeshFileKind.Ps2Scene => ScanPs2SceneFile(source, displayPath, rootDir, iskinStems),
            MeshFileKind.Ps2Geom => ScanPs2GeomFile(source, displayPath, rootDir),
            MeshFileKind.XbxScene => ScanXbxSceneFile(source, displayPath, rootDir),
            MeshFileKind.Collision => ScanColFile(source, displayPath, rootDir),
            MeshFileKind.RenderWareDff => ScanRwDffFile(source, displayPath, rootDir),
            MeshFileKind.RenderWareBsp => ScanRwBspFile(source, displayPath, rootDir),
            MeshFileKind.Ddm => ScanDdmFile(source, displayPath, rootDir),
            MeshFileKind.N64Model => ScanN64ModelFile(source, displayPath, rootDir),
            MeshFileKind.GbaLevel => ScanGbaLevelFile(source, displayPath, rootDir),
            MeshFileKind.GbaModel => ScanGbaModelFile(source, displayPath, rootDir),
            MeshFileKind.NdsGeometry => ScanNdsGeometryFile(source, displayPath, rootDir),
            MeshFileKind.NdsCollision => ScanNdsCollisionFile(source, displayPath, rootDir),
            MeshFileKind.Psx => ScanPsxFile(source, displayPath, rootDir),
            _ => null
        };
    }

    private static bool EndsWith(string name, string suffix)
        => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static MeshScanFileBuckets ClassifyFiles(string inputDir, CancellationToken ct)
    {
        var buckets = new MeshScanFileBuckets();
        foreach (var file in EnumerateFiles(inputDir, ct))
        {
            var fileName = Path.GetFileName(file);
            if (MeshTypeDetector.IsWorldzoneCandidate(fileName))
            {
                buckets.PakWorldzoneFiles.Add(file);
                continue;
            }

            if (MeshTypeDetector.IsObjectDdm(fileName))
                continue;

            var route = MeshTypeDetector.DetectByName(fileName);
            switch (route.Kind)
            {
                case MeshFileKind.Ddm:
                    buckets.DdmFiles.Add(file);
                    break;
                case MeshFileKind.N64Model:
                    buckets.N64ModelFiles.Add(file);
                    break;
                case MeshFileKind.GbaLevel:
                    buckets.GbaLevelFiles.Add(file);
                    break;
                case MeshFileKind.GbaModel:
                    buckets.GbaModelFiles.Add(file);
                    break;
                case MeshFileKind.NdsGeometry:
                    buckets.NdsGeometryFiles.Add(file);
                    break;
                case MeshFileKind.Psx:
                    buckets.PsxFiles.Add(file);
                    break;
                case MeshFileKind.RenderWareDff:
                    buckets.RwDffFiles.Add(file);
                    break;
                case MeshFileKind.RenderWareBsp:
                    buckets.RwBspFiles.Add(file);
                    break;
                case MeshFileKind.Collision:
                    buckets.ColFiles.Add(file);
                    break;
                case MeshFileKind.Ps2Geom:
                    buckets.Ps2GeomFiles.Add(file);
                    break;
                case MeshFileKind.XbxScene:
                    buckets.XbxSceneFiles.Add(file);
                    break;
                case MeshFileKind.Ps2Scene:
                    buckets.Ps2SceneFiles.Add(file);
                    if (string.Equals(route.Suffix, ".iskin.ps2", StringComparison.Ordinal))
                        buckets.IskinStems.Add(StripCompoundExtension(fileName));
                    break;
                default:
                    // Bare .skin/.mdl — the platform is decided from content when scanned.
                    if (route.RequiresContentProbe)
                        buckets.PakSceneFiles.Add(file);
                    break;
            }
        }

        return buckets;
    }

    private static void AddIfNotNull(ConcurrentBag<MeshFileEntry> bag, MeshFileEntry? entry)
    {
        if (entry != null)
            bag.Add(entry);
    }

    /// <summary>
    ///     A bare .skin/.mdl carries no platform in its name. THAW shipped them on
    ///     PC (scene format) and PS2 (skin/MDL format) alike, so the reader is
    ///     chosen from content — the PC ones would otherwise be dropped by the PS2
    ///     reader.
    /// </summary>
    private static MeshFileEntry? ScanAmbiguousSceneFile(
        string file, string inputDir, HashSet<string>? iskinStems)
    {
        var source = new FileSystemAssetSource(file);
        var route = MeshTypeDetector.Detect(file);
        return route.Kind switch
        {
            MeshFileKind.XbxScene => ScanXbxSceneFile(source, file, inputDir),
            MeshFileKind.Ps2Scene => ScanPs2SceneFile(source, file, inputDir, iskinStems),
            _ => null
        };
    }

    private static bool IsColFilePath(string file)
    {
        return MeshTypeDetector.DetectByName(Path.GetFileName(file)).Kind == MeshFileKind.Collision;
    }

    private static string MakeRelativePath(string displayPath, string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir))
            return Path.GetFileName(displayPath);
        try
        {
            return Path.GetRelativePath(rootDir, displayPath);
        }
        catch
        {
            return Path.GetFileName(displayPath);
        }
    }

    private static MeshFileEntry? ScanDdmFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var ddm = DdmFile.Parse(source.ReadBytes());
            var stem = Path.GetFileNameWithoutExtension(source.EntryName);
            var companionPsxExists = source.CompanionExists(stem + ".psx");

            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = companionPsxExists ? "DDM (placed)" : "DDM",
                ObjectCount = ddm.Objects.Count,
                MeshCount = ddm.Objects.Count,
                Source = source,
                HasPlacedPsxCompanion = companionPsxExists
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Carved GBA level records. Trusted by name like N64 bundles, so the gate
    ///     is only what a carved record must satisfy structurally: the exact record
    ///     length and a companion ROM to dereference into. Everything downstream
    ///     (the level's art, collision and conversion) is fail-closed.
    /// </summary>
    private static MeshFileEntry? ScanGbaLevelFile(AssetSource source, string displayPath, string rootDir)
    {
        // Every supported isometric generation now converts to one textured
        // collision-surface mesh. Its carved parent/art record has a different
        // length in each format family; the ROM companion closes the structure.
        return ScanGbaRecord(
                   source, displayPath, rootDir, GbaLevelCarver.LevelRecordSize, "GBA Level",
                   objectCount: 1, meshCount: 1)
               ?? ScanGbaRecord(
                   source, displayPath, rootDir, GbaThps3LevelArt.LevelRecordStride, "GBA Level",
                   objectCount: 1, meshCount: 1)
               ?? ScanGbaRecord(
                   source, displayPath, rootDir, GbaLaterLevelArt.ArtRecordStride, "GBA Level",
                   objectCount: 1, meshCount: 1);
    }

    /// <summary>Carved GBA character records — the shared skater mesh plus this
    ///     character's colours, animated by the ROM's clip table.</summary>
    private static MeshFileEntry? ScanGbaModelFile(AssetSource source, string displayPath, string rootDir)
    {
        // THPS2: every character is the same 8-sub-object mesh, emitted as one
        // mesh node. THPS3: the one rider, two parts (rider + deck), one node.
        return ScanGbaRecord(
                   source, displayPath, rootDir, GbaSkaterModel.CharacterRecordSize, "GBA Character",
                   objectCount: GbaSkaterModel.SubObjectCount, meshCount: 1)
               ?? ScanGbaRecord(
                   source, displayPath, rootDir, GbaThps3RiderModel.DirectoryRecordSize, "GBA Character",
                   objectCount: 2, meshCount: 1);
    }

    private static MeshFileEntry? ScanGbaRecord(
        AssetSource source, string displayPath, string rootDir, int recordSize, string format,
        int objectCount, int meshCount)
    {
        try
        {
            if (source.ReadBytes().Length != recordSize
                || !source.CompanionExists(GbaLevelCarver.RomEntryName))
                return null;

            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = format,
                ObjectCount = objectCount,
                MeshCount = meshCount,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     One Vicarious Visions DS model. The row's counts come from running the
    ///     display list, because a DS geometry file declares no mesh or object count
    ///     of its own — the GX pipeline is what says how much is in there.
    /// </summary>
    private static MeshFileEntry? ScanNdsGeometryFile(
        AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var data = source.ReadBytes();
            if (!NdsGeometryFile.TryParseValidated(data, out var geometry))
                return null;

            var groups = NdsGxInterpreter.Run(data, geometry);
            var triangles = groups.Sum(g => g.Indices.Count / 3);
            if (triangles == 0)
                return null;

            return new MeshFileEntry
            {
                // A cart names its models; a loose file keeps its two hex ids,
                // because the names live in code the container does not carry.
                FileName = NdsRowName(source) ?? source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "DS",
                ObjectCount = geometry.JointCount,
                MeshCount = groups.Count,
                TriangleCount = triangles,
                NdsHasClips = NdsModelCompanions.ReadClips(source, limit: 1).Count > 0,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     A DS level's collision world. It sits in the Levels tab beside the level's
    ///     render row, because it IS that level — the surface it is skated on rather
    ///     than the surface it is drawn as.
    /// </summary>
    private static MeshFileEntry? ScanNdsCollisionFile(
        AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            if (!NdsCollisionFile.TryParse(source.ReadBytes(), out var world))
                return null;

            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                // The bare entry name, so a collision row reads beside its level's
                // row rather than carrying the container path the level row omits.
                RelativePath = source.EntryName,
                Format = "DS Collision",
                // Chained rail segments and distinct gameplay volumes — the two
                // things in here that are not the surface itself.
                ObjectCount = world.Edges.Count,
                MeshCount = world.Faces.Where(f => f.IsVolume)
                    .Select(f => f.VolumeId).Distinct().Count(),
                TriangleCount = world.Faces.Count,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? NdsRowName(AssetSource source)
    {
        if (source is not ArchiveAssetSource archive)
            return null;
        return NdsModelCompanions.TryReadSetIds(source, out var idA, out var idB)
            ? NdsModelNaming.For(archive.Backend.FileSystem).StemFor(idA, idB)
            : null;
    }

    private static MeshFileEntry? ScanN64ModelFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var shellData = source.ReadBytes();
            var shell = PsxN64ShellFile.Parse(shellData);
            if (shell == null)
                return null;

            var bank = N64ModelCompanions.TryReadRenderBank(source);
            var meshes = bank != null ? N64RenderBankFile.Parse(bank) : [];
            var renderBankId = N64ModelCompanions.TryReadRenderBankId(source);

            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "N64",
                ObjectCount = shell.Objects.Count,
                MeshCount = meshes.Count,
                PsxIsSuperModel = shell.IsSuperModel,
                PsxFormatRevision = shell.FormatRevision,
                N64MaxBoundsRadius = N64ModelCompanions.TryReadMaxBoundsRadius(source) ?? 0f,
                N64HasEmbeddedAnimations =
                    N64AnimatedModelGate.TryOpen(
                        shellData, shell, bank, renderBankId, meshes) != null,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPsxFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var psxFile = PsxMeshFile.Parse(source.ReadBytes());
            if (psxFile == null)
                return null;

            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "PSX",
                ObjectCount = psxFile.Objects.Count,
                MeshCount = psxFile.Meshes.Count,
                PsxIsSuperModel = psxFile.IsSuperModel,
                PsxFormatRevision = psxFile.FormatRevision,
                HasSupportedLevelObjectCompanion =
                    MeshCompanionResolver.HasSupportedLevelObjectCompanion(
                        source,
                        source.EntryName),
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanRwDffFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var data = source.ReadBytes();
            if (!RwDffFile.IsDffFile(data))
                return null;

            var clump = RwDffFile.Parse(data);
            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "RW DFF",
                ObjectCount = clump.Atomics.Length,
                MeshCount = clump.Geometries.Length,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanRwBspFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var data = source.ReadBytes();
            if (!RwBspFile.IsBspFile(data))
                return null;

            var world = RwBspFile.Parse(data);
            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "RW BSP",
                ObjectCount = world.Sections.Length,
                MeshCount = world.Materials.Length,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanColFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var data = source.ReadBytes();
            if (!ColFile.IsColFile(data))
                return null;

            var scene = ColFile.Parse(data);
            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "COL",
                ObjectCount = scene.Objects.Length,
                MeshCount = scene.Objects.Length,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPs2SceneFile(
        AssetSource source, string displayPath, string rootDir, HashSet<string>? iskinStems)
    {
        try
        {
            var data = source.ReadBytes();
            var fileName = source.EntryName;
            var lower = fileName.ToLowerInvariant();
            var stem = StripCompoundExtension(fileName);

            Ps2SceneSubFormat subFormat;
            string format;
            int objectCount;
            int meshCount;

            if (ThawPs2SkinFile.IsPakSkin(data))
            {
                subFormat = Ps2SceneSubFormat.PakSkin;
                format = "PS2 (THAW)";
                var scene = ThawPs2SkinFile.ParsePakSkin(data);
                objectCount = scene.MeshGroups.Count;
                meshCount = scene.MeshGroups.Sum(group => group.Meshes.Count);
            }
            else if (Ps2GeomFile.IsPakMdl(data))
            {
                subFormat = Ps2SceneSubFormat.PakMdl;
                format = "PS2 (THAW)";
                var scene = Ps2GeomFile.ParsePakMdl(data);
                objectCount = scene.Leaves.Count;
                meshCount = scene.Leaves.Count;
            }
            else if (ThawPs2SkinFile.IsThawPs2Skin(data, data.Length))
            {
                if (lower.EndsWith(".skin.ps2", StringComparison.OrdinalIgnoreCase) &&
                    iskinStems != null &&
                    iskinStems.Contains(stem))
                {
                    return null;
                }

                subFormat = Ps2SceneSubFormat.ThawSkin;
                format = "PS2 (THAW)";
                var scene = ThawPs2SkinFile.Parse(data);
                objectCount = scene.MeshGroups.Count;
                meshCount = scene.MeshGroups.Sum(group => group.Meshes.Count);
            }
            else if (Ps2SceneFile.IsPs2Scene(data))
            {
                subFormat = Ps2SceneSubFormat.Standard;
                format = BitConverter.ToUInt32(data, 0) switch
                {
                    3 => "PS2 (THPS4)",
                    5 => "PS2 (THUG)",
                    6 => "PS2 (THUG2)",
                    _ => "PS2 (pre-compiled)"
                };
                var scene = Ps2SceneFile.Parse(data);
                objectCount = scene.MeshGroups.Count;
                meshCount = scene.MeshGroups.Sum(group => group.Meshes.Count);
            }
            else
            {
                return null;
            }

            return new MeshFileEntry
            {
                FileName = fileName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = format,
                ObjectCount = objectCount,
                MeshCount = meshCount,
                Ps2SubFormat = subFormat,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPs2GeomFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var scene = Ps2GeomFile.Parse(source.ReadBytes());
            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "PS2 GEOM",
                ObjectCount = scene.Leaves.Count,
                MeshCount = scene.Leaves.Count,
                Ps2SubFormat = Ps2SceneSubFormat.Geom,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanXbxSceneFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            var data = source.ReadBytes();
            var fileName = source.EntryName;

            XbxScene scene;
            string format;
            if (NgcSceneFile.IsNgcScene(data))
            {
                scene = NgcSceneFile.Parse(data);
                format = "GameCube (THAW)";
            }
            else if (ThawSceneFile.IsThawScene(data))
            {
                scene = ThawSceneFile.Parse(data);
                format = fileName.EndsWith(".wpc", StringComparison.OrdinalIgnoreCase)
                    ? "PC (THAW)"
                    : "Xbox (THAW)";
            }
            else if (XbxSceneFile.IsXbxScene(data))
            {
                scene = XbxSceneFile.Parse(data);
                format = fileName.EndsWith(".wpc", StringComparison.OrdinalIgnoreCase)
                    ? "PC (THUG2)"
                    : "Xbox (THUG2)";
            }
            else
            {
                return null;
            }

            return new MeshFileEntry
            {
                FileName = fileName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = format,
                ObjectCount = scene.Sectors.Length,
                MeshCount = scene.Materials.Length,
                Source = source,
                XbxHasSkinnedSectors = scene.Sectors.Any(static sector => sector.IsSkinned)
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPakWorldzoneFile(AssetSource source, string displayPath, string rootDir)
    {
        try
        {
            // Byte-based so worldzone PAKs nested inside a parent archive
            // (e.g. z_bh.pak.ps2 inside DATAP.WAD) are detected the same way
            // as on-disk PAK files.
            var data = source.ReadBytes();
            if (!Ps2WorldzoneDetection.IsWorldzonePak(data))
                return null;

            var typed = PakArchive.GetTypedEntries(data);
            var mdlCount = typed.Count(e =>
                e.TypeHash == Ps2WorldzoneDetection.WorldzoneMdlTypeHash
                || e.TypeHash == Ps2WorldzoneDetection.WorldzoneLevelMdlTypeHash);
            var placementCount = typed.Count(e =>
                e.TypeHash == Ps2WorldzoneDetection.WorldzonePlacementTypeHash);

            return new MeshFileEntry
            {
                FileName = source.EntryName,
                FilePath = displayPath,
                RelativePath = MakeRelativePath(displayPath, rootDir),
                Format = "PS2 (THAW worldzone)",
                ObjectCount = mdlCount,
                MeshCount = placementCount,
                Ps2SubFormat = Ps2SceneSubFormat.PakWorldzone,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed record ArchiveScanItem(
        ArchiveAssetBackend Backend,
        ArchiveEntry Entry,
        bool IsWorldzoneProbe);

    private sealed class MeshScanFileBuckets
    {
        public List<string> ColFiles { get; } = [];
        public List<string> DdmFiles { get; } = [];
        public HashSet<string> IskinStems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> PakSceneFiles { get; } = [];
        public List<string> PakWorldzoneFiles { get; } = [];
        public List<string> Ps2GeomFiles { get; } = [];
        public List<string> Ps2SceneFiles { get; } = [];
        public List<string> N64ModelFiles { get; } = [];
        public List<string> GbaLevelFiles { get; } = [];
        public List<string> GbaModelFiles { get; } = [];
        public List<string> NdsGeometryFiles { get; } = [];
        public List<string> PsxFiles { get; } = [];
        public List<string> RwBspFiles { get; } = [];
        public List<string> RwDffFiles { get; } = [];
        public List<string> XbxSceneFiles { get; } = [];
    }
}
