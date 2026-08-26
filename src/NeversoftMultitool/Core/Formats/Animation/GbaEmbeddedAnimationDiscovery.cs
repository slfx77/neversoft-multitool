using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     User-facing discovery for the THPS2 GBA skater's clips. Like the N64 route
///     it never searches external banks: the clips live in the companion ROM the
///     carved character record already resolves against, and every character
///     shares the one skater mesh, so every clip always matches the rig.
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
            var model = GbaSkaterModel.TryLocate(rom);
            if (model == null)
                return [];

            var boneCount = model.VertCounts.Sum(count => count);
            var probes = new List<AnimationProbe>(model.ClipCount);
            foreach (var clip in GbaSkaterModel.ReadClips(rom, model))
            {
                if (clip.TickCount == 0)
                    continue;

                var clipSource = new GbaAnimationSource(source, clip.Index, clip.TickCount);
                probes.Add(new AnimationProbe(
                    clipSource,
                    clipSource.DisplayName,
                    clip.TickCount / GbaAnimatedModelWriter.TicksPerSecond,
                    boneCount,
                    MatchesSkeleton: true,
                    // DISTINCT frames, not ticks: a clip that holds one pose for
                    // 30 ticks is a single-frame pose, and the pane's
                    // "Hide single-frame poses" filter should treat it as one.
                    GbaSkaterModel.ClipFrames(rom, model, clip).Distinct().Count()));
            }

            return probes;
        }
        catch
        {
            return [];
        }
    }
}
