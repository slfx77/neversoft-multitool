using System.Collections.Concurrent;
using System.Diagnostics;
using static SampleGenerator.SampleGeneratorBuildOperations;
using static SampleGenerator.SampleGeneratorConfig;

namespace SampleGenerator;

static class Program
{
    static int Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = ParseArguments(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        if (options.SelfTest)
            return SampleGeneratorSelfTests.Run();

        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            Configure(
                options.MediaRoot,
                options.ResearchRoot,
                options.SampleRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        var buildFilter = options.BuildFilter;
        var repopulate = options.Repopulate;

        var selectedBuilds = Builds
            .Where(build => string.IsNullOrWhiteSpace(buildFilter) ||
                build.Name.Contains(buildFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (selectedBuilds.Length == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(buildFilter)
                ? "No builds configured."
                : $"No builds matched --build \"{buildFilter}\".");
            return 1;
        }

        Console.WriteLine("Generating Sample/Builds (mirrored disc tree, archives unpacked in-place)...");
        Console.WriteLine($"Media root: {ResearchMedia}");
        Console.WriteLine($"Research cache: {ResearchBuilds}");
        Console.WriteLine($"Sample output: {SampleBuilds}");
        if (!string.IsNullOrWhiteSpace(buildFilter))
            Console.WriteLine($"Build filter: {buildFilter}");
        if (repopulate)
            Console.WriteLine("Repopulate: research trees re-extract from Media disc images");
        Console.WriteLine();

        var stopwatch = Stopwatch.StartNew();
        var results = new ConcurrentBag<BuildResult>();
        var consoleLock = new object();

        Parallel.ForEach(selectedBuilds, build =>
        {
            BuildResult result;
            try
            {
                result = RunBuild(build.Name, build.SearchSubdir, build.Mode, repopulate);
            }
            catch (Exception ex)
            {
                lock (consoleLock)
                    Console.Error.WriteLine($"  ERROR: {build.Name}: {ex.GetType().Name}: {ex.Message}");
                result = new BuildResult(build.Name, 0, 0, Missing: true);
            }

            results.Add(result);

            // Stream per-build status as soon as it completes, so a hung worker doesn't
            // hide the progress of the others.
            lock (consoleLock)
            {
                if (result.Missing)
                {
                    Console.WriteLine($"  MISSING: {result.Name}");
                }
                else
                {
                    var detail = result.ArchivesExtracted > 0
                        ? $" ({result.ArchivesExtracted} archives extracted)"
                        : string.Empty;
                    Console.WriteLine($"  {result.Name}: {result.FileCount} files{detail}");
                }
            }
        });

        var totalBuilds = results.Count(r => !r.Missing);

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Done. {totalBuilds} builds in {stopwatch.Elapsed.TotalSeconds:F1}s");
        return results.Any(result => result.Missing) ? 1 : 0;
    }

    private static CliOptions ParseArguments(string[] args)
    {
        string? mediaRoot = null;
        string? researchRoot = null;
        string? sampleRoot = null;
        string? buildFilter = null;
        var repopulate = false;
        var selfTest = false;
        var help = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option))
                throw new ArgumentException($"Option specified more than once: {option}");

            switch (option.ToLowerInvariant())
            {
                case "--media-root":
                    mediaRoot = ReadOptionValue(args, ref i, option);
                    break;
                case "--research-root":
                    researchRoot = ReadOptionValue(args, ref i, option);
                    break;
                case "--sample-root":
                    sampleRoot = ReadOptionValue(args, ref i, option);
                    break;
                case "--build":
                    buildFilter = ReadOptionValue(args, ref i, option);
                    break;
                case "--repopulate":
                    repopulate = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        return new CliOptions(
            mediaRoot,
            researchRoot,
            sampleRoot,
            buildFilter,
            repopulate,
            selfTest,
            help);
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            throw new ArgumentException($"{option} requires a value.");

        return args[++index];
    }

    private sealed record CliOptions(
        string? MediaRoot,
        string? ResearchRoot,
        string? SampleRoot,
        string? BuildFilter,
        bool Repopulate,
        bool SelfTest,
        bool Help);

    static void PrintUsage()
    {
        Console.WriteLine("Usage: SampleGenerator --media-root <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --media-root <path>     Source media tree (or NEVERSOFT_MEDIA_ROOT). Required.");
        Console.WriteLine("  --research-root <path> Extracted-build cache (or NEVERSOFT_RESEARCH_ROOT).");
        Console.WriteLine("  --sample-root <path>   Generated sample tree (or NEVERSOFT_SAMPLE_ROOT).");
        Console.WriteLine("  --build <text>         Process only matching configured build names.");
        Console.WriteLine("  --repopulate           Delete and re-extract matching research-cache builds.");
        Console.WriteLine("  --self-test            Run path-containment checks without reading media.");
        Console.WriteLine("  --help, -h             Show this help.");
    }
}
