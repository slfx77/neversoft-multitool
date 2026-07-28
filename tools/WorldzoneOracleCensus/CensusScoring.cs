using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace WorldzoneOracleCensus;

/// <summary>One suppressed leaf adjudicated against the oracle.</summary>
internal sealed record SuppressionScoreRow(
    LeafDecisionRecord Leaf,
    string Verdict,
    OracleGoldenSet.BlendStateEvidence Evidence)
{
    public const string WronglySuppressed = "WRONGLY_SUPPRESSED";
    public const string StateDrawnNoPixels = "STATE_DRAWN_NO_PIXELS";
    public const string StateUnobserved = "STATE_UNOBSERVED";
    public const string ChecksumUnobserved = "CHECKSUM_UNOBSERVED";
}

/// <summary>Leaves sharing one geometry key with more than one surviving pass.</summary>
internal sealed record OverlapCluster(
    int Id,
    string MdlName,
    string Space,
    Ps2DestinationAlphaLeafGeometryKey Key,
    List<LeafDecisionRecord> Members);

/// <summary>
///     Oracle draw-order evidence for one pair of surviving overlap passes.
///     First/Second are in converter draw order (ascending DrawIndex).
/// </summary>
internal sealed record ClusterPairEvidence(
    int ClusterId,
    LeafDecisionRecord First,
    LeafDecisionRecord Second,
    int CapturesBoth,
    int CapturesFirstBefore,
    int CapturesSecondBefore,
    int CapturesTied)
{
    public string OracleAgreement =>
        CapturesBoth == 0 ? "unobserved"
        : CapturesFirstBefore > CapturesSecondBefore ? "agrees"
        : CapturesSecondBefore > CapturesFirstBefore ? "DISAGREES"
        : "tied";
}

internal static class CensusScoring
{
    public static List<SuppressionScoreRow> ScoreSuppressions(
        IEnumerable<LeafDecisionRecord> records,
        OracleGoldenSet oracle)
    {
        var rows = new List<SuppressionScoreRow>();
        foreach (var leaf in records.Where(static r => r.Decision == LeafDecision.Suppressed))
        {
            var evidence = oracle.ScoreBlendState(
                leaf.TextureChecksum, leaf.AlphaA, leaf.AlphaB, leaf.AlphaC, leaf.AlphaD);
            var verdict = !oracle.IsObserved(leaf.TextureChecksum)
                ? SuppressionScoreRow.ChecksumUnobserved
                : evidence.StatePixels > 0
                    ? SuppressionScoreRow.WronglySuppressed
                    : evidence.StateDraws > 0
                        ? SuppressionScoreRow.StateDrawnNoPixels
                        : SuppressionScoreRow.StateUnobserved;
            rows.Add(new SuppressionScoreRow(leaf, verdict, evidence));
        }

        return rows;
    }

    public static List<OverlapCluster> BuildOverlapClusters(IEnumerable<LeafDecisionRecord> records)
    {
        var clusters = new List<OverlapCluster>();
        var id = 0;
        foreach (var group in records
                     .Where(static r => r.Decision == LeafDecision.Emitted)
                     .GroupBy(static r => (r.MdlName, r.Space, r.GeometryKey)))
        {
            var members = group.OrderBy(static r => r.DrawIndex).ToList();
            if (members.Count < 2)
                continue;

            clusters.Add(new OverlapCluster(id++, group.Key.MdlName, group.Key.Space, group.Key.GeometryKey,
                members));
        }

        return clusters;
    }

    public static List<ClusterPairEvidence> ScoreClusterPairs(
        IEnumerable<OverlapCluster> clusters,
        OracleGoldenSet oracle)
    {
        var pairs = new List<ClusterPairEvidence>();
        foreach (var cluster in clusters)
        {
            for (var i = 0; i < cluster.Members.Count; i++)
            {
                for (var j = i + 1; j < cluster.Members.Count; j++)
                {
                    var first = cluster.Members[i];
                    var second = cluster.Members[j];
                    if (first.TextureChecksum == 0 ||
                        second.TextureChecksum == 0 ||
                        first.TextureChecksum == second.TextureChecksum)
                    {
                        // The oracle keys facts by checksum; same-checksum pairs
                        // (or unresolved ones) carry no per-pass order signal.
                        pairs.Add(new ClusterPairEvidence(cluster.Id, first, second, 0, 0, 0, 0));
                        continue;
                    }

                    var both = 0;
                    var firstBefore = 0;
                    var secondBefore = 0;
                    var tied = 0;
                    foreach (var tag in oracle.CaptureTags)
                    {
                        var fa = oracle.FirstDrawIndex(tag, first.TextureChecksum,
                            first.AlphaA, first.AlphaB, first.AlphaC, first.AlphaD);
                        var fb = oracle.FirstDrawIndex(tag, second.TextureChecksum,
                            second.AlphaA, second.AlphaB, second.AlphaC, second.AlphaD);
                        if (fa is not { } a || fb is not { } b)
                            continue;

                        both++;
                        if (a < b)
                            firstBefore++;
                        else if (b < a)
                            secondBefore++;
                        else
                            tied++;
                    }

                    pairs.Add(new ClusterPairEvidence(cluster.Id, first, second, both, firstBefore,
                        secondBefore, tied));
                }
            }
        }

        return pairs;
    }
}
