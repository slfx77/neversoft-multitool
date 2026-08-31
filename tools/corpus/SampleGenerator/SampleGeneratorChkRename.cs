using NeversoftMultitool.Core.QbKey;

namespace SampleGenerator;

internal static class SampleGeneratorChkRename
{
    /// <summary>
    ///     Renames Project 8 PS3's hash-named *.CHK files to their re-hash-proven
    ///     real names (<see cref="P8Ps3ChkNames" />) in place inside the research
    ///     tree, before mirroring — the PS3 port names every DATA file
    ///     QbKey(lowercased filename).CHK while keeping the real directory tree.
    ///     Idempotent: the first run renames every resolvable file, later runs find
    ///     no matching .CHK; unresolved stems (7 empty cutscene placeholder tables
    ///     and 2 PS3-only SPU/SELF modules) keep their shipped hash names. Runs for
    ///     any build whose research tree contains .CHK files, which in the current
    ///     corpus is only the P8 PS3 build.
    /// </summary>
    internal static int RenameChkFiles(string researchDir)
    {
        if (!Directory.Exists(researchDir))
            return 0;

        var renamed = 0;
        foreach (var file in Directory.EnumerateFiles(researchDir, "*.CHK", SearchOption.AllDirectories))
        {
            if (!P8Ps3ChkNames.TryResolveChkFileName(Path.GetFileName(file), out var resolved))
                continue;

            var directory = Path.GetDirectoryName(file)!;
            var target = SampleGeneratorPathSafety.ResolveDestinationPath(directory, resolved);
            if (File.Exists(target))
                continue;

            File.Move(file, target);
            renamed++;
        }

        return renamed;
    }
}
