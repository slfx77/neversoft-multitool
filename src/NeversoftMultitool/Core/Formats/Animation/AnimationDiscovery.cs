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
        ["anims", "Bits/anims", "bits/anims", "pre/anims", "Pre/anims"];

    /// <summary>
    ///     Walk the character's parent directories looking for known animation
    ///     subdirs and probe every <c>.ska*</c> file under them. PS1 character
    ///     <c>.psx</c> files are special-cased: their animations are embedded
    ///     in the same file, not stored as separate <c>.ska</c> files.
    /// </summary>
    public static IReadOnlyList<AnimationProbe> FindForCharacter(
        AssetSource skinSource, int? skeletonBoneCount, CancellationToken ct)
    {
        if (IsPsxCharacter(skinSource))
            return FindForPsxCharacter(skinSource);

        var fsPath = skinSource.FileSystemPath;
        if (fsPath == null)
        {
            // Archive-backed characters: fall back to scanning the archive itself.
            return [];
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
               || path.EndsWith(".ska.wpc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPsxAnimationBankFileName(string path)
    {
        return path.EndsWith(".psx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPsxCharacter(AssetSource source)
    {
        return source.EntryName.EndsWith(".psx", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AnimationProbe> FindForPsxCharacter(AssetSource skinSource)
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

        if (psxFile == null) return [];

        return PsxAnimationBank.CreateProbes(
            skinSource,
            psxFile.Objects.Count,
            i => $"anim_{i}");
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

            // Bone-count match: only flag mismatched when both counts are known.
            // THPS3 anims have null BoneCount → we cannot disqualify them, so they
            // render as "matches" and the user can still preview / convert.
            var matches = !skeletonBoneCount.HasValue
                          || !probe.BoneCount.HasValue
                          || probe.BoneCount.Value == skeletonBoneCount.Value;

            return new AnimationProbe(
                source,
                displayName,
                probe.Duration,
                probe.BoneCount,
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
