using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool;

internal static class MeshConverterTabFileScanner
{
    private static readonly EnumerationOptions CaseInsensitiveEnumeration =
        new() { MatchCasing = MatchCasing.CaseInsensitive };

    private static readonly IComparer<string> FileNameComparer = Comparer<string>.Create(static (left, right) =>
        StringComparer.OrdinalIgnoreCase.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right)));

    private static readonly string[] CompoundExtensions =
    [
        ".iskin.ps2", ".skin.ps2", ".mdl.ps2", ".geom.ps2",
        ".skin.xbx", ".mdl.xbx", ".skin.wpc", ".mdl.wpc",
        ".col.xbx", ".col.wpc", ".col.ps2"
    ];

    private static readonly string[] ColSuffixes = [".col.xbx", ".col.wpc", ".col.ps2"];

    public static MeshScanSummary AnalyzeDirectory(string inputDir)
    {
        var allFiles = Directory.GetFiles(inputDir);
        return new MeshScanSummary(
            MeshConverterTabScanAnalysis.FindUnsupportedFiles(allFiles),
            MeshConverterTabScanAnalysis.CountPotentiallySupportedFiles(allFiles));
    }

    public static List<MeshFileEntry> ScanDirectory(string inputDir)
    {
        var entries = new List<MeshFileEntry>();

        var files = ClassifyFiles(inputDir);
        foreach (var file in files.DdmFiles)
            AddEntry(entries, ScanDdmFile(file));

        var ddmStems = files.DdmFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static stem => stem != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.PsxFiles.Where(file => !ddmStems.Contains(Path.GetFileNameWithoutExtension(file))))
            AddEntry(entries, ScanPsxFile(file));

        foreach (var file in files.RwDffFiles)
            AddEntry(entries, ScanRwDffFile(file));

        foreach (var file in files.RwBspFiles)
            AddEntry(entries, ScanRwBspFile(file));

        foreach (var file in files.ColFiles)
            AddEntry(entries, ScanColFile(file));

        foreach (var file in files.Ps2SceneFiles)
            AddEntry(entries, ScanPs2SceneFile(file, files.IskinStems));

        foreach (var file in files.Ps2GeomFiles)
            AddEntry(entries, ScanPs2GeomFile(file));

        foreach (var file in files.XbxSceneFiles)
            AddEntry(entries, ScanXbxSceneFile(file));

        foreach (var file in files.PakSceneFiles)
            AddEntry(entries, ScanPs2SceneFile(file));

        return entries;
    }

    internal static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension, CaseInsensitiveEnumeration);
        return files.Length > 0 ? files[0] : null;
    }

    internal static string StripCompoundExtension(string filename)
    {
        return OrdinalFileName.StripCompoundSuffix(filename, CompoundExtensions);
    }

    private static MeshScanFileBuckets ClassifyFiles(string inputDir)
    {
        var buckets = new MeshScanFileBuckets();
        foreach (var file in Directory.GetFiles(inputDir))
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
        }

        SortByFileName(buckets.DdmFiles);
        SortByFileName(buckets.PsxFiles);
        SortByFileName(buckets.RwDffFiles);
        SortByFileName(buckets.RwBspFiles);
        SortByFileName(buckets.ColFiles);
        SortByFileName(buckets.Ps2SceneFiles);
        SortByFileName(buckets.Ps2GeomFiles);
        SortByFileName(buckets.XbxSceneFiles);
        SortByFileName(buckets.PakSceneFiles);
        return buckets;
    }

    private static void SortByFileName(List<string> files)
    {
        files.Sort(FileNameComparer);
    }

    private static void AddEntry(List<MeshFileEntry> entries, MeshFileEntry? entry)
    {
        if (entry != null)
            entries.Add(entry);
    }

    private static bool IsColFilePath(string file)
    {
        var fileName = Path.GetFileName(file);
        return OrdinalFileName.HasAnySuffix(fileName, ColSuffixes) || OrdinalFileName.HasExtension(file, ".col");
    }

    private static MeshFileEntry? ScanDdmFile(string file)
    {
        try
        {
            var ddm = DdmFile.Parse(file);
            var directory = Path.GetDirectoryName(file)!;
            var stem = Path.GetFileNameWithoutExtension(file);
            var companionPsx = FindCompanionFile(directory, stem, ".psx");
            var companionObjectsDdm = companionPsx != null
                ? FindCompanionFile(directory, stem + "_o", ".ddm")
                : null;

            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = companionPsx != null ? "DDM (placed)" : "DDM",
                ObjectCount = ddm.Objects.Count,
                MeshCount = ddm.Objects.Count,
                CompanionPsxPath = companionPsx,
                CompanionObjectsDdmPath = companionObjectsDdm
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPsxFile(string file)
    {
        try
        {
            var psxFile = PsxMeshFile.Parse(file);
            if (psxFile == null)
                return null;

            string? companionLibraryPath = null;
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
            {
                var libraryStem = stem[..^2] + "_l";
                companionLibraryPath = FindCompanionFile(Path.GetDirectoryName(file)!, libraryStem, ".psx");
            }

            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = "PSX",
                ObjectCount = psxFile.Objects.Count,
                MeshCount = psxFile.Meshes.Count,
                CompanionLibraryPsxPath = companionLibraryPath
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanRwDffFile(string file)
    {
        try
        {
            var data = File.ReadAllBytes(file);
            if (!RwDffFile.IsDffFile(data))
                return null;

            var clump = RwDffFile.Parse(data);
            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = "RW DFF",
                ObjectCount = clump.Atomics.Length,
                MeshCount = clump.Geometries.Length
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanRwBspFile(string file)
    {
        try
        {
            var data = File.ReadAllBytes(file);
            if (!RwBspFile.IsBspFile(data))
                return null;

            var world = RwBspFile.Parse(data);
            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = "RW BSP",
                ObjectCount = world.Sections.Length,
                MeshCount = world.Materials.Length
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanColFile(string file)
    {
        try
        {
            var data = File.ReadAllBytes(file);
            if (!ColFile.IsColFile(data))
                return null;

            var scene = ColFile.Parse(data);
            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = "COL",
                ObjectCount = scene.Objects.Length,
                MeshCount = scene.Objects.Length
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPs2SceneFile(string file, HashSet<string>? iskinStems = null)
    {
        try
        {
            var data = File.ReadAllBytes(file);
            var fileName = Path.GetFileName(file);
            var lower = fileName.ToLowerInvariant();
            var directory = Path.GetDirectoryName(file)!;
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

            string? skeletonPath = null;
            if (lower.Contains(".skin", StringComparison.Ordinal))
                skeletonPath = ThawSkeletonDiscovery.FindSkeletonPath(
                    file,
                    stem,
                    subFormat == Ps2SceneSubFormat.ThawSkin);

            var texPath = CompanionSearch.FindCompanion(
                directory,
                stem,
                [".tex.ps2", ".tex", ".img.ps2"],
                ["TEX", "Textures", "IMG"]);

            return new MeshFileEntry
            {
                FileName = fileName,
                FilePath = file,
                Format = format,
                ObjectCount = objectCount,
                MeshCount = meshCount,
                Ps2SubFormat = subFormat,
                CompanionSkeletonPath = skeletonPath,
                CompanionTexPath = texPath
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPs2GeomFile(string file)
    {
        try
        {
            var scene = Ps2GeomFile.Parse(file);
            var fileName = Path.GetFileName(file);
            var stem = StripCompoundExtension(fileName);
            var texPath = CompanionSearch.FindCompanion(
                Path.GetDirectoryName(file)!,
                stem,
                [".tex.ps2", ".tex", ".img.ps2"],
                ["TEX", "Textures", "IMG"]);

            return new MeshFileEntry
            {
                FileName = fileName,
                FilePath = file,
                Format = "PS2 GEOM",
                ObjectCount = scene.Leaves.Count,
                MeshCount = scene.Leaves.Count,
                Ps2SubFormat = Ps2SceneSubFormat.Geom,
                CompanionTexPath = texPath
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanXbxSceneFile(string file)
    {
        try
        {
            var data = File.ReadAllBytes(file);
            var fileName = Path.GetFileName(file);
            var directory = Path.GetDirectoryName(file)!;
            var stem = StripCompoundExtension(fileName);

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

            var texPath = CompanionSearch.FindCompanion(
                directory,
                stem,
                [".tex.xbx", ".tex.wpc"],
                ["TEX", "Textures"]);

            return new MeshFileEntry
            {
                FileName = fileName,
                FilePath = file,
                Format = format,
                ObjectCount = scene.Sectors.Length,
                MeshCount = scene.Materials.Length,
                CompanionTexPath = texPath
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
        public List<string> Ps2GeomFiles { get; } = [];
        public List<string> Ps2SceneFiles { get; } = [];
        public List<string> PsxFiles { get; } = [];
        public List<string> RwBspFiles { get; } = [];
        public List<string> RwDffFiles { get; } = [];
        public List<string> XbxSceneFiles { get; } = [];
    }
}
