using System.Collections.Concurrent;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
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

    private static readonly string[] CompoundExtensions =
    [
        ".iskin.ps2", ".skin.ps2", ".mdl.ps2", ".geom.ps2",
        ".skin.xbx", ".mdl.xbx", ".scn.xbx", ".skin.wpc", ".mdl.wpc", ".scn.wpc",
        ".skin.ngc", ".mdl.ngc", ".scn.ngc",
        ".col.xbx", ".col.wpc", ".col.ps2",
        ".pak.ps2"
    ];

    private static readonly string[] ColSuffixes = [".col.xbx", ".col.wpc", ".col.ps2"];

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

        var ddmStems = buckets.DdmFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static stem => stem != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            buckets.PsxFiles.Where(file => !ddmStems.Contains(Path.GetFileNameWithoutExtension(file))),
            parallelOptions,
            file =>
            {
                AddIfNotNull(results, ScanPsxFile(new FileSystemAssetSource(file), file, inputDir));
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
            AddIfNotNull(results, ScanPs2SceneFile(new FileSystemAssetSource(file), file, inputDir, buckets.IskinStems));
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
        list.Sort(RelativePathComparer);
        return list;
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

    private sealed record ArchiveScanItem(
        ArchiveAssetBackend Backend, ArchiveEntry Entry, bool IsWorldzoneProbe);

    /// <summary>
    ///     True when the entry name matches any suffix <see cref="TryScanEntry" />
    ///     can route. Mirrors TryScanEntry's gates exactly.
    /// </summary>
    private static bool IsScanCandidate(string name)
    {
        if (EndsWith(name, ".iskin.ps2") || EndsWith(name, ".skin.ps2") || EndsWith(name, ".mdl.ps2") ||
            EndsWith(name, ".geom.ps2"))
        {
            return true;
        }

        if (EndsWith(name, ".skin.xbx") || EndsWith(name, ".mdl.xbx") ||
            EndsWith(name, ".scn.xbx") || EndsWith(name, ".scn.wpc") ||
            EndsWith(name, ".skin.wpc") || EndsWith(name, ".mdl.wpc") ||
            EndsWith(name, ".skin.ngc") || EndsWith(name, ".mdl.ngc") ||
            EndsWith(name, ".scn.ngc"))
        {
            return true;
        }

        if (OrdinalFileName.HasAnySuffix(name, ColSuffixes) || EndsWith(name, ".col"))
            return true;

        if (EndsWith(name, ".skn") || EndsWith(name, ".bsp"))
            return true;

        if (OrdinalFileName.HasExtension(name, ".ddm") &&
            !Path.GetFileNameWithoutExtension(name).EndsWith("_o", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return OrdinalFileName.HasExtension(name, ".psx") ||
               OrdinalFileName.HasExtension(name, ".skin") ||
               OrdinalFileName.HasExtension(name, ".mdl");
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
        return OrdinalFileName.StripCompoundSuffix(filename, CompoundExtensions);
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

        if (EndsWith(name, ".iskin.ps2") ||
            EndsWith(name, ".skin.ps2") ||
            EndsWith(name, ".mdl.ps2"))
        {
            return ScanPs2SceneFile(source, displayPath, rootDir, iskinStems);
        }

        if (EndsWith(name, ".geom.ps2"))
            return ScanPs2GeomFile(source, displayPath, rootDir);

        if (EndsWith(name, ".skin.xbx") || EndsWith(name, ".mdl.xbx") ||
            EndsWith(name, ".scn.xbx") || EndsWith(name, ".scn.wpc") ||
            EndsWith(name, ".skin.wpc") || EndsWith(name, ".mdl.wpc") ||
            EndsWith(name, ".skin.ngc") || EndsWith(name, ".mdl.ngc") ||
            EndsWith(name, ".scn.ngc"))
        {
            return ScanXbxSceneFile(source, displayPath, rootDir);
        }

        if (OrdinalFileName.HasAnySuffix(name, ColSuffixes) || EndsWith(name, ".col"))
            return ScanColFile(source, displayPath, rootDir);

        if (EndsWith(name, ".skn"))
            return ScanRwDffFile(source, displayPath, rootDir);

        if (EndsWith(name, ".bsp"))
            return ScanRwBspFile(source, displayPath, rootDir);

        if (OrdinalFileName.HasExtension(name, ".ddm") &&
            !Path.GetFileNameWithoutExtension(name).EndsWith("_o", StringComparison.OrdinalIgnoreCase))
        {
            return ScanDdmFile(source, displayPath, rootDir);
        }

        if (OrdinalFileName.HasExtension(name, ".psx"))
            return ScanPsxFile(source, displayPath, rootDir);

        if (OrdinalFileName.HasExtension(name, ".skin") || OrdinalFileName.HasExtension(name, ".mdl"))
            return ScanPs2SceneFile(source, displayPath, rootDir, iskinStems);

        return null;
    }

    private static bool EndsWith(string name, string suffix)
        => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static MeshScanFileBuckets ClassifyFiles(string inputDir, CancellationToken ct)
    {
        var buckets = new MeshScanFileBuckets();
        foreach (var file in EnumerateFiles(inputDir, ct))
        {
            var fileName = Path.GetFileName(file);
            var fileStem = Path.GetFileNameWithoutExtension(fileName);

            if (OrdinalFileName.HasExtension(fileName, ".ddm") &&
                !fileStem.EndsWith("_o", StringComparison.OrdinalIgnoreCase))
            {
                buckets.DdmFiles.Add(file);
            }

            if (OrdinalFileName.HasExtension(fileName, ".psx"))
                buckets.PsxFiles.Add(file);

            if (OrdinalFileName.HasExtension(fileName, ".skn"))
                buckets.RwDffFiles.Add(file);

            if (OrdinalFileName.HasExtension(fileName, ".bsp"))
                buckets.RwBspFiles.Add(file);

            if (IsColFilePath(file))
                buckets.ColFiles.Add(file);

            if (fileName.EndsWith(".iskin.ps2", StringComparison.OrdinalIgnoreCase))
            {
                buckets.Ps2SceneFiles.Add(file);
                buckets.IskinStems.Add(StripCompoundExtension(fileName));
            }
            else if (fileName.EndsWith(".skin.ps2", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".mdl.ps2", StringComparison.OrdinalIgnoreCase))
            {
                buckets.Ps2SceneFiles.Add(file);
            }

            if (fileName.EndsWith(".geom.ps2", StringComparison.OrdinalIgnoreCase))
                buckets.Ps2GeomFiles.Add(file);

            if (fileName.EndsWith(".skin.xbx", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".mdl.xbx", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".scn.xbx", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".scn.wpc", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".skin.wpc", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".mdl.wpc", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".skin.ngc", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".mdl.ngc", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".scn.ngc", StringComparison.OrdinalIgnoreCase))
            {
                buckets.XbxSceneFiles.Add(file);
            }

            if (OrdinalFileName.HasExtension(fileName, ".skin") ||
                OrdinalFileName.HasExtension(fileName, ".mdl"))
            {
                buckets.PakSceneFiles.Add(file);
            }

            if (fileName.EndsWith(".pak.ps2", StringComparison.OrdinalIgnoreCase))
                buckets.PakWorldzoneFiles.Add(file);
        }

        return buckets;
    }

    private static void AddIfNotNull(ConcurrentBag<MeshFileEntry> bag, MeshFileEntry? entry)
    {
        if (entry != null)
            bag.Add(entry);
    }

    private static bool IsColFilePath(string file)
    {
        var fileName = Path.GetFileName(file);
        return OrdinalFileName.HasAnySuffix(fileName, ColSuffixes) || OrdinalFileName.HasExtension(file, ".col");
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
                Source = source
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

    private sealed class MeshScanFileBuckets
    {
        public List<string> ColFiles { get; } = [];
        public List<string> DdmFiles { get; } = [];
        public HashSet<string> IskinStems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> PakSceneFiles { get; } = [];
        public List<string> PakWorldzoneFiles { get; } = [];
        public List<string> Ps2GeomFiles { get; } = [];
        public List<string> Ps2SceneFiles { get; } = [];
        public List<string> PsxFiles { get; } = [];
        public List<string> RwBspFiles { get; } = [];
        public List<string> RwDffFiles { get; } = [];
        public List<string> XbxSceneFiles { get; } = [];
    }
}
