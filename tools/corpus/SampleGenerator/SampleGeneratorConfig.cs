namespace SampleGenerator;

internal static class SampleGeneratorConfig
{
    internal const string LegacyExtractedDirName = "Extracted";
    internal const string InstalledDirName = "Installed";
    internal const string SystemDirName = "System";

    internal static string ResearchMedia { get; private set; } = string.Empty;

    internal static string ResearchBuilds { get; private set; } = string.Empty;

    internal static string SampleBuilds { get; private set; } = string.Empty;

    internal enum BuildMode { Iso, Directory, Msi }

    internal static void Configure(string? mediaRoot, string? researchRoot, string? sampleRoot)
    {
        mediaRoot = FirstNonEmpty(mediaRoot, Environment.GetEnvironmentVariable("NEVERSOFT_MEDIA_ROOT"));
        researchRoot = FirstNonEmpty(researchRoot, Environment.GetEnvironmentVariable("NEVERSOFT_RESEARCH_ROOT"));
        sampleRoot = FirstNonEmpty(sampleRoot, Environment.GetEnvironmentVariable("NEVERSOFT_SAMPLE_ROOT"));

        if (string.IsNullOrWhiteSpace(mediaRoot))
            throw new ArgumentException(
                "A media root is required. Pass --media-root or set NEVERSOFT_MEDIA_ROOT.");

        var resolvedMedia = NormalizeRoot(mediaRoot, "media");
        if (!Directory.Exists(resolvedMedia))
            throw new DirectoryNotFoundException($"Media root does not exist: {resolvedMedia}");

        var resolvedResearch = string.IsNullOrWhiteSpace(researchRoot)
            ? Path.Combine(Directory.GetParent(resolvedMedia)!.FullName, "Builds")
            : NormalizeRoot(researchRoot, "research");
        resolvedResearch = NormalizeRoot(resolvedResearch, "research");

        var resolvedSample = string.IsNullOrWhiteSpace(sampleRoot)
            ? Path.Combine(FindRepositoryRoot(), "Sample", "Builds")
            : NormalizeRoot(sampleRoot, "sample");
        resolvedSample = NormalizeRoot(resolvedSample, "sample");

        EnsureRootsDoNotOverlap("media", resolvedMedia, "research", resolvedResearch);
        EnsureRootsDoNotOverlap("media", resolvedMedia, "sample", resolvedSample);
        EnsureRootsDoNotOverlap("research", resolvedResearch, "sample", resolvedSample);

        ResearchMedia = resolvedMedia;
        ResearchBuilds = resolvedResearch;
        SampleBuilds = resolvedSample;

        SampleGeneratorPathSafety.RejectReparseTraversal(ResearchBuilds, ResearchBuilds);
        SampleGeneratorPathSafety.RejectReparseTraversal(SampleBuilds, SampleBuilds);
        Directory.CreateDirectory(ResearchBuilds);
        Directory.CreateDirectory(SampleBuilds);
        SampleGeneratorPathSafety.RejectReparseTraversal(ResearchBuilds, ResearchBuilds);
        SampleGeneratorPathSafety.RejectReparseTraversal(SampleBuilds, SampleBuilds);
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    private static string NormalizeRoot(string path, string label)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.GetParent(fullPath) == null)
            throw new ArgumentException($"The {label} root cannot be a filesystem root: {fullPath}");
        return fullPath;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NeversoftMultitool.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException(
            "Could not find the repository root; pass --sample-root or set NEVERSOFT_SAMPLE_ROOT.");
    }

    private static void EnsureRootsDoNotOverlap(
        string leftLabel,
        string left,
        string rightLabel,
        string right)
    {
        if (IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left))
        {
            throw new ArgumentException(
                $"The {leftLabel} and {rightLabel} roots must not overlap: '{left}' and '{right}'.");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.Equals(root, comparison) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    internal static readonly (string Name, string? SearchSubdir, BuildMode Mode)[] Builds =
    [
        // PS1 / Dreamcast / Xbox / PC — directory-based from the configured media collection
        ("Apocalypse (1998-11-17, PSX - Final)", null, BuildMode.Directory),
        ("Spider-Man (2000-2-4, PSX - Prototype)", "SPIDEY", BuildMode.Directory),
        ("Spider-Man (2000-2-18, PSX - Prototype)", null, BuildMode.Directory),
        ("Spider-Man (2000-4-29, PSX - Prototype)", null, BuildMode.Directory),
        ("Spider-Man (2000-6-12, PSX - Prototype)", null, BuildMode.Directory),
        ("Spider-Man (2000-9-1, PSX - Final)", null, BuildMode.Directory),
        ("Spider-Man (2001-2-14, DC - Prototype)", null, BuildMode.Directory),
        ("Spider-Man (2001-9-17, PC - Final)", null, BuildMode.Directory),
        ("Spider-Man 2 - Enter Electro (2001-8-14, PSX - Prototype)", null, BuildMode.Directory),
        ("Spider-Man 2 - Enter Electro (2001-8-15, PSX - Final)", null, BuildMode.Directory),
        ("Spider-Man 2 - Enter Electro (2001-9-28, PSX - Rev1)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater (1999-9-29, PSX - Final)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 2 (2000-6-2, PSX - Prototype)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)", null, BuildMode.Directory),

        // Late PS1 ports by other studios on the Neversoft data lineage:
        // THPS3 PS1 = Shaba Games (SLUS-01419, PVD 2001-10-03), THPS4 PS1 =
        // Vicarious Visions (SLUS-01485; the PVD year is mastered wrong,
        // "1902" — date taken from the EXE record). Both ship CD.HED+CD.WAD.
        ("Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 4 (2002-9-28, PSX - Final)", null, BuildMode.Directory),

        // N64 carts (.z64 big-endian ROMs, mirrored verbatim — no ROM
        // filesystem exists to extract; dates are the US release dates, the
        // ROM headers carry none). Edge of Reality ports of the THPS line;
        // Spider-Man N64 is the same lineage.
        ("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", null, BuildMode.Directory),
        ("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", null, BuildMode.Directory),
        ("Spider-Man (2000-11-21, N64 - Final)", null, BuildMode.Directory),

        // Disc images are discovered beneath media-root/{build name}/.
        ("Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)", null, BuildMode.Iso),
        ("Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)", null, BuildMode.Iso),
        ("Tony Hawk's Underground (2003-10-2, PS2 - Final)", null, BuildMode.Iso),
        ("Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)", null, BuildMode.Iso),
        ("Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", null, BuildMode.Iso),
        ("Tony Hawk's Project 8 (2006-9-21, PS2 - Final)", null, BuildMode.Iso),
        ("Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)", null, BuildMode.Iso),

        // GameCube — raw disc layout preserved from extracted media
        ("Tony Hawk's American Wasteland (2005-8-22, GC - Final)", null, BuildMode.Directory),

        // Xbox / PC — directory-based with archive extraction
        ("Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)", "data", BuildMode.Directory),
        ("Tony Hawks Underground 2 (2004-10-4, Windows - Final)", null, BuildMode.Msi),
        ("Tony Hawk's American Wasteland (2006-2-6, PC - Final)", null, BuildMode.Msi),
    ];
}
