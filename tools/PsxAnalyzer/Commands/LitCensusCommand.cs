using System.CommandLine;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace PsxAnalyzer.Commands;

/// <summary>
///     Census of dynamically-lit faces (face flag bit 2, the lit primitive-type
///     selector) across a PSX corpus, one process for the whole tree. The PS1
///     engine lights a model's bit-2 faces from its normals via
///     M3dAsm_GetDynamicLighting (thps2-psx-proto M3D.cpp) when the model/item
///     lighting gate is set, REPLACING serialized face/RGBs colours — so the
///     converter must export those faces with neutral modulation. This census
///     sizes that change per build and per file class (level `_g` / objects
///     `_o` / library `_l` / bare character) and flags any gouraud lit faces
///     whose authored colours would be dropped.
/// </summary>
public static class LitCensusCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Directory to scan recursively for .psx files (e.g. Sample/Builds)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "List every file with lit faces"
        };

        var command = new Command("lit-census", "Tally dynamically-lit faces (flag bit 2) per build and file class");
        command.Arguments.Add(inputArgument);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var verbose = parseResult.GetValue(verboseOption);
            Run(input, verbose);
            return Task.FromResult(0);
        });

        return command;
    }

    private sealed class Bucket
    {
        public int Files;
        public int FilesWithLit;
        public long Faces;
        public long LitFaces;
        public long LitGouraud;
        public long LitEligibleMeshFaces;
    }

    private static void Run(string root, bool verbose)
    {
        var perBuildClass = new SortedDictionary<string, Bucket>(StringComparer.Ordinal);
        var litFiles = new List<(string Build, string File, int Faces, int Lit, int LitGouraud, bool Eligible)>();

        foreach (var path in Directory.EnumerateFiles(root, "*.psx", SearchOption.AllDirectories))
        {
            PsxMeshFile? psx;
            try
            {
                psx = PsxMeshFile.Parse(path);
            }
            catch
            {
                continue;
            }

            if (psx is null || psx.Meshes.Count == 0)
                continue;

            var build = RelativeBuild(root, path);
            var fileClass = Classify(Path.GetFileNameWithoutExtension(path));
            var key = $"{build} | v{psx.Version} {fileClass}";
            if (!perBuildClass.TryGetValue(key, out var bucket))
                perBuildClass[key] = bucket = new Bucket();

            bucket.Files++;
            var fileFaces = 0;
            var fileLit = 0;
            var fileLitGouraud = 0;
            var fileEligible = false;
            foreach (var mesh in psx.Meshes)
            {
                var eligible = mesh.UsesDynamicLighting;
                foreach (var face in mesh.Faces)
                {
                    fileFaces++;
                    if ((face.Flags & 0x0004) == 0)
                        continue;
                    fileLit++;
                    if (face.IsGouraud)
                        fileLitGouraud++;
                    if (eligible)
                    {
                        bucket.LitEligibleMeshFaces++;
                        fileEligible = true;
                    }
                }
            }

            bucket.Faces += fileFaces;
            bucket.LitFaces += fileLit;
            bucket.LitGouraud += fileLitGouraud;
            if (fileLit > 0)
            {
                bucket.FilesWithLit++;
                litFiles.Add((build, Path.GetFileName(path), fileFaces, fileLit, fileLitGouraud, fileEligible));
            }
        }

        var table = new Table();
        table.AddColumn("Build | version class");
        table.AddColumn(new TableColumn("Files").RightAligned());
        table.AddColumn(new TableColumn("w/lit").RightAligned());
        table.AddColumn(new TableColumn("Faces").RightAligned());
        table.AddColumn(new TableColumn("Lit").RightAligned());
        table.AddColumn(new TableColumn("LitGouraud").RightAligned());
        table.AddColumn(new TableColumn("LitEligible").RightAligned());
        foreach (var (key, b) in perBuildClass)
        {
            if (b.LitFaces == 0)
                continue;
            table.AddRow(
                key,
                b.Files.ToString(),
                b.FilesWithLit.ToString(),
                b.Faces.ToString(),
                b.LitFaces.ToString(),
                b.LitGouraud.ToString(),
                b.LitEligibleMeshFaces.ToString());
        }

        AnsiConsole.Write(table);

        var untouched = perBuildClass.Where(static kv => kv.Value.LitFaces == 0).ToList();
        AnsiConsole.MarkupLine(
            $"[grey]{untouched.Count} build/class groups have ZERO lit faces (unaffected): " +
            $"{string.Join("; ", untouched.Select(static kv => kv.Key))}[/]");

        if (verbose)
        {
            foreach (var (build, file, faces, lit, litGouraud, eligible) in litFiles
                         .OrderByDescending(static f => (double)f.Lit / Math.Max(f.Faces, 1)))
            {
                AnsiConsole.MarkupLine(
                    $"  {build,-44} {file,-16} lit={lit}/{faces} gouraudLit={litGouraud} eligible={eligible}");
            }
        }
    }

    private static string RelativeBuild(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];
        return first.EndsWith(".psx", StringComparison.OrdinalIgnoreCase) ? "(root)" : first;
    }

    private static string Classify(string stem)
    {
        if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
            return "level";
        if (stem.EndsWith("_l", StringComparison.OrdinalIgnoreCase))
            return "texlib";
        if (stem.EndsWith("_o", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_obj", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("obj", StringComparison.OrdinalIgnoreCase))
            return "objects";
        return "model";
    }
}
