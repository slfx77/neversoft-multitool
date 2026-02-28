namespace NeversoftMultitool.Core;

/// <summary>
///     Scans .psh header files for mesh part names and matches them against PSX mesh hashes.
/// </summary>
internal static class PshScanner
{
    /// <summary>
    ///     Scans .psh header files under a builds directory for mesh part names,
    ///     then matches them against PSX mesh hashes.
    /// </summary>
    public static PshScanResult ScanPshNames(string buildsPath, string psxDir)
    {
        var meshHashes = QbKeyCrossRef.CollectAllPsxHashes(psxDir).MeshHashes;
        return ScanPshNames(buildsPath, meshHashes);
    }

    /// <summary>
    ///     Scans .psh header files under a builds directory for mesh part names,
    ///     then matches them against the provided mesh hash pool.
    /// </summary>
    public static PshScanResult ScanPshNames(string buildsPath, HashSet<uint> meshHashes)
    {
        var pshFiles = Directory.GetFiles(buildsPath, "*.psh",
            new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                RecurseSubdirectories = true
            });

        // Collect all candidate names across all .psh files
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pshFile in pshFiles)
        {
            var entries = ParsePshFile(pshFile);
            foreach (var entry in entries)
                AddPshCandidates(candidates, entry);
        }

        // Match candidates against mesh hash pool
        var matches = new List<QbKeyMapping>();
        foreach (var (_, name) in candidates)
        {
            var hash = QbKey.Hash(name);
            if (meshHashes.Contains(hash))
            {
                matches.Add(new QbKeyMapping
                {
                    Name = name,
                    Hash = hash,
                    SourceFile = "psh-scan",
                    Source = QbKeyMappingSource.PshPartName
                });
            }
        }

        var newDiscoveries = matches.Count(m => QbKey.TryResolve(m.Hash) == null);

        return new PshScanResult
        {
            Matches = matches,
            TotalPshFiles = pshFiles.Length,
            TotalCandidateNames = candidates.Count,
            TotalMeshHashes = meshHashes.Count,
            NewDiscoveries = newDiscoveries
        };
    }

    private static List<PshEntry> ParsePshFile(string path)
    {
        var entries = new List<PshEntry>();
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();

            if (!line.StartsWith("#define ", StringComparison.Ordinal))
                continue;

            // Extract part name after PART_ prefix
            var partIdx = line.IndexOf("PART_", StringComparison.Ordinal);
            if (partIdx < 0) continue;
            var afterPart = line[(partIdx + 5)..];
            var endIdx = afterPart.IndexOfAny([' ', '\t']);
            var defineName = endIdx > 0 ? afterPart[..endIdx] : afterPart;

            var parentName = i + 1 < lines.Length
                ? TryExtractParentName(lines[i + 1])
                : null;

            entries.Add(new PshEntry(defineName, parentName));
        }

        return entries;
    }

    private static string? TryExtractParentName(string line)
    {
        const string prefix = "//   parent: ";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var candidate = trimmed[prefix.Length..].Trim();
        return candidate.Equals("Scene Root", StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
    }

    /// <summary>
    ///     Adds candidate names from a PshEntry to the dictionary.
    ///     Parent names take priority for case recovery.
    /// </summary>
    private static void AddPshCandidates(Dictionary<string, string> candidates, PshEntry entry)
    {
        // Parent name is the authoritative case source
        if (entry.ParentName != null)
        {
            // Parent name always wins for case
            candidates[entry.ParentName.ToLowerInvariant()] = entry.ParentName;
        }

        var lowerDefine = entry.DefineName.ToLowerInvariant();

        // Only add define-derived names if no parent name already covers this key
        if (!candidates.ContainsKey(lowerDefine))
        {
            // Try lowercased form (THPS convention: hawk_pelvis)
            candidates[lowerDefine] = lowerDefine;
        }

        // Also try the uppercase form as a separate candidate
        // (in case some PSX files hash uppercase names)
        if (!candidates.ContainsKey(entry.DefineName))
            candidates.TryAdd(entry.DefineName, entry.DefineName);
    }

    private sealed record PshEntry(string DefineName, string? ParentName);
}
