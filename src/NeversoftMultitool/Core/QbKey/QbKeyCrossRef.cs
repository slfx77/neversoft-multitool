using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Core.QbKey;

/// <summary>
///     Cross-references DDM plaintext names against PSX QBKey hashes
///     to discover name-to-hash mappings.
/// </summary>
public static class QbKeyCrossRef
{
    public static CrossRefResult Run(string ddmDir, string psxDir)
    {
        var ddmFiles = Directory.GetFiles(ddmDir, "*.ddm",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        var psxFiles = Directory.GetFiles(psxDir, "*.psx",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });

        // Index PSX files by base filename (case-insensitive)
        var psxByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var psx in psxFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(psx);
            psxByName.TryAdd(baseName, psx);
        }

        var fileResults = new List<CrossRefFileResult>();
        var allMappings = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var totalDdmNames = 0;
        var totalPsxHashes = 0;
        var totalMatches = 0;
        var totalMeshHashes = 0;
        var totalTextureHashes = 0;
        var totalMeshMatches = 0;
        var totalTextureMatches = 0;

        foreach (var ddmFile in ddmFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(ddmFile);
            if (!psxByName.TryGetValue(baseName, out var psxFile))
                continue;

            var result = CrossReferenceFilePair(ddmFile, psxFile);
            fileResults.Add(result);

            totalDdmNames += result.DdmNameCount;
            totalPsxHashes += result.PsxHashCount;
            totalMatches += result.Matches.Count;
            totalMeshHashes += result.MeshHashCount;
            totalTextureHashes += result.TextureHashCount;
            totalMeshMatches += result.MeshMatches;
            totalTextureMatches += result.TextureMatches;

            foreach (var match in result.Matches)
                allMappings.TryAdd(match.Name.ToLowerInvariant(), match.Hash);
        }

        var newDiscoveries = allMappings.Count(kv => QbKey.TryResolve(kv.Value) == null);

