using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     User-facing discovery for a DS model's clips. Like the N64 and GBA routes it
///     never searches external banks: a model's clips are container files the loader
///     names from the model's own two ids, so they are fetched by name and belong to
///     that model by construction.
///
///     A clip is offered only when it can actually be APPLIED. Channels bind to
///     joints positionally, so the clip's channel counts must match the geometry's
///     joint-flag census — <see cref="NdsPoseScatter.CanApply" /> — and a clip that
///     does not is left out of the pane rather than offered and then silently
///     dropped at export.
/// </summary>
internal static class NdsEmbeddedAnimationDiscovery
{
    public static IReadOnlyList<AnimationProbe> CreateProbes(AssetSource source)
    {
        try
        {
            var data = source.ReadBytes();
            if (!NdsGeometryFile.TryParseValidated(data, out var geometry))
                return [];

            var clips = NdsModelCompanions.ReadClips(source);
            if (clips.Count == 0)
                return [];

            // Bones are the matrices the display list actually uses, which is what
            // NdsAnimatedModelWriter emits — see its provenance rule.
            var boneCount = NdsGxInterpreter.RunInterpreter(data, geometry).UsedMatrices.Count;

            var probes = new List<AnimationProbe>(clips.Count);
            foreach (var (index, clip) in clips)
            {
                if (!NdsPoseScatter.CanApply(geometry, clip))
                    continue;

                var clipSource = new NdsAnimationSource(source, index, clip.Frames);
                probes.Add(new AnimationProbe(
                    clipSource,
                    clipSource.DisplayName,
                    clip.Frames / NdsAnimatedModelWriter.FramesPerSecond,
                    boneCount,
                    MatchesSkeleton: true,
                    clip.Frames));
            }

            return probes;
        }
        catch
        {
            return [];
        }
    }
}
