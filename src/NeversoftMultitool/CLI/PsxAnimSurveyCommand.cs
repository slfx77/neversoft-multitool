using System.CommandLine;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Walks a directory of <c>.psx</c> files and categorizes each by version
///     (0x03 / 0x04 / 0x06), hierarchy flag, and detected animation-table layout.
///     Writes a CSV detail report plus a console summary grouped by build (top-level
///     subdirectory under the input root). Used to scope per-game decomp work.
/// </summary>
public static class PsxAnimSurveyCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Directory to scan recursively (e.g. Sample/Builds)"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "CSV output path (default: TestOutput/psx_anim_survey.csv)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable per-file output"
        };

        var command = new Command("psx-anim-survey",
            "Survey PSX files across a corpus, grouping by version + animation-table layout");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var verbose = parseResult.GetValue(verboseOption);
            return Task.FromResult(Execute(input, output, verbose));
        });

        return command;
    }

    private static int Execute(string input, string? output, bool verbose)
    {
        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {Markup.Escape(input)}");
            return 1;
        }

        var rootFull = Path.GetFullPath(input);
        var files = Directory.GetFiles(rootFull, "*.psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AnsiConsole.MarkupLine($"Scanning [green]{files.Count}[/] PSX file(s) under {Markup.Escape(rootFull)}");

        var rows = new List<SurveyRow>(files.Count);
        foreach (var file in files)
        {
            var row = ProbeFile(file, rootFull);
            rows.Add(row);
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]{Markup.Escape(row.RelPath)}[/]  ver={row.VersionLabel}  hier={row.HasHierarchy}  " +
                    $"meshRev={row.MeshRevisionLabel}  bones={row.Bones}  layout={row.LayoutLabel}  " +
                    $"animRev={row.AnimationRevisionLabel}  runtime={row.RuntimeRevisionLabel}  " +
                    $"entries={row.EntriesRecovered}/{row.NumStreamsDecl}");
            }
        }

        var csvPath = output ?? Path.Combine("TestOutput", "psx_anim_survey.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
        WriteCsv(csvPath, rows);
        AnsiConsole.MarkupLine($"\nWrote CSV: [green]{Markup.Escape(csvPath)}[/]");

        PrintSummary(rows);
        return 0;
    }

    private static SurveyRow ProbeFile(string path, string rootFull)
    {
        var rel = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
        var build = ExtractBuild(rel);

        try
        {
            var data = File.ReadAllBytes(path);
            // Header-only parse skips the geometry/face decoder — ~10× faster
            // for the survey since we only need version, hierarchy flag, object
            // count, and mesh count.
            var psxFile = PsxMeshFile.ParseHeaderOnly(data);
            if (psxFile == null)
            {
                return new SurveyRow(build, rel, null, null, false, 0, 0, data.Length, 0,
                    null, null, 0, 0, null, "no mesh");
            }

            long meshBlockEnd = 0;
            try
            {
                meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
            }
            catch
            {
                /* leave 0 */
            }

            PsxAnimFile? animFile = null;
            string? error = null;
            try
            {
                animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return new SurveyRow(
                build, rel,
                psxFile.Version,
                psxFile.FormatRevision,
                psxFile.HasHierarchy,
                psxFile.Objects.Count,
                psxFile.Meshes.Count,
                data.Length,
                data.Length - meshBlockEnd,
                animFile?.Layout,
                animFile?.FormatRevision,
                animFile?.NumStreamsDeclared ?? 0,
                animFile?.Entries.Count ?? 0,
                animFile?.MinimumRuntimeRevision,
                error);
        }
        catch (Exception ex)
        {
            return new SurveyRow(build, rel, null, null, false, 0, 0, 0, 0, null, null, 0, 0, null, ex.Message);
        }
    }

    private static string ExtractBuild(string relPath)
    {
        var sep = relPath.IndexOf('/');
        return sep > 0 ? relPath[..sep] : relPath;
    }

    private static void WriteCsv(string path, IReadOnlyList<SurveyRow> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine(
            "build,relpath,version,mesh_revision,has_hierarchy,bones,meshes,file_size,post_mesh_size,layout,anim_revision,num_streams_declared,entries_recovered,runtime_revision,error");
        foreach (var r in rows)
        {
            writer.WriteLine(
                $"{Csv(r.Build)},{Csv(r.RelPath)},{r.VersionLabel},{r.MeshRevisionLabel},{r.HasHierarchy},{r.Bones},{r.Meshes}," +
                $"{r.FileSize},{r.PostMeshSize},{r.LayoutLabel},{r.AnimationRevisionLabel},{r.NumStreamsDecl}," +
                $"{r.EntriesRecovered},{r.RuntimeRevisionLabel},{Csv(r.Error)}");
        }

        static string Csv(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }
    }

    private static void PrintSummary(IReadOnlyList<SurveyRow> rows)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Per-build summary (files with recoverable animation entries)[/]");

        // Apocalypse v3 character files don't set HasHierarchy (no HIER chunk)
        // but DO have animation tables — HasHierarchy alone is too narrow, so
        // we count files where the parser recovered ≥1 entry.
        var groups = rows
            .GroupBy(r => r.Build)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var table = new Table()
            .AddColumn("Build")
            .AddColumn(new TableColumn("Total").RightAligned())
            .AddColumn(new TableColumn("Char(HIER)").RightAligned())
            .AddColumn(new TableColumn("WithAnims").RightAligned())
            .AddColumn(new TableColumn("v0x03").RightAligned())
            .AddColumn(new TableColumn("v0x04").RightAligned())
            .AddColumn(new TableColumn("v0x06").RightAligned())
            .AddColumn(new TableColumn("Direct").RightAligned())
            .AddColumn(new TableColumn("Mono").RightAligned())
            .AddColumn(new TableColumn("ExtSlot").RightAligned())
            .AddColumn(new TableColumn("ProtoSparse").RightAligned());

        foreach (var g in groups)
        {
            var withAnims = g.Where(r => r.EntriesRecovered > 0).ToList();
            var v3 = withAnims.Count(r => r.Version == 0x03);
            var v4 = withAnims.Count(r => r.Version == 0x04);
            var v6 = withAnims.Count(r => r.Version == 0x06);
            var direct = withAnims.Count(r => r.AnimationRevision == PsxAnimationFormatRevision.DirectMatrixV1);
            var mono = withAnims.Count(r => r.Layout == PsxAnimLayoutVariant.Monolithic);
            var extSlot = withAnims.Count(r => r.RuntimeRevision == PsxCharacterRuntimeRevision.ExtendedAnimSlots);
            var proto = withAnims.Count(r => r.Layout == PsxAnimLayoutVariant.PrototypeSparse);
            var hierCount = g.Count(r => r.HasHierarchy);

            table.AddRow(
                Markup.Escape(g.Key),
                g.Count().ToString(),
                hierCount.ToString(),
                withAnims.Count.ToString(),
                v3.ToString(),
                v4.ToString(),
                v6.ToString(),
                direct.ToString(),
                mono.ToString(),
                extSlot.ToString(),
                proto.ToString());
        }

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Distinct revision groupings (files with anim data)[/]");

        var combos = rows
            .Where(r => r.EntriesRecovered > 0)
            .GroupBy(r => (r.Build, r.MeshRevisionLabel, r.AnimationRevisionLabel, r.RuntimeRevisionLabel))
            .Select(g => (Combo: g.Key, Count: g.Count(), TotalEntries: g.Sum(r => r.EntriesRecovered)))
            .OrderBy(c => c.Combo.Build, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Combo.MeshRevisionLabel)
            .ThenBy(c => c.Combo.AnimationRevisionLabel);

        var comboTable = new Table()
            .AddColumn("Build")
            .AddColumn("MeshRev")
            .AddColumn("AnimRev")
            .AddColumn("Runtime")
            .AddColumn(new TableColumn("Files").RightAligned())
            .AddColumn(new TableColumn("TotalEntries").RightAligned());

        foreach (var (combo, count, totalEntries) in combos)
        {
            comboTable.AddRow(
                Markup.Escape(combo.Build),
                combo.MeshRevisionLabel,
                combo.AnimationRevisionLabel,
                combo.RuntimeRevisionLabel,
                count.ToString(),
                totalEntries.ToString("N0"));
        }

        AnsiConsole.Write(comboTable);
    }

    private sealed record SurveyRow(
        string Build,
        string RelPath,
        ushort? Version,
        PsxMeshFormatRevision? MeshRevision,
        bool HasHierarchy,
        int Bones,
        int Meshes,
        long FileSize,
        long PostMeshSize,
        PsxAnimLayoutVariant? Layout,
        PsxAnimationFormatRevision? AnimationRevision,
        int NumStreamsDecl,
        int EntriesRecovered,
        PsxCharacterRuntimeRevision? RuntimeRevision,
        string? Error)
    {
        public string VersionLabel => Version.HasValue ? $"0x{Version.Value:X2}" : "?";

        public string MeshRevisionLabel => MeshRevision?.ToString() ?? "?";

        public string AnimationRevisionLabel => AnimationRevision?.ToString() ?? LayoutLabel;

        public string RuntimeRevisionLabel => RuntimeRevision?.ToString() ?? "?";

        public string LayoutLabel => Layout?.ToString() ?? (Error == "no mesh" ? "NoMesh" :
            Error != null ? "Error" : "NoAnimBlock");
    }
}
