using System.CommandLine;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>Exports Downhill Jam GBA's polygon courses and collision to GLB.</summary>
public static class GbaDhjLevelCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Tony Hawk's Downhill Jam GBA ROM"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for course and collision GLBs"
        };
        var indexOption = new Option<int?>("--index")
        {
            Description = "Export only one zero-based course"
        };
        var noCollisionOption = new Option<bool>("--no-collision")
        {
            Description = "Skip the paired-edge and collision-polyline viewer GLBs"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "List every source bank and exported course"
        };

        var command = new Command(
            "gba-dhj-level",
            "Export Downhill Jam GBA polygon courses and collision to GLB");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(indexOption);
        command.Options.Add(noCollisionOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(indexOption),
                !parseResult.GetValue(noCollisionOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input,
        string? outputDir,
        int? selectedIndex,
        bool includeCollision,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(input);
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        return ExecuteRom(
            rom, input, outputDir, selectedIndex, includeCollision, verbose, cancellationToken);
    }

    /// <summary>
    ///     Shared entry point used by the generic <c>gba-level</c> command after
    ///     it identifies Visual Impact's BXS engine.
    /// </summary>
    internal static int ExecuteRom(
        byte[] rom,
        string input,
        string? outputDir,
        int? selectedIndex,
        bool includeCollision,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        var courses = GbaDhjCourse.FindCourses(rom);
        if (courses.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Downhill Jam course containers found[/] in this ROM");
            return 0;
        }

        if (selectedIndex is < 0 || selectedIndex >= courses.Count)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Course index must be between 0 and {courses.Count - 1}");
            return 1;
        }

        var dir = outputDir
                  ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gba-dhj-levels");
        var selection = selectedIndex.HasValue
            ? [courses[selectedIndex.Value]]
            : courses;
        var visualWritten = 0;
        var collisionWritten = 0;
        foreach (var course in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stem = $"course_{course.Index:D2}";
            var visual = GbaDhjCourseGeometryWriter.BuildVisual(rom, course, stem);
            var visualResult = Export(visual, dir, stem, cancellationToken);
            if (visualResult.OutputPaths.Count > 0)
                visualWritten++;

            ModelDocument? collision = null;
            if (includeCollision)
            {
                collision = GbaDhjCourseGeometryWriter.BuildCollision(
                    rom, course, stem + "_collision");
                var collisionResult = Export(
                    collision, dir, stem + "_collision", cancellationToken);
                if (collisionResult.OutputPaths.Count > 0)
                    collisionWritten++;
            }

            if (verbose)
            {
                var polylines = GbaDhjCourse.ReadCollisionPolylines(rom, course);
                var objects = GbaDhjCourse.ReadObjects(rom, course);
                var references = GbaDhjCourse.ReadChunkObjectReferences(rom, course)
                    .SelectMany(static reference =>
                        new[] { reference.FirstObjectIndex, reference.SecondObjectIndex })
                    .Count(static index => index != GbaDhjCourse.MissingObjectIndex);
                AnsiConsole.MarkupLine(
                    $"  {stem}.glb  header 0x{0x08000000 + course.HeaderOffset:X8}  "
                    + $"vertices {course.VertexCount}  faces {course.FaceCount}  "
                    + $"pages {course.TexturePageCount}  chunks {course.ChunkCount}  "
                    + $"collision lists {polylines.Length}  "
                    + $"objects {objects.Length} in {objects.Select(static placed => placed.Type)
                        .Distinct().Count()} types from {references} chunk references"
                    + (collision != null
                        ? $" / {collision.TriangleCount} display triangles"
                        : string.Empty));
            }
        }

        AnsiConsole.MarkupLine(
            $"Exported [green]{visualWritten}[/] Downhill Jam courses"
            + (includeCollision ? $" and [green]{collisionWritten}[/] collision views" : string.Empty)
            + $" to [green]{Markup.Escape(dir)}[/]");
        AnsiConsole.MarkupLine(
            "[grey]course_NN.glb contains every non-degenerate indexed visual triangle with exact "
            + "palette fills and texture pages; authored zero-area records are omitted. "
            + "course_NN_collision.glb contains every chunk-referenced sequential "
            + "collision polyline and each authored road edge as narrow viewer ribbons. When both edge "
            + "arrays have the same count, it also contains a point-paired road-envelope viewer proxy. Course 06 has "
            + "unequal edge counts (661/635), and the final bonus/test course stores one edge followed "
            + "by 0xCDCD, so neither receives an unproven road strip. These proxy triangles are not an "
            + "authored collision mesh.[/]");
        AnsiConsole.MarkupLine(
            "[grey]course_NN.glb also carries a separate placed_objects node holding one small marker per "
            + "16-byte placed-object record, at the record's authored world X/Y/Z in the course mesh's own "
            + "space, grouped into one primitive per raw type byte. The markers occupy their own node so the "
            + "course geometry above is unchanged; the format stores a point and a type, not a shape, and the "
            + "meshes the type ids select have not been located. Only bytes +0x00..+0x06 of each record are "
            + "authored - the loader zeroes the halfword at +0x08 and the rest is padding - and what an "
            + "individual type id denotes is not decoded, so primitives are named by the raw id.[/]");
        return 0;
    }

    private static MeshExportResult Export(
        ModelDocument document,
        string outputDir,
        string stem,
        CancellationToken cancellationToken) =>
        ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = outputDir,
            OutputStem = stem,
            Format = MeshOutputFormat.Glb,
            CancellationToken = cancellationToken
        });
}
