using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Joins THPS2X's authored TRG background registrations to the Xbox DDM
///     object bank. The Xbox port ships the original PSX placement/TRG files
///     beside its replacement DDM geometry, and every background checksum in
///     the current corpus resolves exactly to a DDM object. No filename or
///     geometric sky heuristic is used.
/// </summary>
internal static class DdmSkyClassifier
{
    internal sealed record Result(
        IReadOnlySet<int> ObjectIndices,
        IReadOnlyDictionary<int, int> LayerOrder,
        Matrix4x4 AnchorTransform,
        uint? SkyColor);

    /// <summary>
    ///     Resolves the complete registration set or returns null. One missing
    ///     or ambiguous checksum declines the whole sky: emitting only part of
    ///     a layered background would be less truthful than leaving the bank's
    ///     geometry in its ordinary authored placement.
    /// </summary>
    internal static Result? Classify(DdmFile? objectBank, TrgFile? trg)
    {
        if (objectBank == null || trg == null)
            return null;

        var checksumRanks = PsxSkyDomeClassifier.CollectBackgroundRegistrations(
            trg, out var anchor);
        if (checksumRanks.Count == 0)
            return null;

        var byHash = DdmHashLookup.Build(objectBank);
        var objectIndices = new HashSet<int>();
        var layerOrder = new Dictionary<int, int>();
        foreach (var (checksum, rank) in checksumRanks)
        {
            if (!byHash.TryGetValue(checksum, out var candidates))
                return null;

            var distinct = candidates.Distinct().ToArray();
            if (distinct.Length != 1)
                return null;

            var objectIndex = distinct[0];
            objectIndices.Add(objectIndex);
            if (!layerOrder.TryGetValue(objectIndex, out var existing) || rank < existing)
                layerOrder[objectIndex] = rank;
        }

        // TRG coordinates are serialized in whole engine units (the runtime
        // shifts them left by 12 when constructing its 20.12 vector), unlike
        // *_o.psx layout coordinates, which are already 20.12. THPS2X DDM
        // geometry uses the Xbox conversion (-X, -Y, +Z); applying the PS1
        // helper here would both divide the anchor by 4096 and flip the wrong
        // pair of axes. skhvn corroborates the units directly: its first TRG
        // anchor (-1087,-1,2490) nearly coincides with the authored sky-bank
        // placement (-1069,1191,2458), not (-0.27,0,0.61).
        var anchorTransform = anchor != null
            ? Matrix4x4.CreateTranslation(
                new Vector3(-anchor.RawX, -anchor.RawY, anchor.RawZ))
            : Matrix4x4.Identity;
        return new Result(
            objectIndices,
            layerOrder,
            anchorTransform,
            PsxSkyDomeClassifier.FindSkyColor(trg));
    }
}
