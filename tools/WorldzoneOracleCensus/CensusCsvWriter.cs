using System.Globalization;
using System.Text;

namespace WorldzoneOracleCensus;

/// <summary>
///     CSV emission for the census artifacts. All numeric fields are ints, hex,
///     or bools except max dimension, which is formatted invariant explicitly.
/// </summary>
internal static class CensusCsvWriter
{
    public static void WriteLeaves(string path, IEnumerable<LeafDecisionRecord> records, OracleGoldenSet oracle)
    {
        using var csv = Open(path);
        csv.WriteLine("mdl,space,leafIndex,drawIndex,orderKey,decision,alphaMode,alphaBlend,alphaAbcd," +
                      "texChecksum,groupChecksum,vertexCount,triangleCount,maxDimension," +
                      "oracleObserved,oracleDraws,oraclePixels");
        foreach (var r in records)
        {
            var (draws, pixels) = oracle.Totals(r.TextureChecksum);
            csv.WriteLine(
                $"{r.MdlName},{r.Space},{r.LeafIndex},{r.DrawIndex},0x{r.RenderOrderKey:X4},{r.Decision}," +
                $"{r.AlphaMode},0x{r.AlphaBlend:X2},{r.AlphaA}{r.AlphaB}{r.AlphaC}{r.AlphaD}," +
                $"0x{r.TextureChecksum:X8},0x{r.GroupChecksum:X8},{r.VertexCount},{r.TriangleCount}," +
                $"{F(r.MaxDimension)},{oracle.IsObserved(r.TextureChecksum)},{draws},{pixels}");
        }
    }

    public static void WriteSuppressed(string path, IEnumerable<SuppressionScoreRow> rows)
    {
        using var csv = Open(path);
        csv.WriteLine("mdl,space,leafIndex,drawIndex,texChecksum,alphaBlend,prevTexChecksum,prevAlphaBlend," +
                      "maxDimension,verdict,blendStateDraws,blendStatePixels,blendStateCaptures," +
                      "anyStateDraws,anyStatePixels");
        foreach (var row in rows)
        {
            var r = row.Leaf;
            csv.WriteLine(
                $"{r.MdlName},{r.Space},{r.LeafIndex},{r.DrawIndex},0x{r.TextureChecksum:X8}," +
                $"0x{r.AlphaBlend:X2},0x{r.PreviousMaskChecksum:X8},0x{r.PreviousMaskAlphaBlend:X2}," +
                $"{F(r.MaxDimension)},{row.Verdict},{row.Evidence.StateDraws},{row.Evidence.StatePixels}," +
                $"{string.Join(';', row.Evidence.StateCaptures)},{row.Evidence.AnyDraws},{row.Evidence.AnyPixels}");
        }
    }

    public static void WriteClusters(string path, IEnumerable<OverlapCluster> clusters, OracleGoldenSet oracle)
    {
        using var csv = Open(path);
        csv.WriteLine("clusterId,mdl,space,memberCount,leafIndex,drawIndex,orderKey,alphaMode,alphaBlend," +
                      "texChecksum,oracleObserved,vertexCount,maxDimension");
        foreach (var cluster in clusters)
        {
            foreach (var r in cluster.Members)
            {
                csv.WriteLine(
                    $"{cluster.Id},{cluster.MdlName},{cluster.Space},{cluster.Members.Count},{r.LeafIndex}," +
                    $"{r.DrawIndex},0x{r.RenderOrderKey:X4},{r.AlphaMode},0x{r.AlphaBlend:X2}," +
                    $"0x{r.TextureChecksum:X8},{oracle.IsObserved(r.TextureChecksum)},{r.VertexCount}," +
                    $"{F(r.MaxDimension)}");
            }
        }
    }

    public static void WriteClusterPairs(string path, IEnumerable<ClusterPairEvidence> pairs)
    {
        using var csv = Open(path);
        csv.WriteLine("clusterId,mdl,space,leafA,leafB,texA,texB,alphaModeA,alphaModeB,alphaBlendA,alphaBlendB," +
                      "capturesBoth,capturesABeforeB,capturesBBeforeA,capturesTied,oracleVsConverter");
        foreach (var pair in pairs)
        {
            var a = pair.First;
            var b = pair.Second;
            csv.WriteLine(
                $"{pair.ClusterId},{a.MdlName},{a.Space},{a.LeafIndex},{b.LeafIndex}," +
                $"0x{a.TextureChecksum:X8},0x{b.TextureChecksum:X8},{a.AlphaMode},{b.AlphaMode}," +
                $"0x{a.AlphaBlend:X2},0x{b.AlphaBlend:X2},{pair.CapturesBoth},{pair.CapturesFirstBefore}," +
                $"{pair.CapturesSecondBefore},{pair.CapturesTied},{pair.OracleAgreement}");
        }
    }

    public static void WriteNearMisses(
        string path,
        IEnumerable<(LeafDecisionRecord Leaf, BlendNearMissReason Reason)> nearMisses,
        OracleGoldenSet oracle)
    {
        using var csv = Open(path);
        csv.WriteLine("mdl,space,leafIndex,drawIndex,texChecksum,alphaBlend,reason,maxDimension," +
                      "oracleObserved,blendStateDraws,blendStatePixels");
        foreach (var (r, reason) in nearMisses)
        {
            var evidence = oracle.ScoreBlendState(r.TextureChecksum, r.AlphaA, r.AlphaB, r.AlphaC, r.AlphaD);
            csv.WriteLine(
                $"{r.MdlName},{r.Space},{r.LeafIndex},{r.DrawIndex},0x{r.TextureChecksum:X8}," +
                $"0x{r.AlphaBlend:X2},{reason},{F(r.MaxDimension)},{oracle.IsObserved(r.TextureChecksum)}," +
                $"{evidence.StateDraws},{evidence.StatePixels}");
        }
    }

    private static StreamWriter Open(string path)
    {
        return new StreamWriter(path, false, new UTF8Encoding(false));
    }

    private static string F(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
