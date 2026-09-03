using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The one place that knows which GBA cart's rider a companion ROM carries
///     and lists its animation clips: the THPS2 skater (<see cref="GbaSkaterModel" />,
///     clips named by the cart's tricks.bin where a trick uniquely owns one) or the
///     THPS3 rider (<see cref="GbaThps3RiderModel" />, anonymous clips). The CLI's
///     <c>--gba-animation(s)</c> selection, the Animations pane's discovery and
///     the rig-size probe all consult this rather than one cart's parser, so a
///     carved <c>.chr.gba</c> of either cart animates through the same route.
/// </summary>
public static class GbaRiderClips
{
    /// <summary>One clip: its table index, tick count (0 = authored-empty),
    ///     distinct pose frames, and the proven trick name if one owns it.</summary>
    public readonly record struct Entry(int Index, int TickCount, int DistinctFrames, string? TrickName);

    /// <summary>Every clip of the ROM's rider in table order, or null when the ROM
    ///     carries no rider this tool decodes.</summary>
    public static IReadOnlyList<Entry>? TryList(ReadOnlySpan<byte> rom)
    {
        var thps2 = GbaSkaterModel.TryLocate(rom);
        if (thps2 != null)
        {
            var names = GbaTricksFile.TryBuildClipNames(rom, thps2.ClipCount);
            var entries = new List<Entry>(thps2.ClipCount);
            foreach (var clip in GbaSkaterModel.ReadClips(rom, thps2))
            {
                var distinct = clip.TickCount == 0
                    ? 0
                    : GbaSkaterModel.ClipFrames(rom, thps2, clip).Distinct().Count();
                entries.Add(new Entry(
                    clip.Index, clip.TickCount, distinct,
                    names != null && names.TryGetValue(clip.Index, out var trick) ? trick : null));
            }

            return entries;
        }

        var thps3 = GbaThps3RiderModel.TryLocate(rom);
        if (thps3 != null)
        {
            var entries = new List<Entry>(thps3.ClipCount);
            foreach (var clip in GbaThps3RiderModel.ReadClips(rom, thps3))
            {
                var distinct = clip.TickCount == 0
                    ? 0
                    : GbaThps3RiderModel.ClipFrames(rom, thps3, clip).Distinct().Count();
                entries.Add(new Entry(clip.Index, clip.TickCount, distinct, TrickName: null));
            }

            return entries;
        }

        return null;
    }

    /// <summary>
    ///     The rider's morph-mesh vertex count — what the Animations pane reports
    ///     as the rig size, since a morph model has no skeleton and every clip
    ///     always fits the one mesh.
    /// </summary>
    public static int? TryGetVertexCount(ReadOnlySpan<byte> rom)
    {
        var thps2 = GbaSkaterModel.TryLocate(rom);
        if (thps2 != null)
            return thps2.VertCounts.Sum(static count => count);
        var thps3 = GbaThps3RiderModel.TryLocate(rom);
        return thps3?.Rider.VertexCount;
    }

    /// <summary>The clip's export name: the trick where one owns it, else the synthetic label.</summary>
    public static string ExportName(Entry entry) =>
        entry.TrickName != null
            ? AnimationExportName.ForMesh(meshStem: string.Empty, entry.TrickName)
            : $"anim_{entry.Index}";

    /// <summary>The export name of one clip index, or the synthetic label when the
    ///     ROM carries no decodable rider.</summary>
    public static string ExportName(ReadOnlySpan<byte> rom, int clipIndex)
    {
        var entries = TryList(rom);
        return entries != null && clipIndex >= 0 && clipIndex < entries.Count
            ? ExportName(entries[clipIndex])
            : $"anim_{clipIndex}";
    }
}
