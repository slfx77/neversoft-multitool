using NeversoftMultitool.Tests.Helpers;

[assembly: AssemblyFixture(typeof(TestPaths))]

namespace NeversoftMultitool.Tests.Helpers;

/// <summary>
///     Assembly-level fixture that locates test data and golden files once per test run.
///     Tests inject this via constructor and call Assert.SkipWhen() when data is absent.
/// </summary>
public class TestPaths
{
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
