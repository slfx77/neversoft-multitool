using System.Collections.Concurrent;
using System.Collections.Frozen;
using NeversoftMultitool.Tests.Helpers;

[assembly: AssemblyFixture(typeof(TestPaths))]

namespace NeversoftMultitool.Tests.Helpers;

/// <summary>
///     Assembly-level fixture that locates test data and golden files once per test run.
///     Tests inject this via constructor and call Assert.SkipWhen() when data is absent.
/// </summary>
public class TestPaths
{
    private readonly ConcurrentDictionary<string, FrozenDictionary<string, string>> _buildIndex = new();

    public TestPaths()
    {
        TestDataDir = FindDirectory("tests", "TestData");
        GoldenFilesDir = FindDirectory("tests", "NeversoftMultitool.Tests", "GoldenFiles");
        SampleBuildsDir = FindDirectory("Sample", "Builds");
        TestOutputDir = FindDirectory("TestOutput");
    }

    public string? TestDataDir { get; }
    public string? GoldenFilesDir { get; }

    // PSX test files
    public string? PsxXboxDir => TestDataDir != null ? Path.Combine(TestDataDir, "Psx", "Xbox") : null;
    public string? PsxPs1Dir => TestDataDir != null ? Path.Combine(TestDataDir, "Psx", "Ps1") : null;

    // RLE/BMR test files
    public string? RleDir => TestDataDir != null ? Path.Combine(TestDataDir, "Rle") : null;

    // Archive test files
    public string? WadDir => TestDataDir != null ? Path.Combine(TestDataDir, "Archives", "Wad") : null;
    public string? PkrDir => TestDataDir != null ? Path.Combine(TestDataDir, "Archives", "Pkr") : null;

    // THPS3 SKA animation fixtures (extracted from disc, gitignored)
    public string? Thps3SkaDir => TestDataDir != null ? Path.Combine(TestDataDir, "Thps3", "Ska") : null;

    // Golden files
    public string? GoldenPsxXboxDir => GoldenFilesDir != null ? Path.Combine(GoldenFilesDir, "Psx", "Xbox") : null;
    public string? GoldenPsxPs1Dir => GoldenFilesDir != null ? Path.Combine(GoldenFilesDir, "Psx", "Ps1") : null;
    public string? GoldenRleDir => GoldenFilesDir != null ? Path.Combine(GoldenFilesDir, "Rle") : null;

    public string? GoldenWadManifest =>
        GoldenFilesDir != null ? Path.Combine(GoldenFilesDir, "Archives", "Wad", "manifest.json") : null;

    public string? GoldenPkrManifest =>
        GoldenFilesDir != null ? Path.Combine(GoldenFilesDir, "Archives", "Pkr", "manifest.json") : null;

    // Sample builds (for formats without dedicated TestData, e.g. TRG)
    public string? SampleBuildsDir { get; }
    public bool HasSampleBuilds => SampleBuildsDir != null && Directory.Exists(SampleBuildsDir);

    // Test output directory for diagnostic reports
    public string? TestOutputDir { get; }

    public bool HasTestData => TestDataDir != null && Directory.Exists(TestDataDir);
    public bool HasGoldenFiles => GoldenFilesDir != null && Directory.Exists(GoldenFilesDir);

    /// <summary>
    /// Locates a file by its bare filename inside Sample/Builds/{buildName}/. The first call for a
    /// given build builds an in-memory index by walking the build tree; subsequent calls hit the
    /// cache. Lookup is case-insensitive. If multiple files share the same name, the indexer keeps
    /// the last one encountered — callers needing disambiguation should use <see cref="FindSampleFiles"/>.
    /// </summary>
    public string? FindSampleFile(string buildName, string fileName)
    {
        var index = GetBuildIndex(buildName);
        return index.TryGetValue(fileName, out var path) ? path : null;
    }

    /// <summary>
    /// Enumerates files matching a glob pattern under Sample/Builds/{buildName}/, recursively.
    /// Returns an empty sequence when the build directory does not exist.
    /// </summary>
    public IEnumerable<string> FindSampleFiles(string buildName, string searchPattern)
    {
        if (SampleBuildsDir is null) return [];
        var buildDir = Path.Combine(SampleBuildsDir, buildName);
        return Directory.Exists(buildDir)
            ? Directory.EnumerateFiles(buildDir, searchPattern, SearchOption.AllDirectories)
            : [];
    }

    private FrozenDictionary<string, string> GetBuildIndex(string buildName) =>
        _buildIndex.GetOrAdd(buildName, BuildIndex);

    private FrozenDictionary<string, string> BuildIndex(string buildName)
    {
        if (SampleBuildsDir is null) return FrozenDictionary<string, string>.Empty;
        var buildDir = Path.Combine(SampleBuildsDir, buildName);
        if (!Directory.Exists(buildDir)) return FrozenDictionary<string, string>.Empty;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories))
            dict[Path.GetFileName(file)] = file;

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindDirectory(params string[] relativeParts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine([dir, .. relativeParts]);
            if (Directory.Exists(candidate))
                return candidate;

            dir = Path.GetDirectoryName(dir)!;
        }

        return null;
    }
}
