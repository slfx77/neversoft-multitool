using System.Diagnostics;
using NeversoftMultitool.Core;
using static SampleGenerator.SampleGeneratorConfig;
using static SampleGenerator.SampleGeneratorDiscOperations;

namespace SampleGenerator;

internal static class SampleGeneratorBuildOperations
{
    /// <summary>
    /// End-to-end pipeline for one build:
    /// 1. Populate ResearchBuilds/{name}/ from disc image, raw directory, or MSI installer (cached if present;
    ///    <paramref name="repopulate" /> wipes the cache so Media/{name}/ disc images re-extract with current code).
    /// 2. Mirror ResearchBuilds/{name}/ → SampleBuilds/{name}/ verbatim.
    /// 3. Run RecursiveUnpacker against SampleBuilds/{name}/ so archives unpack in-place.
    /// </summary>
    internal static BuildResult RunBuild(string name, string? searchSubdir, BuildMode mode,
        bool repopulate = false)
    {
        var researchDir = SampleGeneratorPathSafety.ResolveDestinationPath(ResearchBuilds, name);
        var sampleDir = SampleGeneratorPathSafety.ResolveDestinationPath(SampleBuilds, name);

        if (repopulate)
            DeleteTree(researchDir);

        if (NeedsResearchPopulation(researchDir, mode))
        {
            var populated = PopulateResearchBuild(name, searchSubdir, mode, researchDir);
            if (populated.Missing) return populated;
        }

        // Strip stale Extracted/ left behind by the previous classification-based generator.
        var legacyExtractedDir = SampleGeneratorPathSafety.ResolveDestinationPath(
            researchDir,
            LegacyExtractedDirName);
        DeleteTree(legacyExtractedDir);

        if (!Directory.Exists(researchDir))
            return new BuildResult(name, 0, 0, Missing: true);

        var fileCount = MirrorTree(researchDir, sampleDir);

        // Unpack archives in-place inside SampleBuilds (matches the unpack CLI/tab behavior).
        var unpacked = RecursiveUnpacker.ExtractAll(sampleDir);
        var archivesExtracted = unpacked.Count(a => a.Extracted);

        if (archivesExtracted > 0)
            fileCount = Directory.EnumerateFiles(sampleDir, "*", SearchOption.AllDirectories).Count();

        return new BuildResult(name, fileCount, archivesExtracted, Missing: false);
    }

