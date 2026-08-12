using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     User-facing discovery for bounded N64 animation. Unlike PS1 discovery
///     it never searches external banks: only the selected shell's own direct
///     <c>0x2A</c> or compressed <c>0x2C</c> table can be surfaced.
/// </summary>
internal static class N64EmbeddedAnimationDiscovery
{
    public static IReadOnlyList<AnimationProbe> CreateProbes(AssetSource source)
    {
        try
        {
            var shellData = source.ReadBytes();
            var shell = PsxN64ShellFile.Parse(shellData);
            var renderData = N64ModelCompanions.TryReadRenderBank(source);
            if (shell == null || renderData == null)
                return [];

            var meshes = N64RenderBankFile.Parse(renderData);
            var renderBankId = N64ModelCompanions.TryReadRenderBankId(source);
            var plan = N64AnimatedModelGate.TryOpen(
                shellData, shell, renderData, renderBankId, meshes);
            if (plan == null)
                return [];

            return plan.Animations.Entries
                .Select((entry, index) =>
                {
                    var animationSource = new N64AnimationSource(
                        source, index, entry.FrameCount);
                    return new AnimationProbe(
                        animationSource,
                        animationSource.DisplayName,
                        // Preview policy only. The N64 runtime cadence has not
                        // yet been established from a playback oracle.
                        entry.FrameCount / PsxAnimationBank.DefaultPreviewFps,
                        shell.Objects.Count,
                        MatchesSkeleton: true,
                        entry.FrameCount);
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
