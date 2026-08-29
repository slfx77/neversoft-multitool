using System.CommandLine;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Gba;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Dumps the per-level entity table at level-record <c>+0x150</c> as inspection
///     JSON. See <see cref="GbaLevelEntityTable" />.
/// </summary>
/// <remarks>
///     A separate verb rather than a flag on <c>gba-level</c>: that command iterates
///     the art scanner's DEDUPLICATED level list, and doing this from there would
///     silently drop 19 of the ROM's 510 entity records.
/// </remarks>
public static class GbaLevelEntitiesCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Vicarious Visions GBA Tony Hawk ROM"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output path for the inspection JSON"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "List each level's record and count"
        };

        var command = new Command(
            "gba-level-entities",
            "Inspect the per-level entity tables in a GBA ROM (structure only, not decoded)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(verboseOption)));
        });
        return command;
    }

    internal static int Execute(string input, string? outputPath, bool verbose)
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

        var records = GbaLevelEntityTable.FindLevelRecordOffsets(rom);
        if (records.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Vicarious Visions level table found in this ROM.[/]");
            return 0;
        }

        var names = GbaLevelCarver.ListLevels(rom);
        var levels = new List<GbaLevelEntityTableSnapshot>();
        var total = 0;

        for (var i = 0; i < records.Count; i++)
        {
            var entities = GbaLevelEntityTable.TryRead(rom, records[i]);
            if (entities == null) continue;
            total += entities.Count;

            // Variants past the art scanner's deduplicated list have no name of
            // their own; say so rather than borrowing the base level's.
            var name = i < names.Count ? names[i].Name : null;
            levels.Add(new GbaLevelEntityTableSnapshot(
                i,
                records[i],
                name,
                GbaLevelEntityTable.TableOffset(rom, records[i]),
                entities.Count,
                [
                    .. entities.Select(e => new GbaLevelEntitySnapshot(
                        e.WorldX, e.WorldY, e.CellX, e.CellY,
                        e.Field2, e.Field3, e.Field4, e.Field5, e.Field6, e.Field7))
                ]));

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  record {i,2} @ 0x{records[i]:X6}  {entities.Count,3} entities  "
                    + $"{Markup.Escape(name ?? "(variant)")}");
            }
        }

        var manifest = new GbaLevelEntityManifest(
            1,
            Path.GetFileName(input),
            rom.Length,
            GbaLevelEntityTable.TableField,
            GbaLevelEntityTable.RecordBytes,
            GbaLevelEntityTable.RawUnitsPerCell,
            records.Count,
            total,
            // The fields are read, not understood. Only worldX/worldY are
            // established (every record lands inside its own collision grid);
            // field2 is signed so it is a coordinate, fields 3-5 are unread
            // magnitudes, field6 is an id banded on decimal thousands, and
            // field7 is always a multiple of 0x1000 with five distinct values.
            "notDecoded",
            "notApplied",
            levels);

        var json = JsonSerializer.Serialize(manifest, GbaLevelEntityJsonContext.Default.GbaLevelEntityManifest);
        var target = outputPath ?? Path.Combine(
            "TestOutput", Path.GetFileNameWithoutExtension(input) + "-entities.json");
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(target, json);

        AnsiConsole.MarkupLine(
            $"Inspected {records.Count} level record(s), {total} entity record(s) "
            + $"→ {Markup.Escape(target)}");
        AnsiConsole.MarkupLine(
            "[grey]Structure only: the record fields are read, not decoded, and no "
            + "geometry is emitted from them.[/]");
        return 0;
    }
}
