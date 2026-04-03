using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool;

internal sealed record MeshScanSummary(IReadOnlyList<ScanSummaryDialog.UnsupportedFile> UnsupportedFiles, int SupportedCount);

internal static class MeshConverterTabFileScanner
{
    private static readonly EnumerationOptions CaseInsensitiveEnumeration = new() { MatchCasing = MatchCasing.CaseInsensitive };
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

        var ddmFiles = Directory.GetFiles(inputDir, "*.ddm", CaseInsensitiveEnumeration)
            .Where(static file => !Path.GetFileNameWithoutExtension(file)
                .EndsWith("_o", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in ddmFiles)
            AddEntry(entries, ScanDdmFile(file));

        var ddmStems = ddmFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static stem => stem != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var psxFiles = Directory.GetFiles(inputDir, "*.psx", CaseInsensitiveEnumeration)
            .Where(file => !ddmStems.Contains(Path.GetFileNameWithoutExtension(file)))
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

        foreach (var file in psxFiles)
            AddEntry(entries, ScanPsxFile(file));

        var sknFiles = Directory.GetFiles(inputDir, "*.SKN", CaseInsensitiveEnumeration)
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in sknFiles)
            AddEntry(entries, ScanRwDffFile(file));

        var bspFiles = Directory.GetFiles(inputDir, "*.bsp", CaseInsensitiveEnumeration)
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in bspFiles)
            AddEntry(entries, ScanRwBspFile(file));

        var colFiles = Directory.GetFiles(inputDir, "*", CaseInsensitiveEnumeration)
            .Where(static file => IsColFilePath(file))
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in colFiles)
            AddEntry(entries, ScanColFile(file));

        var iskinStems = Directory.GetFiles(inputDir, "*", CaseInsensitiveEnumeration)
            .Where(static file => Path.GetFileName(file).EndsWith(".iskin.ps2", StringComparison.OrdinalIgnoreCase))
            .Select(static file => StripCompoundExtension(Path.GetFileName(file)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ps2SceneFiles = Directory.GetFiles(inputDir, "*", CaseInsensitiveEnumeration)
            .Where(static file =>
            {
                var fileName = Path.GetFileName(file);
                return fileName.EndsWith(".skin.ps2", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".mdl.ps2", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".iskin.ps2", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in ps2SceneFiles)
            AddEntry(entries, ScanPs2SceneFile(file, iskinStems));

        var ps2GeomFiles = Directory.GetFiles(inputDir, "*", CaseInsensitiveEnumeration)
            .Where(static file => Path.GetFileName(file).EndsWith(".geom.ps2", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in ps2GeomFiles)
            AddEntry(entries, ScanPs2GeomFile(file));

        var xbxSceneFiles = Directory.GetFiles(inputDir, "*", CaseInsensitiveEnumeration)
            .Where(static file =>
            {
                var fileName = Path.GetFileName(file);
                return fileName.EndsWith(".skin.xbx", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".mdl.xbx", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".skin.wpc", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".mdl.wpc", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in xbxSceneFiles)
            AddEntry(entries, ScanXbxSceneFile(file));

        var pakSceneFiles = Directory.GetFiles(inputDir, "*", CaseInsensitiveEnumeration)
            .Where(static file => OrdinalFileName.HasExtension(file, ".skin") || OrdinalFileName.HasExtension(file, ".mdl"))
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        foreach (var file in pakSceneFiles)
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
}
