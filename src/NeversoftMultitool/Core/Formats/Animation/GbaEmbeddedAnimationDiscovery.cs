using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     User-facing discovery for a GBA rider's clips (the THPS2 skater or the
///     THPS3 rider — <see cref="GbaRiderClips" /> decides which). Like the N64
///     route it never searches external banks: the clips live in the companion
///     ROM the carved character record already resolves against, and a morph
///     model has no skeleton to mismatch, so every clip always matches the rig.
/// </summary>
internal static class GbaEmbeddedAnimationDiscovery
{
    public static IReadOnlyList<AnimationProbe> CreateProbes(AssetSource source)
    {
        try
        {
            var rom = source.TryReadCompanion(GbaLevelCarver.RomEntryName);
            if (rom == null)
                return [];
            var clips = GbaRiderClips.TryList(rom);
            if (clips == null)
                return [];

            var boneCount = GbaRiderClips.TryGetVertexCount(rom);
            var probes = new List<AnimationProbe>(clips.Count);
            foreach (var clip in clips)
            {
                if (clip.TickCount == 0)
                    continue;

                // THPS2 ships the same tricks.bin the PS1 discs do; clips a single
                // trick uniquely owns are listed by their real name.
                var clipSource = new GbaAnimationSource(source, clip.Index, clip.TickCount, clip.TrickName);
                probes.Add(new AnimationProbe(
                    clipSource,
                    clipSource.DisplayName,
                    clip.TickCount / GbaAnimatedModelWriter.TicksPerSecond,
                    boneCount,
                    MatchesSkeleton: true,
                    // DISTINCT frames, not ticks: a clip that holds one pose for
                    // 30 ticks is a single-frame pose, and the pane's
                    // "Hide single-frame poses" filter should treat it as one.
                    clip.DistinctFrames));
            }

            return probes;
        }
        catch
        {
            return [];
        }
    }
}