        return new CrossRefResult
        {
            FileResults = fileResults,
            AllDiscoveredMappings = allMappings,
            TotalDdmFiles = ddmFiles.Length,
            TotalPsxFiles = psxFiles.Length,
            MatchedFilePairs = fileResults.Count,
            TotalDdmNames = totalDdmNames,
            TotalPsxHashes = totalPsxHashes,
            TotalMatches = totalMatches,
            NewDiscoveries = newDiscoveries,
            TotalMeshHashes = totalMeshHashes,
            TotalTextureHashes = totalTextureHashes,
            TotalMeshMatches = totalMeshMatches,
            TotalTextureMatches = totalTextureMatches
        };
    }

    /// <summary>
    ///     Scans archive-sourced candidate names against PSX hashes.
    ///     Focuses on texture hash matching (the 0% coverage gap).
    /// </summary>
    public static ArchiveScanResult ScanArchiveNames(string buildsPath, string psxDir)
    {
        var candidates = CollectNamesFromArchives(buildsPath, out var archivesScanned, out var errors);
        var (textureHashes, meshHashes) = CollectAllPsxHashes(psxDir);

        var textureMatches = MatchCandidatesAgainstPool(candidates, textureHashes);
        var meshMatches = MatchCandidatesAgainstPool(candidates, meshHashes);

        var allHashes = new HashSet<uint>(textureMatches.Select(m => m.Hash));
        allHashes.UnionWith(meshMatches.Select(m => m.Hash));
        var newDiscoveries = allHashes.Count(h => QbKey.TryResolve(h) == null);

        return new ArchiveScanResult
        {
            TextureMatches = textureMatches,
            MeshMatches = meshMatches,
            TotalCandidateNames = candidates.Count,
            TotalTextureHashes = textureHashes.Count,
            TotalMeshHashes = meshHashes.Count,
            NewDiscoveries = newDiscoveries,
            ArchivesScanned = archivesScanned,
            ArchiveErrors = errors
        };
    }

    private static List<QbKeyMapping> MatchCandidatesAgainstPool(
        HashSet<string> candidates, HashSet<uint> hashPool)
    {
        var matches = new List<QbKeyMapping>();
        foreach (var name in candidates)
        {
            var hash = QbKey.Hash(name);
            if (hashPool.Contains(hash))
            {
                matches.Add(new QbKeyMapping
                {
                    Name = name,
                    Hash = hash,
                    SourceFile = "archive-scan",
                    Source = QbKeyMappingSource.ArchiveFilename
                });
            }
        }

        return matches;
    }

    /// <summary>
    ///     Collects plaintext filenames from all supported archive formats under a builds directory.
    ///     Returns filenames and stems as candidate names for QBKey matching.
    /// </summary>
    public static HashSet<string> CollectNamesFromArchives(string buildsPath,
        out int archivesScanned, out int errors)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        archivesScanned = 0;
        errors = 0;

        var searchOptions = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = true
        };

        ScanArchiveType(buildsPath, "*.WAD", searchOptions, WadArchive.GetFileList,
            candidates, ref archivesScanned, ref errors);
        ScanArchiveType(buildsPath, "*.PRE", searchOptions, PreArchive.GetFileList,
            candidates, ref archivesScanned, ref errors);
        ScanArchiveType(buildsPath, "*.BON", searchOptions, BonArchive.GetFileList,
            candidates, ref archivesScanned, ref errors);
        ScanArchiveType(buildsPath, "*.PKR", searchOptions, PkrArchive.GetFileList,
            candidates, ref archivesScanned, ref errors);
        ScanArchiveType(buildsPath, "*.DDX", searchOptions, DdxArchive.GetFileList,
            candidates, ref archivesScanned, ref errors);

        return candidates;
    }

    private static void ScanArchiveType(string buildsPath, string pattern,
        EnumerationOptions searchOptions, Func<string, List<ArchiveEntry>> getFileList,
        HashSet<string> candidates, ref int archivesScanned, ref int errors)
    {
        foreach (var file in Directory.EnumerateFiles(buildsPath, pattern, searchOptions))
        {
            try
            {
                var entries = getFileList(file);
                foreach (var entry in entries)
                    AddCandidateNames(candidates, entry.Name);
                archivesScanned++;
            }
            catch
            {
                errors++;
            }
        }
    }

    /// <summary>
    ///     Collects all unique mesh and texture name hashes from PSX files in a directory.
    ///     Searches recursively to support builds directory structures.
    /// </summary>
    public static (HashSet<uint> TextureHashes, HashSet<uint> MeshHashes) CollectAllPsxHashes(string psxDir)
    {
        var textureHashes = new HashSet<uint>();
        var meshHashes = new HashSet<uint>();

        foreach (var psxFile in Directory.EnumerateFiles(psxDir, "*.psx",
                     new EnumerationOptions
                     {
                         MatchCasing = MatchCasing.CaseInsensitive,
                         RecurseSubdirectories = true
                     }))
        {
            try
            {
                var hashes = PsxHashEnumerator.EnumerateAllHashes(psxFile);
                if (hashes == null) continue;

                textureHashes.UnionWith(hashes.TextureNameHashes);
                meshHashes.UnionWith(hashes.MeshNameHashes);
            }
            catch
            {
                /* Skip unreadable files */
            }
        }

        return (textureHashes, meshHashes);
    }

    private static void AddCandidateNames(HashSet<string> candidates, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return;

        // Get just the filename part (some archives include path separators)
        var name = Path.GetFileName(filename);
        if (string.IsNullOrEmpty(name))
            return;

        // Add the full filename (e.g. "barearm1.bmp")
        candidates.Add(name);

        // Add the stem without extension (e.g. "barearm1")
        var stem = Path.GetFileNameWithoutExtension(name);
        if (!string.IsNullOrEmpty(stem) && stem != name)
            candidates.Add(stem);
    }

    private static CrossRefFileResult CrossReferenceFilePair(string ddmPath, string psxPath)
    {
        var ddm = DdmFile.Parse(ddmPath);
        var psxHashes = PsxHashEnumerator.EnumerateAllHashes(psxPath);

        // Collect all unique plaintext names from DDM with their source types
        var nameToSource = new Dictionary<string, QbKeyMappingSource>(StringComparer.Ordinal);

        foreach (var obj in ddm.Objects)
        {
            if (!string.IsNullOrEmpty(obj.Name))
                nameToSource.TryAdd(obj.Name, QbKeyMappingSource.ObjectName);

            foreach (var mat in obj.Materials)
            {
                if (!string.IsNullOrEmpty(mat.TextureName))
                    nameToSource.TryAdd(mat.TextureName, QbKeyMappingSource.TextureName);
                if (!string.IsNullOrEmpty(mat.Name))
                    nameToSource.TryAdd(mat.Name, QbKeyMappingSource.MaterialName);
            }
        }

        // Build separate sets for mesh and texture PSX hashes
        var meshHashes = new HashSet<uint>();
        var textureHashes = new HashSet<uint>();
        if (psxHashes != null)
        {
            foreach (var h in psxHashes.MeshNameHashes)
                meshHashes.Add(h);
            foreach (var h in psxHashes.TextureNameHashes)
                textureHashes.Add(h);
        }

        var allPsxHashes = new HashSet<uint>(meshHashes);
        allPsxHashes.UnionWith(textureHashes);

        // Also check v6 extended header names against PSX hashes
        if (psxHashes?.DetailTextureNames != null)
        {
            foreach (var name in psxHashes.DetailTextureNames)
            {
                if (!string.IsNullOrEmpty(name))
                    nameToSource.TryAdd(name, QbKeyMappingSource.DetailTextureName);
            }
        }

        if (psxHashes?.CubemapNames != null)
        {
            foreach (var name in psxHashes.CubemapNames)
            {
                if (!string.IsNullOrEmpty(name))
                    nameToSource.TryAdd(name, QbKeyMappingSource.CubemapName);
            }
        }

        // Cross-reference: hash each name, check if hash exists in PSX
        var matches = new List<QbKeyMapping>();
        var unmatchedDdm = new List<string>();
        var matchedPsxHashes = new HashSet<uint>();
        var matchedMeshCount = 0;
        var matchedTextureCount = 0;
        var ddmFile = Path.GetFileName(ddmPath);

        foreach (var (name, source) in nameToSource)
        {
            var hash = QbKey.Hash(name);
            if (allPsxHashes.Contains(hash))
            {
                matches.Add(new QbKeyMapping
                {
                    Name = name,
                    Hash = hash,
                    SourceFile = ddmFile,
                    Source = source
                });
                matchedPsxHashes.Add(hash);

                if (meshHashes.Contains(hash)) matchedMeshCount++;
                if (textureHashes.Contains(hash)) matchedTextureCount++;
            }
            else
            {
                unmatchedDdm.Add(name);
            }
        }

        var unmatchedPsx = allPsxHashes
            .Where(h => !matchedPsxHashes.Contains(h))
            .OrderBy(h => h)
            .ToList();

        return new CrossRefFileResult
        {
            DdmFile = ddmFile,
            PsxFile = Path.GetFileName(psxPath),
            Matches = matches,
            UnmatchedDdmNames = unmatchedDdm,
            UnmatchedPsxHashes = unmatchedPsx,
            DdmNameCount = nameToSource.Count,
            PsxHashCount = allPsxHashes.Count,
            MeshHashCount = meshHashes.Count,
            TextureHashCount = textureHashes.Count,
            MeshMatches = matchedMeshCount,
            TextureMatches = matchedTextureCount
        };
    }
}
