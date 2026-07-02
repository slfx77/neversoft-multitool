using System.Collections.Concurrent;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
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
        ".skin.xbx", ".mdl.xbx", ".skin.wpc", ".mdl.wpc",
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
            AddIfNotNull(results, ScanPs2SceneFile(new FileSystemAssetSource(file), file, inputDir, iskinStems: null));
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
    ///     Entry point used when the user picks a single archive file. Routes THAW
    ///     worldzone PAKs to the single-entry worldzone path; for other archive
    ///     types (WAD, PRE, PRE3/PRX, PKR, non-worldzone PAK), opens the archive
    ///     in-memory and enumerates every entry that looks like a supported mesh
    ///     file. Every returned entry carries an <see cref="ArchiveAssetSource" />.
    /// </summary>
    public static List<MeshFileEntry> ScanArchive(string archivePath, CancellationToken ct = default)
    {
        // Worldzone PAKs stay on their dedicated single-entry path.
        if (Ps2WorldzoneDetection.IsWorldzonePak(archivePath))
        {
            var worldzone = ScanPakWorldzoneFile(new FileSystemAssetSource(archivePath), archivePath, rootDir: "");
            return worldzone != null ? [worldzone] : [];
        }

        var backend = ArchiveAssetBackend.TryOpen(archivePath);
        if (backend == null)
            return [];

        var archiveName = Path.GetFileName(archivePath);
        var results = new List<MeshFileEntry>();
        foreach (var archiveEntry in backend.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var source = new ArchiveAssetSource(backend, archiveEntry);
            var virtualPath = $"{archiveName}::{archiveEntry.Name}";
            var entry = TryScanEntry(source, virtualPath, rootDir: "", iskinStems: null);
            if (entry != null)
                results.Add(entry);
        }

        results.Sort(RelativePathComparer);
        return results;
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

    private static IReadOnlyList<string> EnumerateFiles(string inputDir, CancellationToken ct)
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
            EndsWith(name, ".skin.wpc") || EndsWith(name, ".mdl.wpc"))
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
            return ScanPs2SceneFile(source, displayPath, rootDir, iskinStems: null);

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
                fileName.EndsWith(".skin.wpc", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".mdl.wpc", StringComparison.OrdinalIgnoreCase))
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
                PsxHasHierarchy = psxFile.HasHierarchy,
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
            if (ThawSceneFile.IsThawScene(data))
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
        var fileSystemPath = source.FileSystemPath;
        if (fileSystemPath == null) return null; // Worldzone requires a real PAK path

        try
        {
            if (!Ps2WorldzoneDetection.IsWorldzonePak(fileSystemPath))
                return null;

            var typed = PakArchive.GetTypedEntries(fileSystemPath);
            var mdlCount = typed.Count(e =>
                e.TypeHash == Ps2WorldzoneDetection.WorldzoneMdlTypeHash
                || e.TypeHash == Ps2WorldzoneDetection.WorldzoneLevelMdlTypeHash);
            var placementCount = typed.Count(e =>
                e.TypeHash == Ps2WorldzoneDetection.WorldzonePlacementTypeHash);

            return new MeshFileEntry
            {
                FileName = Path.GetFileName(fileSystemPath),
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
