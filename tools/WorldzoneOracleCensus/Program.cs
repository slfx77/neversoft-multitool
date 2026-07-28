// WorldzoneOracleCensus — Phase-3 B0 of the THAW-fidelity stream.
//
// Scores the converter's CURRENT worldzone multi-pass decisions (the
// ShouldSkipRedundantWorldzoneBlendLayer suppression filter and the physical
// draw-order vertex stagger) against the committed GS-oracle goldens:
//   a. SUPPRESSION SCORE  — would-be-suppressed blend layers whose blend state
//      observably drew pixels in-game (evidence the pass IS visible).
//   b. OVERLAP CLUSTERS   — same-geometry leaves with >1 surviving pass, with
//      the oracle's FirstDrawIndex order between them (evidence for the B1
//      pass-index ladder that should replace the physical stagger).
//   c. COVERAGE           — how much of the zone the captures actually saw.
//
// Usage: WorldzoneOracleCensus [pakPath] [--goldens dir] [-o outputDir]
//   pakPath default: C:/tmp/z_bh.pak.ps2
//   goldens default: tests/NeversoftMultitool.Tests/GoldenFiles/GsOracle
//   output default:  TestOutput/WorldzoneOracleCensus

using System.Globalization;
using WorldzoneOracleCensus;

var pakPath = @"C:\tmp\z_bh.pak.ps2";
string? goldenDir = null;
string? outputDir = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "-o" or "--output" && i + 1 < args.Length)
        outputDir = args[++i];
    else if (args[i] is "--goldens" && i + 1 < args.Length)
        goldenDir = args[++i];
    else if (!args[i].StartsWith('-'))
        pakPath = args[i];
}

var repoRoot = FindRepoRoot();
goldenDir ??= repoRoot != null
    ? Path.Combine(repoRoot, "tests", "NeversoftMultitool.Tests", "GoldenFiles", "GsOracle")
    : null;
outputDir ??= Path.Combine(repoRoot ?? ".", "TestOutput", "WorldzoneOracleCensus");

if (!File.Exists(pakPath))
{
    Console.Error.WriteLine($"Worldzone pak not found: {pakPath}");
    return 1;
}

if (goldenDir == null || !Directory.Exists(goldenDir))
{
    Console.Error.WriteLine($"GS-oracle golden directory not found: {goldenDir ?? "(unresolved)"}");
    return 1;
}

var oracle = OracleGoldenSet.Load(goldenDir);
if (oracle.CaptureTags.Count == 0)
{
    Console.Error.WriteLine($"No *.gsoracle.json goldens in {goldenDir}");
    return 1;
}

var census = WorldzoneCensusSimulator.Run(pakPath);
var records = census.Records;
var suppressionRows = CensusScoring.ScoreSuppressions(records, oracle);
var clusters = CensusScoring.BuildOverlapClusters(records);
var clusterPairs = CensusScoring.ScoreClusterPairs(clusters, oracle);

Directory.CreateDirectory(outputDir);
CensusCsvWriter.WriteLeaves(Path.Combine(outputDir, "leaves.csv"), records, oracle);
CensusCsvWriter.WriteSuppressed(Path.Combine(outputDir, "suppressed.csv"), suppressionRows);
CensusCsvWriter.WriteClusters(Path.Combine(outputDir, "clusters.csv"), clusters, oracle);
CensusCsvWriter.WriteClusterPairs(Path.Combine(outputDir, "cluster_pairs.csv"), clusterPairs);
CensusCsvWriter.WriteNearMisses(Path.Combine(outputDir, "near_misses.csv"), census.BlendNearMisses, oracle);

PrintSummary();
Console.WriteLine($"\nCSV artifacts: {Path.GetFullPath(outputDir)}");
return 0;

