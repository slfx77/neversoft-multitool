using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Locates SKA animation files associated with a given character (skinned
///     mesh). Used by the Character Preview tab to populate its animation list
///     after the user selects a character.
/// </summary>
internal static class AnimationDiscovery
{
    private const float PsxFps = 30f;

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
        string root, int? skeletonBoneCount, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AnimationProbe>();
        AddFromDirectory(root, skeletonBoneCount, seen, results, ct);
        return results;
    }

    /// <summary>
    ///     Probe every <c>.ska*</c> entry inside an archive backend. Used by the
    ///     manual "Add archive…" picker.
    /// </summary>
    public static IReadOnlyList<AnimationProbe> FindInArchive(
        ArchiveAssetBackend backend, int? skeletonBoneCount, CancellationToken ct)
    {
        var results = new List<AnimationProbe>();
        foreach (var entry in backend.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAnimFileName(entry.Name)) continue;

            var source = new ArchiveAssetSource(backend, entry);
            var probe = TryProbe(source, source.DisplayName, skeletonBoneCount);
            if (probe != null) results.Add(probe);
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

    private static bool IsPsxCharacter(AssetSource source)
    {
        return source.EntryName.EndsWith(".psx", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AnimationProbe> FindForPsxCharacter(AssetSource skinSource)
    {
        var fsPath = skinSource.FileSystemPath;
        // PsxAnimationSource needs a real disk path — archive-backed PSX would need
        // the bytes cached, which we defer until the format is more widely needed.
        if (fsPath == null) return [];

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

        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        if (meshBlockEnd <= 0) return [];

        PsxAnimFile? animFile;
        try
        {
            animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        }
        catch
        {
            return [];
        }

        if (animFile == null || animFile.Entries.Count == 0) return [];

        var results = new List<AnimationProbe>(animFile.Entries.Count);
        for (var i = 0; i < animFile.Entries.Count; i++)
        {
            var entry = animFile.Entries[i];
            var source = new PsxAnimationSource(fsPath, i, entry.FrameCount);
            var duration = entry.FrameCount / PsxFps;
            results.Add(new AnimationProbe(
                source,
                $"anim_{i}",
                duration,
                psxFile.Objects.Count,
                true));
        }

        return results;
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
