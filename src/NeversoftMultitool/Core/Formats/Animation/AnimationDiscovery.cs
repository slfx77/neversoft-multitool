using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Locates SKA animation files associated with a given character (skinned
///     mesh). Used by the Character Preview tab to populate its animation list
///     after the user selects a character.
/// </summary>
internal static class AnimationDiscovery
{
    /// <summary>
    ///     Anim subdirs walked during ancestor search. Mirrors the conventions
    ///     used by <c>SkaCommand.FindCompressTable</c>:
    ///     <list type="bullet">
    ///         <item><c>Bits/anims/</c> — THPS4 PS2 (<c>pre/Bits/anims/</c>), THUG PS2 (<c>Pre/Bits/anims/</c>)</item>
    ///         <item><c>anims/</c> — THUG2/THAW/P8/PG PS2 (<c>DATAP/anims/</c>), THAW GC</item>
    ///         <item><c>bits/anims/</c> — THUG2 nested (<c>DATAP/pre/bits/anims/</c>)</item>
    ///         <item><c>pre/anims/</c> — THPS4-era characters that store anims under their own subtree</item>
    ///     </list>
    /// </summary>
    private static readonly string[] AnimSubdirNames =
        ["anims", "Anims", "Bits/anims", "bits/anims", "pre/anims", "Pre/anims"];

    /// <summary>
    ///     Walk the character's parent directories looking for known animation
    ///     subdirs and probe every <c>.ska*</c> file under them. PS1 character
    ///     <c>.psx</c> files are special-cased: their animations may be embedded
    ///     in the same file or ship in sibling animation-bank <c>.psx</c> files.
    /// </summary>
    public static IReadOnlyList<AnimationProbe> FindForCharacter(
        AssetSource skinSource, int? skeletonBoneCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (IsN64Character(skinSource))
        {
            ct.ThrowIfCancellationRequested();
            return N64EmbeddedAnimationDiscovery.CreateProbes(skinSource);
        }

        if (IsPsxCharacter(skinSource))
            return FindForPsxCharacter(skinSource, ct);

        var fsPath = skinSource.FileSystemPath;
        if (fsPath == null)
        {
            // Archive-backed characters: scan the same archive (THPS3 packs a
            // character's Models/<char>/ and Anims/<char>/ in one PRE).
            return skinSource is ArchiveAssetSource archiveSource
                ? FindForArchiveCharacter(archiveSource, skeletonBoneCount, ct)
                : [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AnimationProbe>();

        var dir = Path.GetDirectoryName(Path.GetFullPath(fsPath));
        while (!string.IsNullOrEmpty(dir))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var subdir in AnimSubdirNames)
            {
                var animRoot = Path.Combine(dir, subdir);
                if (!Directory.Exists(animRoot)) continue;
                AddFromDirectory(animRoot, skeletonBoneCount, seen, results, ct);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return results;
    }

    /// <summary>
    ///     Probe every <c>.ska*</c> file recursively under <paramref name="root" />.
    ///     Used by the manual "Add folder…" picker.
    /// </summary>
    public static IReadOnlyList<AnimationProbe> FindInDirectory(
        string root,
        int? skeletonBoneCount,
        CancellationToken ct,
        bool includePsxAnimationBanks = false,
        AssetSource? targetCharacterSource = null)
    {
        ct.ThrowIfCancellationRequested();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AnimationProbe>();
        if (includePsxAnimationBanks)
            AddPsxBanksFromDirectory(root, skeletonBoneCount, targetCharacterSource, seen, results, ct);
        else
            AddFromDirectory(root, skeletonBoneCount, seen, results, ct);
        return results;
    }

    /// <summary>
    ///     Probe every <c>.ska*</c> entry inside an archive backend. Used by the
    ///     manual "Add archive…" picker.
    /// </summary>
    public static IReadOnlyList<AnimationProbe> FindInArchive(
        ArchiveAssetBackend backend,
        int? skeletonBoneCount,
        CancellationToken ct,
        bool includePsxAnimationBanks = false,
        AssetSource? targetCharacterSource = null)
    {
        ct.ThrowIfCancellationRequested();

        var results = new List<AnimationProbe>();
        foreach (var entry in backend.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var source = new ArchiveAssetSource(backend, entry);
            if (includePsxAnimationBanks)
            {
                if (!IsPsxAnimationBankFileName(entry.Name)) continue;
                var remap = CreatePsxBoneRemap(source, targetCharacterSource, skeletonBoneCount);
                results.AddRange(PsxAnimationBank.CreateProbes(
                    source, skeletonBoneCount, boneRemap: remap));
            }
            else
            {
                if (!IsAnimFileName(entry.Name)) continue;
                var probe = TryProbe(source, source.DisplayName, skeletonBoneCount);
                if (probe != null) results.Add(probe);
            }
        }

        return results;
    }

    /// <summary>Reports whether a path looks like a SKA animation file.</summary>
    public static bool IsAnimFileName(string path)
    {
        return path.EndsWith(".ska", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ska.ps2", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ska.xbx", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ska.wpc", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ska.ngc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPsxAnimationBankFileName(string path)
    {
        return path.EndsWith(".psx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsN64Character(AssetSource source)
    {
        return source.EntryName.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPsxCharacter(AssetSource source)
    {
        return source.EntryName.EndsWith(".psx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Same-archive discovery: prefer entries under an anims directory scoped
    ///     to the character's stem, then any anims directory, then every .ska*.
    /// </summary>
    private static IReadOnlyList<AnimationProbe> FindForArchiveCharacter(
        ArchiveAssetSource archiveSource, int? skeletonBoneCount, CancellationToken ct)
    {
        var backend = archiveSource.Backend;
        var charStem = Path.GetFileNameWithoutExtension(archiveSource.EntryName);

        var animDirEntries = backend.Entries
            .Where(e => IsAnimFileName(e.Name) &&
                        e.Directory.Contains("anims", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var charScoped = animDirEntries
            .Where(e => e.Directory.Contains(charStem, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidates = charScoped.Count > 0 ? charScoped : animDirEntries;

        var results = new List<AnimationProbe>();
        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var source = new ArchiveAssetSource(backend, entry);
            var probe = TryProbe(source, source.DisplayName, skeletonBoneCount);
            if (probe != null) results.Add(probe);
        }

        // No anims directory in this archive at all — probe every .ska* entry
        // (the bone-count flag still separates mismatches).
        return results.Count > 0 ? results : FindInArchive(backend, skeletonBoneCount, ct);
    }

    private static List<AnimationProbe> FindForPsxCharacter(AssetSource skinSource, CancellationToken ct)
    {
        byte[] data;
        try
        {
            data = skinSource.ReadBytes();
        }
        catch
        {
            return [];
        }

        PsxMeshFile? psxFile;
        try
        {
            psxFile = PsxMeshFile.Parse(data);
        }
        catch
        {
            return [];
        }

        // HIER is not a universal character marker. Apocalypse and THPS1
        // prototype characters are flat supers with valid 0x2A/0x2C animation
        // chunks, while some large level files carry animation data for placed
        // objects. The parser's super classification distinguishes the two.
        if (psxFile is not { IsSuperModel: true }) return [];

        var boneCount = psxFile.Objects.Count;
        var results = new List<AnimationProbe>();
        results.AddRange(PsxAnimationBank.CreateProbes(skinSource, boneCount, i => $"anim_{i}"));

        // Most PS1 characters don't embed their own anim table — animations ship
        // in sibling bank .psx files (anims_*.psx, companion libraries). This was
        // previously reachable only through the manual Add-folder picker.
        AddSiblingPsxBanks(skinSource, boneCount, results, ct);
        return results;
    }

    private static void AddSiblingPsxBanks(
        AssetSource skinSource, int boneCount, List<AnimationProbe> results, CancellationToken ct)
    {
        var charStem = Path.GetFileNameWithoutExtension(skinSource.EntryName);

        if (skinSource.FileSystemPath is { } fsPath)
        {
            var fullPath = Path.GetFullPath(fsPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };
            var dir = Path.GetDirectoryName(fullPath);
            for (var depth = 0; depth < 2 && !string.IsNullOrEmpty(dir); depth++, dir = Path.GetDirectoryName(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.psx"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(Path.GetFullPath(path))) continue;
                    if (!IsPsxBankCandidate(Path.GetFileNameWithoutExtension(path), charStem)) continue;

                    var bankSource = new FileSystemAssetSource(path);
                    var remap = CreatePsxBoneRemap(bankSource, skinSource, boneCount);
                    results.AddRange(PsxAnimationBank.CreateProbes(bankSource, boneCount, boneRemap: remap));
                }
            }

            return;
        }

        if (skinSource is not ArchiveAssetSource archiveSource)
            return;

        foreach (var entry in archiveSource.Backend.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPsxAnimationBankFileName(entry.Name)) continue;
            if (entry.Name.Equals(archiveSource.EntryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsPsxBankCandidate(Path.GetFileNameWithoutExtension(entry.Name), charStem)) continue;

            var bankSource = new ArchiveAssetSource(archiveSource.Backend, entry);
            var remap = CreatePsxBoneRemap(bankSource, skinSource, boneCount);
            results.AddRange(PsxAnimationBank.CreateProbes(bankSource, boneCount, boneRemap: remap));
        }
    }

    /// <summary>
    ///     Sibling banks worth probing for a character: anything anim-named plus
    ///     the character's companion-library stems. Banks without an anim table
    ///     yield zero probes, so a broad-ish filter only costs parse time.
    /// </summary>
    private static bool IsPsxBankCandidate(string bankStem, string charStem)
    {
        if (bankStem.Contains("anim", StringComparison.OrdinalIgnoreCase))
            return true;

        return PsxTextureProviderFactory.GetCompanionLibraryStems(charStem)
            .Any(candidate => candidate.Equals(bankStem, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddFromDirectory(
        string root,
        int? skeletonBoneCount,
        HashSet<string> seen,
        List<AnimationProbe> results,
        CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAnimFileName(path)) continue;
            if (!seen.Add(Path.GetFullPath(path))) continue;

            var source = new FileSystemAssetSource(path);
            var probe = TryProbe(source, Path.GetFileName(path), skeletonBoneCount);
            if (probe != null) results.Add(probe);
        }
    }

    private static void AddPsxBanksFromDirectory(
        string root,
        int? skeletonBoneCount,
        AssetSource? targetCharacterSource,
        HashSet<string> seen,
        List<AnimationProbe> results,
        CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.psx", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!seen.Add(fullPath)) continue;

            var source = new FileSystemAssetSource(path);
            var remap = CreatePsxBoneRemap(source, targetCharacterSource, skeletonBoneCount);
            results.AddRange(PsxAnimationBank.CreateProbes(
                source, skeletonBoneCount, boneRemap: remap));
        }
    }

    private static PsxAnimationBoneRemap? CreatePsxBoneRemap(
        AssetSource source,
        AssetSource? targetCharacterSource,
        int? skeletonBoneCount)
    {
        if (targetCharacterSource == null || !skeletonBoneCount.HasValue)
            return null;

        return PsxAnimationBoneMap.TryCreate(
            source, targetCharacterSource, skeletonBoneCount.Value, out _);
    }

    private static AnimationProbe? TryProbe(
        AssetSource source, string displayName, int? skeletonBoneCount)
    {
        try
        {
            var data = source.ReadBytes();
            var probe = SkaFile.TryProbe(data);
            if (probe == null) return null;

            // THPS3 RpHAnim files do not store a bone count in their header, but
            // the full parser derives the track count without any external
            // compression table. Resolve it here so an unrelated level PRE clip
            // is not treated as compatible merely because its header count is
            // unknown (for example a 3-bone bird animation beside a 29-bone pro).
            var boneCount = probe.BoneCount;
            if (!boneCount.HasValue && source is ArchiveAssetSource)
                boneCount = SkaFile.Parse(data).BoneTracks.Length;

            // Bone-count match: only flag mismatched when both counts are known.
            var matches = !skeletonBoneCount.HasValue
                          || !boneCount.HasValue
                          || boneCount.Value == skeletonBoneCount.Value;

            return new AnimationProbe(
                source,
                displayName,
                probe.Duration,
                boneCount,
                matches);
        }
        catch
        {
            // Corrupt or unsupported file — skip silently so the user's list
            // doesn't get spammed by every failed probe.
            return null;
        }
    }
}