void PrintSummary()
{
    Console.WriteLine($"=== Worldzone oracle census: {Path.GetFileName(pakPath)} ===");
    Console.WriteLine(
        $"Pak: {pakPath} ({census.MdlParsedCount}/{census.MdlEntryCount} MDL entries parsed, " +
        $"zone texture catalog: {(census.CatalogBuilt ? "yes" : "NO — checksums unresolved")})");
    Console.WriteLine(
        $"Goldens: {goldenDir} ({oracle.CaptureTags.Count} captures, " +
        $"{oracle.ObservedChecksumCount} distinct observed checksums)");

    var byDecision = records
        .GroupBy(static r => r.Decision)
        .ToDictionary(static g => g.Key, static g => g.Count());
    int Count(LeafDecision d) => byDecision.GetValueOrDefault(d);
    Console.WriteLine($"\nLeaf visits: {records.Count} " +
                      $"(world {records.Count(static r => r.Space == "world")}, " +
                      $"local {records.Count(static r => r.Space == "local")})");
    Console.WriteLine($"  emitted {Count(LeafDecision.Emitted)}, " +
                      $"empty-strip {Count(LeafDecision.EmptyStrip)}, " +
                      $"suppressed {Count(LeafDecision.Suppressed)}, " +
                      $"filtered verts<3 {Count(LeafDecision.FilteredVertexCount)}, " +
                      $"filtered junk-gate {Count(LeafDecision.FilteredJunkGate)}, " +
                      $"not-visited local {Count(LeafDecision.NotVisited)}");

    Console.WriteLine("\n--- a. SUPPRESSION SCORE (ShouldSkipRedundantWorldzoneBlendLayer) ---");
    var byVerdict = suppressionRows
        .GroupBy(static r => r.Verdict)
        .ToDictionary(static g => g.Key, static g => g.Count());
    Console.WriteLine($"suppressed blend layers: {suppressionRows.Count}");
    Console.WriteLine(
        $"  WRONGLY SUPPRESSED (blend state drew pixels in-game): " +
        $"{byVerdict.GetValueOrDefault(SuppressionScoreRow.WronglySuppressed)}");
    Console.WriteLine(
        $"  state drawn but zero pixels: {byVerdict.GetValueOrDefault(SuppressionScoreRow.StateDrawnNoPixels)}");
    Console.WriteLine(
        $"  checksum observed, blend state never: " +
        $"{byVerdict.GetValueOrDefault(SuppressionScoreRow.StateUnobserved)}");
    Console.WriteLine(
        $"  checksum unobserved in any capture: " +
        $"{byVerdict.GetValueOrDefault(SuppressionScoreRow.ChecksumUnobserved)}");
    foreach (var row in suppressionRows
                 .Where(static r => r.Verdict == SuppressionScoreRow.WronglySuppressed)
                 .OrderByDescending(static r => r.Evidence.StatePixels)
                 .Take(10))
    {
        Console.WriteLine(
            $"    tex 0x{row.Leaf.TextureChecksum:X8} mdl {row.Leaf.MdlName} leaf {row.Leaf.LeafIndex}: " +
            $"{row.Evidence.StateDraws} blend-state draws, {row.Evidence.StatePixels} px " +
            $"({row.Evidence.StateCaptures.Count} captures)");
    }

    Console.WriteLine("\n  predicate-eligible blend leaves NOT suppressed (near-miss triage):");
    foreach (var group in census.BlendNearMisses
                 .GroupBy(static m => m.Reason)
                 .OrderByDescending(static g => g.Count()))
    {
        Console.WriteLine($"    {group.Key}: {group.Count()}");
    }

    Console.WriteLine("\n--- b. OVERLAP CLUSTERS (same geometryKey, >1 surviving pass) ---");
    Console.WriteLine($"clusters: {clusters.Count} " +
                      $"({clusters.Sum(static c => c.Members.Count)} member passes; " +
                      $"largest {clusters.Select(static c => c.Members.Count).DefaultIfEmpty(0).Max()})");
    var observedPairs = clusterPairs.Where(static p => p.CapturesBoth > 0).ToList();
    Console.WriteLine($"pairs with oracle order evidence: {observedPairs.Count}/{clusterPairs.Count}");
    Console.WriteLine($"  converter order agrees: {observedPairs.Count(static p => p.OracleAgreement == "agrees")}, " +
                      $"DISAGREES: {observedPairs.Count(static p => p.OracleAgreement == "DISAGREES")}, " +
                      $"tied: {observedPairs.Count(static p => p.OracleAgreement == "tied")}");
    foreach (var pair in observedPairs
                 .OrderByDescending(static p => p.CapturesBoth)
                 .Take(10))
    {
        Console.WriteLine(
            $"    cluster {pair.ClusterId} ({pair.First.MdlName}/{pair.First.Space}): " +
            $"leaf {pair.First.LeafIndex} 0x{pair.First.TextureChecksum:X8} {pair.First.AlphaMode} -> " +
            $"leaf {pair.Second.LeafIndex} 0x{pair.Second.TextureChecksum:X8} {pair.Second.AlphaMode}; " +
            $"oracle first-draw order {pair.CapturesFirstBefore}:{pair.CapturesSecondBefore} " +
            $"(ties {pair.CapturesTied}) over {pair.CapturesBoth} captures -> {pair.OracleAgreement}");
    }

    Console.WriteLine("\n--- c. COVERAGE (the dumps only show drawn textures) ---");
    var visited = records.Where(static r => r.Decision != LeafDecision.NotVisited).ToList();
    var distinct = visited
        .Where(static r => r.TextureChecksum != 0)
        .Select(static r => r.TextureChecksum)
        .Distinct()
        .ToList();
    var observedDistinct = distinct.Count(oracle.IsObserved);
    var observedLeaves = visited.Count(r => oracle.IsObserved(r.TextureChecksum));
    Console.WriteLine(
        $"distinct leaf texture checksums: {distinct.Count}; observed in goldens: {observedDistinct} " +
        $"({Percent(observedDistinct, distinct.Count)})");
    Console.WriteLine(
        $"visited leaves with any oracle observation: {observedLeaves}/{visited.Count} " +
        $"({Percent(observedLeaves, visited.Count)})");
}

static string Percent(int numerator, int denominator)
{
    return denominator == 0
        ? "n/a"
        : (100.0 * numerator / denominator).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

static string? FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir, "tests", "NeversoftMultitool.Tests", "GoldenFiles", "GsOracle")))
            return dir;
        dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
    }

    return null;
}