    /// <summary>
    /// Recursive file copy from src → dest. Wipes dest first.
    /// </summary>
    internal static int MirrorTree(string srcDir, string destDir)
    {
        destDir = SampleGeneratorPathSafety.ResolveConfiguredOutputDirectory(destDir);
        DeleteTree(destDir);
        Directory.CreateDirectory(destDir);

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(srcDir, file);
            var targetPath = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
            // File.Copy preserves the read-only attribute (MSI payloads ship
            // them), which breaks the NEXT regeneration's wipe — normalize.
            File.SetAttributes(targetPath, FileAttributes.Normal);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Deletes a per-build cache/output tree after proving that it is a strict
    /// descendant of one of the configured writable roots. Reparse points are
    /// rejected so normalization cannot escape through a junction or symlink.
    /// </summary>
    internal static void DeleteTree(string dir)
    {
        DeleteTreeUnder(dir, [ResearchBuilds, SampleBuilds]);
    }

    private static void DeleteTemporaryTree(string dir)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (!Path.GetFileName(fullPath).StartsWith("NsMultitool_MsiExpand_", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to delete an unexpected temporary directory: {fullPath}");

        DeleteTreeUnder(fullPath, [Path.GetTempPath()]);
    }

    private static void DeleteTreeUnder(string dir, IReadOnlyCollection<string> allowedRoots)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var allowedRoot = allowedRoots.FirstOrDefault(
            root => SampleGeneratorPathSafety.IsStrictDescendant(target, root));
        if (allowedRoot == null)
        {
            throw new InvalidOperationException(
                $"Refusing recursive deletion outside configured output roots: {target}");
        }

        SampleGeneratorPathSafety.RejectReparseTraversal(allowedRoot, target);
        if (!Directory.Exists(target))
            return;

        NormalizeTreeForDeletion(target);
        Directory.Delete(target, recursive: true);
    }

    private static void NormalizeTreeForDeletion(string target)
    {
        var pending = new Stack<string>();
        pending.Push(target);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Refusing to delete through a reparse point: {directory}");

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException($"Refusing to delete a tree containing a reparse point: {entry}");
                    pending.Push(entry);
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
            }
        }
    }

    private static bool NeedsResearchPopulation(string researchDir, BuildMode mode)
    {
        if (!Directory.Exists(researchDir)) return true;

        if (mode == BuildMode.Msi)
            return !Directory.Exists(
                SampleGeneratorPathSafety.ResolveDestinationPath(researchDir, InstalledDirName));

        // For non-MSI builds, treat the build as populated only when researchDir has content
        // beyond the GameCube System/ holdover (a partial extract from a prior aborted run).
        var entries = Directory.EnumerateFileSystemEntries(researchDir).ToList();
        if (entries.Count == 0) return true;
        if (entries.Count == 1 &&
            string.Equals(Path.GetFileName(entries[0]), SystemDirName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static BuildResult PopulateResearchBuild(string name, string? searchSubdir, BuildMode mode, string destDir)
    {
        return mode switch
        {
            BuildMode.Msi => PopulateBuildFromMsiMedia(name, destDir),
            _ => PopulateBuildFromMedia(name, searchSubdir, destDir),
        };
    }

    private static BuildResult PopulateBuildFromMedia(string name, string? searchSubdir, string destDir)
    {
        var discImages = FindDiscImagePaths(name);
        if (discImages.Count > 0)
        {
            var count = ExtractBuildFromDiscImages(discImages, destDir);
            return new BuildResult(name, count, 0, Missing: count == 0);
        }

        var mediaDir = Path.Combine(ResearchMedia, name);
        return PopulateBuildFromDirectory(name, mediaDir, destDir, searchSubdir);
    }

    private static BuildResult PopulateBuildFromDirectory(string name, string srcDir, string destDir, string? searchSubdir)
    {
        var searchRoot = searchSubdir != null ? Path.Combine(srcDir, searchSubdir) : srcDir;

        if (!Directory.Exists(searchRoot))
            return new BuildResult(name, 0, 0, Missing: true);

        var count = MirrorTree(searchRoot, destDir);
        return new BuildResult(name, count, 0, Missing: false);
    }

    // -------------------------------------------------------------------
    // MSI installer expansion (Windows PC builds)
    // -------------------------------------------------------------------

    /// <summary>
    /// Expands an MSI installer into outputDir by reading the MSI File table
    /// (via PowerShell COM) for GUID→filename mapping, extracting .cab archives
    /// with 7z, then renaming GUID-named files to their real names.
    /// </summary>
    internal static MsiInstallResult ExpandMsiInstaller(string name, string sourceDir, string outputDir)
    {
        var msiFile = Directory.GetFiles(sourceDir, "*.msi", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
        if (msiFile == null)
            return new MsiInstallResult(false, 0, null);

        outputDir = SampleGeneratorPathSafety.ResolveConfiguredOutputDirectory(outputDir);
        var tempDir = Directory.CreateTempSubdirectory("NsMultitool_MsiExpand_").FullName;

        try
        {
            var cabExtractDir = SampleGeneratorPathSafety.ResolveDestinationPath(tempDir, "cabs");
            Directory.CreateDirectory(cabExtractDir);

            if (Directory.Exists(outputDir))
                DeleteTree(outputDir);
            Directory.CreateDirectory(outputDir);

            var fileMap = ReadMsiFileTable(msiFile);
            if (fileMap.Count == 0)
            {
                Console.Error.WriteLine($"    MSI File table empty for {name}");
                return new MsiInstallResult(false, 0, null);
            }

            var cabFiles = Directory.GetFiles(sourceDir, "*.cab", SearchOption.AllDirectories);
            foreach (var cab in cabFiles)
            {
                var cabDest = SampleGeneratorPathSafety.ResolveDestinationPath(
                    cabExtractDir,
                    Path.GetFileName(cab));
                if (!File.Exists(cabDest))
                    File.Copy(cab, cabDest);
            }

            foreach (var cab in Directory.GetFiles(cabExtractDir, "*.cab"))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "7z",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // MSI cabinets use opaque File-table keys, so flattening them
                // avoids honoring any archive-supplied directory components.
                psi.ArgumentList.Add("e");
                psi.ArgumentList.Add(cab);
                psi.ArgumentList.Add($"-o{cabExtractDir}");
                psi.ArgumentList.Add("-y");

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Could not start 7z.");
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                var output = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        $"7z failed to extract {Path.GetFileName(cab)}: {error}{output}");
                }
            }

            var installedFiles = 0;
            foreach (var guidFile in Directory.EnumerateFiles(cabExtractDir))
            {
                var guidName = Path.GetFileName(guidFile);
                if (!fileMap.TryGetValue(guidName, out var relativePath))
                    continue;

                var targetPath = SampleGeneratorPathSafety.ResolveDestinationPath(outputDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(guidFile, targetPath, overwrite: true);
                installedFiles++;
            }

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".cab", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".mst", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetPath = SampleGeneratorPathSafety.ResolveDestinationPath(
                    outputDir,
                    Path.GetFileName(file));
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath);
                    installedFiles++;
                }
            }

            var dataDir = Directory.EnumerateDirectories(outputDir, "Data", SearchOption.AllDirectories)
                .FirstOrDefault();
            var searchSubdir = dataDir != null ? Path.GetRelativePath(outputDir, dataDir) : null;
            return new MsiInstallResult(true, installedFiles, searchSubdir);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { DeleteTemporaryTree(tempDir); }
                catch { /* cleanup failure is non-fatal */ }
            }
        }
    }

    /// <summary>
    /// Reads the MSI File table via PowerShell WindowsInstaller.Installer COM object.
    /// Returns a dictionary mapping GUID file keys to relative paths under the install dir.
    /// </summary>
    internal static Dictionary<string, string> ReadMsiFileTable(string msiPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const string psTemplate = """
            $installer = New-Object -ComObject WindowsInstaller.Installer
            $db = $installer.OpenDatabase($args[0], 0)

            # Read Directory tree
            $dirs = @{}
            $v = $db.OpenView('SELECT Directory, Directory_Parent, DefaultDir FROM Directory')
            $v.Execute()
            $r = $v.Fetch()
            while ($r -ne $null) {
                $k = $r.StringData(1); $p = $r.StringData(2); $d = $r.StringData(3)
                if ($d.Contains('|')) { $d = $d.Split('|')[1] }
                if ($d.Contains(':')) { $d = $d.Split(':')[0] }
                $dirs[$k] = @($p, $d)
                $r = $v.Fetch()
            }
            $v.Close()

            # Read Component -> Directory mapping
            $comps = @{}
            $v2 = $db.OpenView('SELECT Component, Directory_ FROM Component')
            $v2.Execute()
            $r2 = $v2.Fetch()
            while ($r2 -ne $null) {
                $comps[$r2.StringData(1)] = $r2.StringData(2)
                $r2 = $v2.Fetch()
            }
            $v2.Close()

            # Resolve directory path
            function Get-DirPath($key, $visited) {
                if (-not $key -or $visited.Contains($key) -or -not $dirs.ContainsKey($key)) { return '' }
                $visited.Add($key) | Out-Null
                $parent = $dirs[$key][0]; $name = $dirs[$key][1]
                if ($name -eq 'SourceDir' -or $name -eq '.') { $name = '' }
                $pp = Get-DirPath $parent $visited
                if ($pp -and $name) { return "$pp/$name" }
                return $(if ($pp) { $pp } else { $name })
            }

            # Read File table
            $v3 = $db.OpenView('SELECT File, Component_, FileName FROM File')
            $v3.Execute()
            $r3 = $v3.Fetch()
            while ($r3 -ne $null) {
                $guid = $r3.StringData(1)
                $comp = $r3.StringData(2)
                $fn = $r3.StringData(3)
                if ($fn.Contains('|')) { $fn = $fn.Split('|')[1] }
                $dirKey = $comps[$comp]
                $dirPath = Get-DirPath $dirKey (New-Object System.Collections.ArrayList)
                if ($dirPath) { Write-Output "$guid`t$dirPath/$fn" }
                else { Write-Output "$guid`t$fn" }
                $r3 = $v3.Fetch()
            }
            $v3.Close()
            """;

        var psFile = SampleGeneratorPathSafety.ResolveDestinationPath(
            Path.GetTempPath(),
            $"NsMultitool_ReadMsi_{Guid.NewGuid():N}.ps1");
        var ownsPsFile = false;

        try
        {
            using (var stream = new FileStream(psFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPsFile = true;
                using var writer = new StreamWriter(stream);
                writer.Write(psTemplate);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(psFile);
            psi.ArgumentList.Add(msiPath);

            var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('\t', 2);
                if (parts.Length == 2)
                    map[parts[0]] = parts[1].Replace('/', Path.DirectorySeparatorChar);
            }
        }
        finally
        {
            if (ownsPsFile)
            {
                try { File.Delete(psFile); } catch { }
            }
        }

        return map;
    }

    internal record struct MsiInstallResult(bool Success, int InstalledFiles, string? SearchSubdir);

    private static BuildResult PopulateBuildFromMsiMedia(string name, string destDir)
    {
        // Case A: ResearchBuilds/{name}/ already has raw .msi/.cab files (e.g. THUG2 PC ships
        // as a directory of installer files, not a disc image). Expand them in-place into Installed/.
        if (Directory.Exists(destDir) &&
            Directory.GetFiles(destDir, "*.msi", SearchOption.AllDirectories).Length > 0)
        {
            var installedDir = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, InstalledDirName);
            var installResult = ExpandMsiInstaller(name, destDir, installedDir);
            return installResult.Success
                ? new BuildResult(name, installResult.InstalledFiles, 0, Missing: false)
                : new BuildResult(name, 0, 0, Missing: true);
        }

        // Case B: disc images in ResearchMedia (e.g. THAW PC ships as multi-disc CDs).
        // Extract them first, then expand the .msi files from the extracted contents.
        var discImages = FindDiscImagePaths(name);
        if (discImages.Count == 0)
            return new BuildResult(name, 0, 0, Missing: true);

        var rawCount = ExtractBuildFromDiscImages(discImages, destDir);
        if (rawCount == 0)
            return new BuildResult(name, 0, 0, Missing: true);

        var installedDir2 = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, InstalledDirName);
        var installResult2 = ExpandMsiInstaller(name, destDir, installedDir2);
        if (!installResult2.Success)
            return new BuildResult(name, rawCount, 0, Missing: true);

        return new BuildResult(name, rawCount + installResult2.InstalledFiles, 0, Missing: false);
    }
}
